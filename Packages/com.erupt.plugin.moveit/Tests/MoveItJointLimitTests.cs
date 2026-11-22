using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>
    /// MoveIt data against the robot's joint limits: a plan that leaves them is flagged when it
    /// arrives, previews clamped, and is refused on execute unless the plugin allows it; joint
    /// states mirrored from a robot outside the model's limits are clamped and raise one
    /// mismatch refusal, not one per message.
    /// </summary>
    public class MoveItJointLimitTests
    {
        private const float TwoPi = 2f * Mathf.PI;
        private const string ControllerTopic = "/arm_controller/joint_trajectory";

        private FakeRosBus bus;
        private FakeRobotModel robot;
        private RobotProfile profile;
        private RobotRegistry robots;
        private GameObject pluginGo, routerGo, linkGo;
        private MoveItPlugin plugin;
        private InteractionRouter router;
        private readonly List<InteractionRefusal> refusals = new List<InteractionRefusal>();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            linkGo = new GameObject("link1");
            // joint1 is an ordinary limited joint; joint2 has the +-2 pi range of a UR joint.
            robot = new FakeRobotModel("joint1", "joint2").WithLimits(
                new JointLimit("joint1", JointKind.Revolute, -1f, 1f),
                new JointLimit("joint2", JointKind.Revolute, -TwoPi, TwoPi));
            robot.JointLinks["joint1"] = linkGo.transform;

            profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.armGroup = "arm";
            profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
            robots = FakeRobots.Single(bus, robot, "", profile);
            robots.Active.gameObject.AddComponent<JointTrajectoryPlayer>();

            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;

            pluginGo = new GameObject("moveit");
            pluginGo.SetActive(false);
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            ((IEruptPlugin)plugin).OnRegister(new EruptContext
            {
                Ros = bus, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack(), Router = router
            });
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();
        }

        [TearDown]
        public void TearDown()
        {
            ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(linkGo);
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(profile);
        }

        // ---------- plans ----------

        [Test]
        public void APlanInsideTheLimits_IsNotFlagged_AndExecutes()
        {
            // 6 rad on joint2 is inside +-2 pi.
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 0.9, 6.0 });

            Assert.IsFalse(plan.HasLimitViolations);
            Assert.IsEmpty(refusals);

            var statuses = new List<ExecutionStatus>();
            plugin.Execute(plan, statuses.Add);

            Assert.AreEqual(1, bus.CountOn(ControllerTopic));
            Assert.AreEqual(ExecutionPhase.Executing, statuses.Last().Phase);
        }

        [Test]
        public void APlanOutsideTheLimits_IsFlaggedWhenItArrives_AndTheUserIsToldWhichJoint()
        {
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.4, 7.0 });

            Assert.IsTrue(plan.HasLimitViolations);
            CollectionAssert.AreEquivalent(new[] { "joint1", "joint2" }, plan.LimitViolations.Select(v => v.Joint));
            JointLimitViolation joint1 = plan.LimitViolations.First(v => v.Joint == "joint1");
            Assert.AreEqual(0.4f, joint1.Excess, 1e-5f);
            Assert.AreEqual(1, joint1.Point);
            Assert.AreEqual(7f - TwoPi, plan.LimitViolations.First(v => v.Joint == "joint2").Excess, 1e-5f);

            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlanOutsideLimits, refusals[0].Code);
            StringAssert.Contains("joint2", refusals[0].Reason, "The joint that is furthest out is named.");
            StringAssert.Contains("will not be executed", refusals[0].Reason);
        }

        [Test]
        public void ThePlanRefusal_PointsAtTheWorstJointsLink()
        {
            Plan(new[] { 0.0, 0.0 }, new[] { 1.9, 0.0 });

            Assert.AreEqual(1, refusals.Count);
            Assert.AreSame(linkGo.transform, refusals[0].Subject);
        }

        [Test]
        public void RoundingPastALimit_IsNotAViolation()
        {
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.0005, 0.0 });

            Assert.IsFalse(plan.HasLimitViolations, "Half a milliradian is inside the default tolerance.");
        }

        [UnityTest]
        public IEnumerator APlanOutsideTheLimits_PreviewsClamped()
        {
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.4, 0.0 });
            Assert.IsTrue(plugin.IsPreviewing, "A flagged plan is still previewed.");

            // The plan's own request reaches 1.4; the robot must never follow it past 1.
            float highest = 0f;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!robot.Applied.Any(a => a.positions[0] > 1.3) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                highest = Mathf.Max(highest, robot.Positions[0]);
                Assert.LessOrEqual(robot.Positions[0], 1f + 1e-6f, "The robot never shows a pose outside its limits.");
            }
            Assert.IsTrue(robot.Applied.Any(a => a.positions[0] > 1.3), "The preview played the plan to its end.");
            Assert.AreEqual(1f, highest, 1e-6f, "The preview ran up to the limit and stopped there.");

            plugin.StopPreview();
            yield return null;
        }

        [Test]
        public void ExecutingAPlanOutsideTheLimits_IsRefused()
        {
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.4, 0.0 });
            refusals.Clear();
            router.RefusalCoalesceSeconds = 0f;

            var statuses = new List<ExecutionStatus>();
            plugin.Execute(plan, statuses.Add);

            Assert.AreEqual(0, bus.CountOn(ControllerTopic), "Nothing reaches the controller.");
            Assert.AreEqual(1, statuses.Count);
            Assert.AreEqual(ExecutionPhase.Refused, statuses[0].Phase);
            Assert.IsTrue(statuses[0].IsTerminal);
            StringAssert.Contains("joint1", statuses[0].Message);
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlanOutsideLimits, refusals[0].Code);
            StringAssert.StartsWith("Not executed", refusals[0].Reason);
            Assert.IsFalse(plugin.Client.IsMirroring, "A refused execution does not start mirroring.");
        }

        [Test]
        public void TheClientItself_DoesNotPublishAPlanOutsideTheLimits()
        {
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.4, 0.0 });
            LogAssert.Expect(LogType.Warning, new Regex("Not executed: the plan is outside the joint limits"));

            Assert.IsFalse(plugin.Client.Execute(plan));

            Assert.AreEqual(0, bus.CountOn(ControllerTopic));
        }

        [Test]
        public void WithTheOptOut_APlanOutsideTheLimitsExecutes()
        {
            plugin.AllowExecutionOutsideLimits = true;
            PlanResult plan = Plan(new[] { 0.0, 0.0 }, new[] { 1.4, 0.0 });

            Assert.IsTrue(plan.HasLimitViolations, "Still flagged.");
            StringAssert.DoesNotContain("will not be executed", refusals[0].Reason);

            var statuses = new List<ExecutionStatus>();
            plugin.Execute(plan, statuses.Add);

            Assert.AreEqual(1, bus.CountOn(ControllerTopic));
            Assert.AreEqual(ExecutionPhase.Executing, statuses.Last().Phase);
        }

        [Test]
        public void TheOptOut_IsOffByDefault()
        {
            Assert.IsFalse(plugin.AllowExecutionOutsideLimits);
            Assert.IsFalse(plugin.Client.AllowExecutionOutsideLimits);
            Assert.AreEqual(JointLimitTolerance.DefaultRadians, plugin.LimitTolerance.radians);
            Assert.AreEqual(JointLimitTolerance.DefaultMetres, plugin.LimitTolerance.metres);
        }

        // ---------- mirrored joint states ----------

        [Test]
        public void MirroredJointStates_OutsideTheModel_AreClamped_AndRaiseOneMismatch()
        {
            plugin.Client.SetMirroring(true);
            router.RefusalCoalesceSeconds = 0f;   // what is counted is the monitor's rate limit, not the router's

            for (int message = 0; message < 200; message++)
                bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "joint1", "joint2" }, position = new[] { 1.3, 0.5 } });

            Assert.AreEqual(1f, robot.Positions[0], 1e-6f, "The mirrored robot stays inside its limits.");
            Assert.AreEqual(0.5f, robot.Positions[1], 1e-6f);
            Assert.AreEqual(1, refusals.Count, "Two hundred joint states, one refusal.");
            Assert.AreEqual(RefusalCode.LimitMismatch, refusals[0].Code);
            Assert.AreSame(linkGo.transform, refusals[0].Subject);
            StringAssert.Contains("joint1", refusals[0].Reason);
            StringAssert.Contains("1.3", refusals[0].Reason);
        }

        [Test]
        public void EachJointOutsideTheModel_GetsItsOwnMismatch()
        {
            plugin.Client.SetMirroring(true);
            router.RefusalCoalesceSeconds = 0f;

            for (int message = 0; message < 50; message++)
                bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "joint1", "joint2" }, position = new[] { -1.3, 6.9 } });

            Assert.AreEqual(2, refusals.Count);
            CollectionAssert.AreEquivalent(new[] { RefusalCode.LimitMismatch, RefusalCode.LimitMismatch }, refusals.Select(r => r.Code));
            Assert.IsTrue(refusals.Any(r => r.Reason.Contains("joint1")) && refusals.Any(r => r.Reason.Contains("joint2")));
        }

        [Test]
        public void MirroredJointStates_InsideTheModel_RaiseNothing()
        {
            plugin.Client.SetMirroring(true);

            for (int message = 0; message < 20; message++)
                bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "joint1", "joint2" }, position = new[] { 1.0004, 6.2 } });

            Assert.IsEmpty(refusals, "Sensor noise at a limit is not a mismatch.");
        }

        // ---------- helpers ----------

        // Plans through the plugin: the fake move_group answers with a two-point trajectory.
        private PlanResult Plan(double[] from, double[] to)
        {
            bus.SetServiceHandler("/plan_kinematic_path", _ => new GetMotionPlanResponse
            {
                motion_plan_response = new MotionPlanResponseMsg
                {
                    error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                    trajectory = new RobotTrajectoryMsg
                    {
                        joint_trajectory = new JointTrajectoryMsg
                        {
                            joint_names = new[] { "joint1", "joint2" },
                            points = new[]
                            {
                                new JointTrajectoryPointMsg { positions = from },
                                new JointTrajectoryPointMsg
                                {
                                    positions = to,
                                    time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg(0, 20_000_000)
                                },
                            }
                        }
                    }
                }
            });
            PlanResult result = null;
            plugin.RequestPlan(new PlanPreferences(), r => result = r);
            Assert.IsNotNull(result, "The plan request did not come back.");
            return result;
        }
    }
}
