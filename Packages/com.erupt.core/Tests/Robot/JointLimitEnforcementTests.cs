using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Trajectory;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// No path moves a joint past its limits. The robot is a small articulated chain built in
    /// code: two revolute arm joints (one of them with a +-2 pi range), a continuous arm joint, a
    /// hand, and a gripper made of a prismatic joint and a revolute pair in which one joint
    /// mimics the other. Every way of writing a joint is driven past a limit and checked: IK,
    /// jogging, both joint-state overloads, the initial pose, the held pose, trajectory
    /// playback, the ghost copy, the gripper width and the mimic follower.
    /// </summary>
    public class JointLimitEnforcementTests
    {
        private const float TwoPi = 2f * Mathf.PI;
        private const float J1Limit = 1f;          // j1: [-1, 1] rad
        private const float SlideUpper = 0.04f;    // slide: [0, 0.04] m
        private const float FingerUpper = 0.8f;    // finger: [0, 0.8] rad
        private const float MimicLower = -0.5f;    // finger_mimic: [-0.5, 0] rad, narrower than the driver's range

        private GameObject robot, ghostSource;
        private Transform hand;
        private RobotProfile profile;
        private RobotRegistry robots;
        private DirectArticulationIKController ik;
        private readonly Dictionary<string, ArticulationBody> bodies = new Dictionary<string, ArticulationBody>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profile = ChainProfile();
            robots = FakeRobots.Registry(new FakeRosBus());

            robot = new GameObject("robot");
            FakeRobots.Attach(robots, robot, "arm", "", profile);
            bodies.Clear();
            hand = BuildChain(robot, bodies);

            // The articulation exists in the physics scene after a step.
            yield return new WaitForFixedUpdate();
            yield return null;

            ik = Child(robot.transform, "ik").AddComponent<DirectArticulationIKController>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string ghost in new[] { "StartGhost", "GoalGhost" })
            {
                GameObject left = GameObject.Find(ghost);
                if (left != null) UnityEngine.Object.DestroyImmediate(left);
            }
            FakeRobots.DestroyAll(robots);
            if (robot != null) UnityEngine.Object.DestroyImmediate(robot);
            if (ghostSource != null) UnityEngine.Object.DestroyImmediate(ghostSource);
            UnityEngine.Object.DestroyImmediate(profile);
        }

        // ---------- the limit model the controller builds ----------

        [Test]
        public void TheControllersLimits_ComeFromTheProfile()
        {
            ik.Configure(robot.transform, hand);

            IJointLimits limits = ((IRobotModel)ik).Limits;
            Assert.IsTrue(limits.TryGet("j1", out JointLimit j1) && j1.HasLimits);
            Assert.AreEqual(J1Limit, j1.Upper, 1e-6f);
            Assert.IsTrue(limits.TryGet("j2", out JointLimit j2));
            Assert.AreEqual(TwoPi, j2.Upper, 1e-5f);
            Assert.IsTrue(limits.TryGet("j3", out JointLimit j3));
            Assert.IsFalse(j3.HasLimits, "Continuous.");
            Assert.IsTrue(limits.TryGet("finger_mimic", out JointLimit follower));
            Assert.AreEqual(MimicLower, follower.Lower, 1e-6f);

            Assert.IsTrue(ik.TryGetJointLink("j1", out Transform link1));
            Assert.AreSame(bodies["j1"].transform, link1);
            Assert.IsTrue(ik.TryGetJointLink("slide", out Transform slide));
            Assert.AreSame(bodies["slide"].transform, slide);
            Assert.IsFalse(ik.TryGetJointLink("not a joint", out _));
        }

        [Test]
        public void TheProfilesMargin_KeepsJointsShortOfTheirLimits()
        {
            profile.limitMarginRadians = 0.1f;
            profile.limitMarginMetres = 0.005f;
            ik.Configure(robot.transform, hand);

            ik.ApplyJointState(new[] { "j1", "slide" }, new[] { 5.0, 1.0 });

            Assert.AreEqual(J1Limit - 0.1f, Angle("j1"), 1e-5f);
            Assert.AreEqual(SlideUpper - 0.005f, Angle("slide"), 1e-6f);
        }

        // ---------- joint states ----------

        [Test]
        public void ApplyJointState_Doubles_ClampsAndReports()
        {
            ik.Configure(robot.transform, hand);
            LogAssert.Expect(LogType.Warning, new Regex("joint 'nope' not found"));

            JointWriteReport report = ik.ApplyJointState(
                new[] { "j1", "j2", "j3", "slide", "nope" },
                new[] { 5.0, 6.0, 4.5, 1.0, 1.0 });

            Assert.AreEqual(J1Limit, Angle("j1"), 1e-5f);
            Assert.AreEqual(6f, Angle("j2"), 1e-4f, "6 rad is inside +-2 pi: neither clamped nor wrapped.");
            Assert.AreEqual(4.5f, Angle("j3"), 1e-4f, "A continuous joint is not clamped, wrapped or frozen.");
            Assert.AreEqual(SlideUpper, Angle("slide"), 1e-6f);

            Assert.AreEqual(2, report.Clamped.Count);
            Assert.AreEqual("j1", report.Clamped[0].Joint);
            Assert.AreEqual(5f, report.Clamped[0].Requested, 1e-6f);
            Assert.AreEqual(J1Limit, report.Clamped[0].Applied, 1e-6f);
            Assert.AreEqual(4f, report.Clamped[0].Excess, 1e-5f);
            Assert.IsTrue(report.WasClamped("slide"));
            Assert.IsFalse(report.WasClamped("j2"));
            CollectionAssert.AreEqual(new[] { "nope" }, report.Unknown);
            Assert.IsFalse(report.IsClean);

            Assert.IsTrue(ik.ApplyJointState(new[] { "j1" }, new[] { 0.2 }).IsClean, "A write inside the limits reports nothing.");
            AssertEveryJointInsideItsLimits();
        }

        [Test]
        public void ApplyJointState_Floats_ClampsAndReports()
        {
            ik.Configure(robot.transform, hand);

            JointWriteReport report = ik.ApplyJointState(
                new List<string> { "j1", "j2", "slide" },
                new List<float> { -5f, -7f, -1f });

            Assert.AreEqual(-J1Limit, Angle("j1"), 1e-5f);
            Assert.AreEqual(-TwoPi, Angle("j2"), 1e-4f);
            Assert.AreEqual(0f, Angle("slide"), 1e-6f);
            Assert.AreEqual(3, report.Clamped.Count);
            AssertEveryJointInsideItsLimits();
        }

        [Test]
        public void AValueThatIsNotANumber_IsNotWritten()
        {
            ik.Configure(robot.transform, hand);
            ik.ApplyJointState(new[] { "j1", "j3" }, new[] { 0.4, 0.7 });

            JointWriteReport report = ik.ApplyJointState(
                new[] { "j1", "j3" }, new[] { double.NaN, double.PositiveInfinity });

            Assert.AreEqual(0.4f, Angle("j1"), 1e-5f);
            Assert.AreEqual(0.7f, Angle("j3"), 1e-5f, "Infinity on a joint without limits has nowhere to be clamped to.");
            Assert.AreEqual(2, report.Clamped.Count);
        }

        // ---------- initial pose ----------

        [Test]
        public void TheProfilesInitialPose_IsClamped()
        {
            profile.poses = new[]
            {
                new NamedPose
                {
                    name = "start",
                    joints = new[]
                    {
                        new JointValue { joint = "j1", value = 3f },
                        new JointValue { joint = "j2", value = -9f },
                        new JointValue { joint = "slide", value = 0.5f },
                    }
                }
            };
            profile.initialPose = "start";
            LogAssert.Expect(LogType.Warning, new Regex("initial pose is outside the joint limits"));

            ik.Configure(robot.transform, hand);

            Assert.AreEqual(J1Limit, Angle("j1"), 1e-5f);
            Assert.AreEqual(-TwoPi, Angle("j2"), 1e-4f);
            Assert.AreEqual(SlideUpper, Angle("slide"), 1e-6f);
        }

        [Test]
        public void TheControllersOwnInitialPose_IsClamped()
        {
            var pose = new[]
            {
                new DirectArticulationIKController.JointInitialPosition { name = "j1", positionRadians = -4f },
            };
            typeof(DirectArticulationIKController)
                .GetField("initialPose", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(ik, pose);
            LogAssert.Expect(LogType.Warning, new Regex("initial pose is outside the joint limits"));

            ik.Configure(robot.transform, hand);

            Assert.AreEqual(-J1Limit, Angle("j1"), 1e-5f);
        }

        // ---------- held pose ----------

        [UnityTest]
        public IEnumerator TheHeldPose_StaysInsideTheLimits_AfterAnOutOfLimitWrite()
        {
            ik.Configure(robot.transform, hand);
            ik.ApplyJointState(new[] { "j1" }, new[] { 5.0 });

            for (int i = 0; i < 4; i++) yield return new WaitForFixedUpdate();

            Assert.AreEqual(J1Limit, bodies["j1"].jointPosition[0], 1e-3f, "The pose re-applied every physics step is the clamped one.");
            AssertEveryJointInsideItsLimits(1e-3f);
        }

        [UnityTest]
        public IEnumerator TheHeldPose_PullsAJointBack_ThatSomethingElseMoved()
        {
            ik.Configure(robot.transform, hand);
            ik.ApplyJointState(new[] { "j1" }, new[] { 0.5 });

            // Stands in for anything that moves the body behind the controller's back.
            bodies["j1"].jointPosition = new ArticulationReducedSpace(3f);
            for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();

            Assert.AreEqual(0.5f, bodies["j1"].jointPosition[0], 1e-3f);
        }

        // ---------- jog ----------

        [Test]
        public void Jogging_StopsAtTheLimit_AndSaysWhichJoint()
        {
            ik.Configure(robot.transform, hand);

            InteractionRefusal refusal = ik.TryNudgeJoint(bodies["j1"], 5f);

            Assert.AreEqual(J1Limit, Angle("j1"), 1e-5f);
            Assert.IsTrue(refusal.IsRefused);
            Assert.AreEqual(RefusalCode.JointLimit, refusal.Code);
            Assert.AreSame(bodies["j1"].transform, refusal.Subject, "The refusal points at the link the joint moves.");
            StringAssert.Contains("j1", refusal.Reason);
            StringAssert.Contains("upper", refusal.Reason);

            refusal = ik.TryNudgeJoint(bodies["j1"], -9f);
            Assert.AreEqual(-J1Limit, Angle("j1"), 1e-5f);
            Assert.AreEqual(RefusalCode.JointLimit, refusal.Code);
            StringAssert.Contains("lower", refusal.Reason);

            Assert.IsFalse(ik.TryNudgeJoint(bodies["j1"], 0.3f).IsRefused, "Moving away from the limit is not refused.");
            Assert.AreEqual(-J1Limit + 0.3f, Angle("j1"), 1e-5f);

            ik.NudgeJoint(bodies["j1"], 50f);
            Assert.AreEqual(J1Limit, Angle("j1"), 1e-5f, "The plain nudge is clamped as well.");
        }

        [Test]
        public void Jogging_AContinuousJoint_IsNeverRefused()
        {
            ik.Configure(robot.transform, hand);

            Assert.IsFalse(ik.TryNudgeJoint(bodies["j3"], 2.5f).IsRefused);
            Assert.IsFalse(ik.TryNudgeJoint(bodies["j3"], 2f).IsRefused);

            Assert.AreEqual(4.5f, Angle("j3"), 1e-4f, "Past pi, and not stuck at zero.");
        }

        // ---------- IK ----------

        [Test]
        public void Ik_NeverLeavesTheLimits()
        {
            ik.Configure(robot.transform, hand);
            var random = new System.Random(7);

            for (int i = 0; i < 40; i++)
            {
                var target = new Vector3(
                    (float)(random.NextDouble() * 2.0 - 1.0),
                    (float)(random.NextDouble() * 2.0 - 1.0),
                    (float)(random.NextDouble() * 2.0 - 1.0));
                ik.TrySolveToTarget(target);
                AssertEveryJointInsideItsLimits();
            }
        }

        [Test]
        public void Ik_ShortOfTheTargetWithAJointAtItsLimit_IsAJointLimitRefusal()
        {
            ik.Configure(robot.transform, hand);

            // Dragging link2's pivot can only turn j1; this target needs 2 rad of it and it has 1.
            Vector3 target = WhereJ1WouldPutLink2(2f);
            InteractionRefusal refusal = InteractionRefusal.None;
            for (int frame = 0; frame < 6; frame++)
                refusal = ik.TrySolveJointToTarget(bodies["j2"], target);

            Assert.AreEqual(J1Limit, Mathf.Abs(Angle("j1")), 1e-4f, "j1 went as far as its limit and no further.");
            Assert.IsTrue(refusal.IsRefused);
            Assert.AreEqual(RefusalCode.JointLimit, refusal.Code);
            Assert.AreSame(bodies["j1"].transform, refusal.Subject);
            StringAssert.Contains("j1", refusal.Reason);
            AssertEveryJointInsideItsLimits();
        }

        [Test]
        public void Ik_ShortOfTheTargetWithNoJointAtALimit_IsOutOfReach()
        {
            ik.Configure(robot.transform, hand);

            // Straight ahead of the stretched arm, further than it is long.
            InteractionRefusal refusal = ik.TrySolveToTarget(hand.position + robot.transform.forward * 3f);

            Assert.IsTrue(refusal.IsRefused);
            Assert.AreEqual(RefusalCode.OutOfReach, refusal.Code);
            Assert.AreSame(hand, refusal.Subject, "Out of reach is about the thing being dragged, on this robot.");
            StringAssert.Contains("out of reach", refusal.Reason);
        }

        [Test]
        public void Ik_ThatReachesItsTarget_IsNotRefused()
        {
            ik.Configure(robot.transform, hand);
            ik.ApplyJointState(new[] { "j1", "j2", "j3" }, new[] { 0.3, 0.4, 0.2 });
            Vector3 reachable = hand.position;
            ik.ApplyJointState(new[] { "j1", "j2", "j3" }, new[] { 0.2, 0.3, 0.3 });
            Assert.Greater(Vector3.Distance(hand.position, reachable), 0.02f, "The test needs the hand to start away from the target.");

            InteractionRefusal refusal = InteractionRefusal.None;
            for (int frame = 0; frame < 6; frame++)
                refusal = ik.TrySolveToTarget(reachable);

            Assert.IsFalse(refusal.IsRefused, refusal.Reason);
            Assert.Less(Vector3.Distance(hand.position, reachable), 0.01f);
            Assert.AreEqual(RefusalCode.None, refusal.Code);
        }

        // ---------- velocity limit on user-driven motion ----------

        [UnityTest]
        public IEnumerator WithTheVelocityLimitOn_AJogMovesAtMostMaxVelocityTimesDeltaTime()
        {
            profile.limitUserVelocity = true;
            profile.joints[0].maxVelocity = 0.5f;   // j1
            ik.Configure(robot.transform, hand);
            yield return null;

            float budget = 0.5f * Time.deltaTime;
            InteractionRefusal refusal = ik.TryNudgeJoint(bodies["j1"], 0.9f);
            Assert.LessOrEqual(Mathf.Abs(Angle("j1")), budget + 1e-5f);
            Assert.IsFalse(refusal.IsRefused, "Slowed down is not refused.");

            ik.TryNudgeJoint(bodies["j1"], 0.9f);
            Assert.LessOrEqual(Mathf.Abs(Angle("j1")), budget + 1e-5f, "A second jog in the same frame shares the frame's budget.");

            // A joint without a velocity limit is not slowed.
            ik.TryNudgeJoint(bodies["j2"], 0.9f);
            Assert.AreEqual(0.9f, Angle("j2"), 1e-5f);
        }

        [UnityTest]
        public IEnumerator WithTheVelocityLimitOn_IkThatIsStillOnItsWay_IsNotOutOfReach()
        {
            profile.limitUserVelocity = true;
            profile.joints[0].maxVelocity = 0.2f;
            ik.Configure(robot.transform, hand);
            yield return null;

            // Reachable with half a radian of j1, which takes several frames at this speed.
            Vector3 target = WhereJ1WouldPutLink2(0.5f);

            float budget = 0.2f * Time.deltaTime;
            InteractionRefusal refusal = ik.TrySolveJointToTarget(bodies["j2"], target);

            Assert.LessOrEqual(Mathf.Abs(Angle("j1")), budget + 1e-5f, "One frame moves the joint at most max velocity x delta time.");
            Assert.Less(Mathf.Abs(Angle("j1")), 0.45f, "The test needs the frame to be shorter than the whole motion.");
            Assert.Greater(Vector3.Distance(bodies["j2"].transform.position, target), 0.01f, "The target was not reached in this frame.");
            Assert.IsFalse(refusal.IsRefused, "The target was missed only because of the velocity budget.");

            // The same drag without the velocity limit gets there, so only the budget was in the way.
            profile.limitUserVelocity = false;
            ik.Configure(robot.transform, hand);
            for (int frame = 0; frame < 6; frame++)
                refusal = ik.TrySolveJointToTarget(bodies["j2"], target);
            Assert.Less(Vector3.Distance(bodies["j2"].transform.position, target), 0.01f);
            Assert.IsFalse(refusal.IsRefused);
        }

        [UnityTest]
        public IEnumerator WithTheVelocityLimitOn_JointStatesAndPlaybackAreNotSlowed()
        {
            profile.limitUserVelocity = true;
            profile.joints[0].maxVelocity = 0.01f;
            ik.Configure(robot.transform, hand);
            yield return null;

            ik.ApplyJointState(new[] { "j1" }, new[] { 0.9 });

            Assert.AreEqual(0.9f, Angle("j1"), 1e-5f, "Mirrored states and trajectory playback write through ApplyJointState.");
        }

        [Test]
        public void WithTheVelocityLimitOff_UserMotionIsNotSlowed()
        {
            profile.joints[0].maxVelocity = 0.01f;
            ik.Configure(robot.transform, hand);

            ik.TryNudgeJoint(bodies["j1"], 0.9f);

            Assert.AreEqual(0.9f, Angle("j1"), 1e-5f, "The velocity limit is opt-in per profile.");
        }

        // ---------- trajectory playback ----------

        [UnityTest]
        public IEnumerator TrajectoryPlayback_PlaysClamped()
        {
            ik.Configure(robot.transform, hand);
            var player = Child(robot.transform, "player").AddComponent<JointTrajectoryPlayer>();
            var trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "j1", "slide" },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { 0.0, 0.0 } },
                    new JointTrajectoryPointMsg { positions = new[] { 3.0, 0.3 }, time_from_start = new DurationMsg(0, 150_000_000) },
                }
            };

            player.StartReplay(trajectory);
            float highest = 0f;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!player.HasFinishedOneLoop() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                AssertEveryJointInsideItsLimits();
                highest = Mathf.Max(highest, Angle("j1"));
            }

            Assert.IsTrue(player.HasFinishedOneLoop());
            Assert.AreEqual(J1Limit, highest, 1e-4f, "The preview ran up to the limit and stayed there.");

            player.StopReplay();
            for (int frame = 0; frame < 20 && Mathf.Abs(Angle("j1")) > 1e-5f; frame++) yield return null;
            Assert.AreEqual(0f, Angle("j1"), 1e-5f, "The pose from before the preview is restored.");
        }

        // ---------- gripper ----------

        [Test]
        public void TheGripperWidth_CannotPushTheDriverPastItsLimit()
        {
            // A gripper description whose closed position is outside the driver joint's range.
            profile.gripper.closed = 1.2f;
            ik.Configure(robot.transform, hand);

            ik.SetGripperWidth(0f);

            Assert.AreEqual(FingerUpper, Angle("finger"), 1e-5f);
            AssertEveryJointInsideItsLimits();
        }

        [Test]
        public void AMimicFollower_IsClampedToItsOwnLimits()
        {
            ik.Configure(robot.transform, hand);

            // The follower would mirror the driver to -0.8, but its own range ends at -0.5.
            ik.ApplyJointState(new[] { "finger" }, new[] { (double)FingerUpper });
            Assert.AreEqual(FingerUpper, Angle("finger"), 1e-5f);
            Assert.AreEqual(MimicLower, Angle("finger_mimic"), 1e-5f);

            ik.SetGripperWidth(0f);
            Assert.AreEqual(MimicLower, Angle("finger_mimic"), 1e-5f, "The same through the gripper width.");

            // A state that names only the follower moves the driver to match, inside its limits.
            ik.ApplyJointState(new[] { "finger_mimic" }, new[] { -3.0 });
            Assert.AreEqual(MimicLower, Angle("finger_mimic"), 1e-5f);
            Assert.AreEqual(-MimicLower, Angle("finger"), 1e-5f);
            AssertEveryJointInsideItsLimits();
        }

        [UnityTest]
        public IEnumerator GripperJoints_AreHeldInsideTheirLimits()
        {
            ik.Configure(robot.transform, hand);
            ik.ApplyJointState(new[] { "slide", "finger" }, new[] { 9.0, 9.0 });

            for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();

            Assert.AreEqual(SlideUpper, bodies["slide"].jointPosition[0], 1e-4f);
            Assert.AreEqual(FingerUpper, bodies["finger"].jointPosition[0], 1e-3f);
        }

        // ---------- ghosts ----------

        [UnityTest]
        public IEnumerator TheGhost_IsPosedThroughTheSameLimits()
        {
            ghostSource = new GameObject("ghost source");
            BuildChain(ghostSource, new Dictionary<string, ArticulationBody>());
            ghostSource.SetActive(false);
            profile.ghostPrefab = ghostSource;

            ik.Configure(robot.transform, hand);
            var ghosts = Child(robot.transform, "ghosts").AddComponent<RobotGhosts>();
            ik.ApplyJointState(new[] { "j1", "j3" }, new[] { 0.5, 1.5 });

            ghosts.SpawnGoalGhost();
            Assert.IsNotNull(ghosts.GoalGhost);
            // The ghost's articulation exists in the physics scene after a step.
            yield return new WaitForFixedUpdate();
            yield return null;

            ArticulationBody ghostJ1 = ghosts.GoalGhost.transform.Find("link1").GetComponent<ArticulationBody>();
            ArticulationBody ghostJ3 = ghosts.GoalGhost.transform.Find("link1/link2/link3").GetComponent<ArticulationBody>();

            ghosts.UpdateGoalGhost();
            Assert.AreEqual(0.5f, ghostJ1.jointPosition[0], 1e-4f, "The ghost copies the robot's pose.");
            Assert.AreEqual(1.5f, ghostJ3.jointPosition[0], 1e-4f);

            // A source joint outside its limits (moved behind the controller's back) does not
            // take the ghost with it.
            bodies["j1"].jointPosition = new ArticulationReducedSpace(3f);
            bodies["j3"].jointPosition = new ArticulationReducedSpace(4.5f);
            ghosts.UpdateGoalGhost();

            Assert.AreEqual(J1Limit, ghostJ1.jointPosition[0], 1e-4f, "The ghost joint is clamped to the robot's limit.");
            Assert.AreEqual(4.5f, ghostJ3.jointPosition[0], 1e-4f, "The continuous joint is copied as it is.");

            ghosts.ClearGhosts();
        }

        [UnityTest]
        public IEnumerator APlanPreview_PlaysOnACopy_AndTheRobotStaysWhereItIs()
        {
            ghostSource = new GameObject("ghost source");
            BuildChain(ghostSource, new Dictionary<string, ArticulationBody>());
            ghostSource.SetActive(false);
            profile.ghostPrefab = ghostSource;

            ik.Configure(robot.transform, hand);
            var ghosts = Child(robot.transform, "ghosts").AddComponent<RobotGhosts>();
            ik.ApplyJointState(new[] { "j1", "j3" }, new[] { 0.1, 0.2 });
            Assert.IsFalse(ghosts.IsPreviewPlaying);

            // A plan from where the robot is to a pose whose j1 is outside the limit.
            var plan = new JointTrajectoryMsg
            {
                joint_names = new[] { "j1", "j3" },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { 0.1, 0.2 }, time_from_start = new DurationMsg(0, 0) },
                    new JointTrajectoryPointMsg { positions = new[] { 3.0, 1.5 }, time_from_start = new DurationMsg(0, 200000000) }
                }
            };
            Assert.IsTrue(ghosts.PlayPreview(plan));
            Assert.IsTrue(ghosts.IsPreviewPlaying);
            GameObject copy = ghosts.PreviewGhost;
            Assert.IsNotNull(copy);
            Assert.AreEqual(ghosts.previewGhostColor, copy.GetComponent<TranslucentOverride>().overlayColor, "The preview's copy has its own colour.");
            Assert.Greater(ghosts.previewGhostColor.b, ghosts.previewGhostColor.r, "Blue.");
            Assert.Greater(ghosts.previewGhostColor.b, ghosts.previewGhostColor.g);

            ArticulationBody copyJ1 = copy.transform.Find("link1").GetComponent<ArticulationBody>();
            ArticulationBody copyJ3 = copy.transform.Find("link1/link2/link3").GetComponent<ArticulationBody>();

            // It plays in a loop: wait until the copy has been at the end of the plan.
            bool reachedTheEnd = false;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!reachedTheEnd && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                reachedTheEnd = Mathf.Abs(copyJ1.jointPosition[0] - J1Limit) < 1e-3f && Mathf.Abs(copyJ3.jointPosition[0] - 1.5f) < 1e-3f;
                Assert.AreEqual(0.1f, Angle("j1"), 1e-4f, "The robot itself does not move during a preview.");
                Assert.AreEqual(0.2f, Angle("j3"), 1e-4f);
            }
            Assert.IsTrue(reachedTheEnd, "The copy plays the plan to its end, with j1 held at the robot's limit.");

            // And again from the start: the preview keeps playing until it is stopped.
            bool backAtTheStart = false;
            deadline = Time.realtimeSinceStartup + 5f;
            while (!backAtTheStart && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                backAtTheStart = copyJ1.jointPosition[0] < 0.5f;
            }
            Assert.IsTrue(backAtTheStart, "The preview loops.");

            ghosts.StopPreview();
            Assert.IsFalse(ghosts.IsPreviewPlaying);
            Assert.IsNull(ghosts.PreviewGhost);
            yield return null;
            Assert.IsTrue(copy == null, "Stopping the preview removes the copy.");
            Assert.AreEqual(0.1f, Angle("j1"), 1e-4f, "The robot is where it was.");
            Assert.AreEqual(0.2f, Angle("j3"), 1e-4f);

            // A second preview right after a stop plays (it does not wait for the first to let go).
            Assert.IsTrue(ghosts.PlayPreview(plan));
            Assert.IsNotNull(ghosts.PreviewGhost);
            ghosts.StopPreview();
            yield return null;
        }

        [Test]
        public void WithoutAGhostPrefab_ThereIsNoCopyToPreviewOn()
        {
            profile.ghostPrefab = null;
            ik.Configure(robot.transform, hand);
            var ghosts = Child(robot.transform, "ghosts").AddComponent<RobotGhosts>();
            Assert.IsFalse(ghosts.CanShowGhosts);
            Assert.IsFalse(ghosts.PlayPreview(new JointTrajectoryMsg { joint_names = new[] { "j1" }, points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.0 } } } }));
            Assert.IsFalse(ghosts.IsPreviewPlaying);
        }

        // ---------- a robot without a profile ----------

        [Test]
        public void WithoutAProfile_TheImportedDriveLimitsApply()
        {
            ik.Configure(robot.transform, hand, null);

            IJointLimits limits = ((IRobotModel)ik).Limits;
            Assert.IsTrue(limits.TryGet("j1", out JointLimit j1) && j1.HasLimits);
            Assert.AreEqual(J1Limit, j1.Upper, 1e-4f, "Degrees on the drive, radians in the model, same sign.");
            Assert.AreEqual(-J1Limit, j1.Lower, 1e-4f);
            Assert.IsTrue(limits.TryGet("j3", out JointLimit j3));
            Assert.IsFalse(j3.HasLimits, "A joint imported free (continuous) has no limits.");
            Assert.IsTrue(limits.TryGet("slide", out JointLimit slide) && slide.HasLimits);
            Assert.AreEqual(SlideUpper, slide.Upper, 1e-6f);

            ik.ApplyJointState(new[] { "j1", "j3", "slide" }, new[] { 5.0, 4.5, 1.0 });

            Assert.AreEqual(J1Limit, Angle("j1"), 1e-4f);
            Assert.AreEqual(4.5f, Angle("j3"), 1e-4f, "The continuous joint is not frozen at zero.");
            Assert.AreEqual(SlideUpper, Angle("slide"), 1e-6f);

            Assert.AreEqual(RefusalCode.JointLimit, ik.TryNudgeJoint(bodies["j1"], 1f).Code);
            Assert.IsFalse(ik.TryNudgeJoint(bodies["j3"], -1f).IsRefused);
            Assert.AreEqual(3.5f, Angle("j3"), 1e-4f);
        }

        [Test]
        public void WithoutAProfile_ConfiguringAgain_KeepsTheImportedLimits()
        {
            // The first Configure frees the joint locks; the limits must not be re-read from that state.
            ik.Configure(robot.transform, hand, null);
            Assert.AreEqual(ArticulationDofLock.FreeMotion, bodies["j1"].twistLock, "The physics limits are off; the software clamp is the limit.");

            ik.Configure(robot.transform, hand, null);
            ik.ApplyJointState(new[] { "j1" }, new[] { -5.0 });

            Assert.AreEqual(-J1Limit, Angle("j1"), 1e-4f);
        }

        // ---------- helpers ----------

        // Where link2's pivot is when j1 alone is turned by this much from zero (the other
        // joints at zero), read off the joint's own axis.
        private Vector3 WhereJ1WouldPutLink2(float radians)
        {
            ArticulationBody j1 = bodies["j1"];
            Vector3 pivot = j1.transform.TransformPoint(j1.anchorPosition);
            Vector3 axis = j1.transform.TransformDirection(j1.anchorRotation * Vector3.right);
            Vector3 arm = bodies["j2"].transform.position - pivot;
            return pivot + Quaternion.AngleAxis(radians * Mathf.Rad2Deg, axis) * arm;
        }

        private float Angle(string jointName)
        {
            Assert.IsTrue(ik.TryGetJointAngle(jointName, out float position), $"no joint '{jointName}'");
            return position;
        }

        // Reads the bodies themselves, not the controller's idea of where they are.
        private void AssertEveryJointInsideItsLimits(float tolerance = 1e-4f)
        {
            Assert.That(bodies["j1"].jointPosition[0], Is.InRange(-J1Limit - tolerance, J1Limit + tolerance), "j1");
            Assert.That(bodies["j2"].jointPosition[0], Is.InRange(-TwoPi - tolerance, TwoPi + tolerance), "j2");
            Assert.That(bodies["slide"].jointPosition[0], Is.InRange(-tolerance, SlideUpper + tolerance), "slide");
            Assert.That(bodies["finger"].jointPosition[0], Is.InRange(-tolerance, FingerUpper + tolerance), "finger");
            Assert.That(bodies["finger_mimic"].jointPosition[0], Is.InRange(MimicLower - tolerance, tolerance), "finger_mimic");
        }

        private static RobotProfile ChainProfile()
        {
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.baseLink = "robot";
            profile.endEffectorLink = "hand";
            profile.joints = new[]
            {
                new JointSpec { name = "j1", childLink = "link1", role = JointRole.Arm, urdfLower = -J1Limit, urdfUpper = J1Limit },
                new JointSpec { name = "j2", childLink = "link2", role = JointRole.Arm, urdfLower = -TwoPi, urdfUpper = TwoPi },
                new JointSpec { name = "j3", childLink = "link3", role = JointRole.Arm, kind = JointKind.Continuous },
                new JointSpec { name = "slide", childLink = "slider", role = JointRole.Gripper, kind = JointKind.Prismatic, urdfLower = 0f, urdfUpper = SlideUpper },
                new JointSpec { name = "finger", childLink = "knuckle", role = JointRole.Gripper, urdfLower = 0f, urdfUpper = FingerUpper },
                new JointSpec
                {
                    name = "finger_mimic", childLink = "knuckle_mimic", role = JointRole.Gripper, urdfLower = MimicLower, urdfUpper = 0f,
                    mimic = new MimicSpec { leader = "finger", multiplier = -1f }
                },
            };
            profile.gripper = new GripperSpec
            {
                kind = GripperKind.Revolute, driverJoint = "finger", open = 0f, closed = FingerUpper, openWidthMetres = 0.08f, speed = 0.1f
            };
            return profile;
        }

        // A planar arm: the links run along +Z and the joints turn about the articulation's
        // default joint axis (Y), with the gripper joints on the hand. The drive limits and
        // locks are set the way the URDF importer sets them.
        private static Transform BuildChain(GameObject root, Dictionary<string, ArticulationBody> bodies)
        {
            root.AddComponent<ArticulationBody>();

            GameObject link1 = Child(root.transform, "link1", new Vector3(0f, 0.1f, 0f));
            bodies["j1"] = AddUrdfJoint(link1, "UrdfJointRevolute", "j1", ArticulationJointType.RevoluteJoint,
                -J1Limit * Mathf.Rad2Deg, J1Limit * Mathf.Rad2Deg, limited: true);
            GameObject link2 = Child(link1.transform, "link2", new Vector3(0f, 0f, 0.3f));
            bodies["j2"] = AddUrdfJoint(link2, "UrdfJointRevolute", "j2", ArticulationJointType.RevoluteJoint, -360f, 360f, limited: true);
            GameObject link3 = Child(link2.transform, "link3", new Vector3(0f, 0f, 0.3f));
            bodies["j3"] = AddUrdfJoint(link3, "UrdfJointContinuous", "j3", ArticulationJointType.RevoluteJoint, 0f, 0f, limited: false);

            GameObject handObject = Child(link3.transform, "hand", new Vector3(0f, 0f, 0.2f));
            handObject.AddComponent<ArticulationBody>().jointType = ArticulationJointType.FixedJoint;

            bodies["slide"] = AddUrdfJoint(Child(handObject.transform, "slider"), "UrdfJointPrismatic", "slide",
                ArticulationJointType.PrismaticJoint, 0f, SlideUpper, limited: true);
            bodies["finger"] = AddUrdfJoint(Child(handObject.transform, "knuckle"), "UrdfJointRevolute", "finger",
                ArticulationJointType.RevoluteJoint, 0f, FingerUpper * Mathf.Rad2Deg, limited: true);
            bodies["finger_mimic"] = AddUrdfJoint(Child(handObject.transform, "knuckle_mimic"), "UrdfJointRevolute", "finger_mimic",
                ArticulationJointType.RevoluteJoint, MimicLower * Mathf.Rad2Deg, 0f, limited: true);

            return handObject.transform;
        }

        private static GameObject Child(Transform parent, string name, Vector3 localPosition = default)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go;
        }

        // The controller recognises URDF joints by type name, without referencing the importer;
        // the test builds them the same way.
        private static ArticulationBody AddUrdfJoint(GameObject go, string typeName, string jointName,
            ArticulationJointType jointType, float lowerLimit, float upperLimit, bool limited)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Unity.Robotics.UrdfImporter." + typeName))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, $"URDF importer type {typeName} not found");
            Component joint = go.AddComponent(type);
            type.GetField("jointName").SetValue(joint, jointName);

            ArticulationBody body = go.GetComponent<ArticulationBody>();
            body.jointType = jointType;
            ArticulationDofLock motion = limited ? ArticulationDofLock.LimitedMotion : ArticulationDofLock.FreeMotion;
            if (jointType == ArticulationJointType.RevoluteJoint) body.twistLock = motion;
            else body.linearLockX = motion;
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = lowerLimit;
            drive.upperLimit = upperLimit;
            body.xDrive = drive;
            return body;
        }
    }
}
