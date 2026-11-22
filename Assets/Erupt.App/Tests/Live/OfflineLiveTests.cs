using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Mtc;
using Erupt.Robot;
using RosMessageTypes.Geometry;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// The app started before the ROS side: nothing listens on the endpoint. The app has to stay
    /// usable and say why things cannot be done (a refusal, not a hang, not a stream of
    /// exceptions), and, when the ROS side is started later, connect and catch up by itself.
    /// </summary>
    /// <remarks>
    /// Run with <c>ERUPT_LIVE_SCENARIO=offline</c> and the ROS side stopped. For the second test
    /// also set <c>ERUPT_LIVE_WAIT_FOR_ROS=&lt;seconds&gt;</c> and start the single-arm stack while the
    /// test waits (about a minute after Unity was launched is right).
    /// </remarks>
    public class OfflineLiveTests : LiveSceneFixture
    {
        protected override string Scenario => LiveRos.Offline;
        protected override string EntryScene => "KitchenFR3";
        protected override string MainScene => "KitchenFR3";
        protected override bool ExpectsRos => false;

        private static RobotInstance Arm => Robots[0];
        private static GameObject offlineCube;

        [UnityTest, Order(10)]
        public IEnumerator T01_WithoutRos_TheAppRefusesInsteadOfHangingOrThrowing()
        {
            yield return Enter();
            yield return LiveRos.Seconds(4f);
            Assert.IsNotNull(Connection, "The app created its ROS connection.");
            Assert.IsTrue(Connection.HasConnectionThread, "The connection keeps trying.");
            Assert.IsTrue(Connection.HasConnectionError, "Nothing listens on the endpoint (stop the ROS side for this scenario).");
            AssertNoExceptions();
            AssertNoErrorsFromErupt();

            // The robot can still be posed, and a plan request is refused at once with the reason.
            ISelectable endEffector = EndEffectorOf(Arm);
            Assert.IsFalse(Press(endEffector, "set-start").IsRefused);
            Vector3 target = Arm.Model.EndEffector.position + RosOffset(Arm, new Vector3(0.08f, 0.08f, 0.05f));
            yield return DragEndEffector(Arm, target);
            Assert.Less((Arm.Model.EndEffector.position - target).magnitude, 0.02f, "IK works without ROS.");
            Assert.IsFalse(Press(endEffector, "set-goal").IsRefused);

            int refusalsBefore = Refusals.Count;
            var outcome = new PlanOutcome();
            yield return PressPlan(Arm, outcome, timeoutSeconds: 3f);
            Assert.IsNull(outcome.Result);
            StringAssert.Contains("not connected", outcome.Failure);
            Assert.IsTrue(Refusals.Skip(refusalsBefore).Any(r => r.Code == RefusalCode.PlannerUnavailable && r.Reason.Contains("not connected")),
                "The user is told that ROS is not connected: " + string.Join("; ", Refusals.Skip(refusalsBefore).Select(r => r.Reason)));

            // The gripper verb moves the gripper shown here and says the real one was not reached.
            // (The router drops a refusal that repeats the code and subject of one raised within
            // the last half second, so the two are told apart by waiting.)
            yield return LiveRos.Seconds(0.6f);
            GripperSpec spec = Arm.Profile.gripper;
            Arm.Model.TryGetJointAngle(spec.driverJoint, out float fingerBefore);
            refusalsBefore = Refusals.Count;
            Assert.IsFalse(Press(endEffector, "gripper").IsRefused);
            yield return LiveRos.Until(() => Arm.Model.TryGetJointAngle(spec.driverJoint, out float now) && Mathf.Abs(now - fingerBefore) > 0.01f,
                5f, "the gripper shown in Unity to move");
            Assert.IsTrue(Refusals.Skip(refusalsBefore).Any(r => r.Reason.Contains("not connected")), "The gripper verb says ROS is not connected.");

            // The environment can be edited; it is sent when ROS appears.
            offlineCube = AddShape(PrimitiveType.Cube, Arm.PlanningFrame, new Vector3(0.3f, -0.75f, 0.5f), Quaternion.Euler(0f, 30f, 0f), Vector3.one * 0.12f);
            Spawned.Remove(offlineCube);   // kept for the second test
            GameObject scratch = AddShape(PrimitiveType.Sphere, Arm.PlanningFrame, new Vector3(0.3f, -0.95f, 0.5f), Quaternion.identity, Vector3.one * 0.1f);
            yield return LiveRos.Seconds(1.5f);
            DeleteWithVerb(scratch);
            yield return LiveRos.Seconds(0.5f);

            // Pick and place says it is not available, and a goal fails at once instead of waiting.
            MtcPlugin.RobotSession mtc = Mtc.SessionFor(Arm);
            Assert.IsNotNull(mtc);
            Assert.IsFalse(mtc.Available, "Pick and place is not offered while ROS is away.");
            PoseStampedMsg place = mtc.PickPlace.PlacePose(new PointMsg(0.4, 0.2, 0.3), new QuaternionMsg(0, 0, 0, 1));
            Task goal = null;
            Exception thrown = null;
            try { goal = mtc.PickPlace.PlanAsync("nothing", place); }
            catch (Exception e) { thrown = e; }
            yield return LiveRos.Until(() => thrown != null || (goal != null && goal.IsCompleted), 3f, "the pick_place goal to be turned down");
            Assert.IsTrue(thrown != null || goal.IsFaulted, "A pick_place goal without ROS fails; it does not wait.");
            Assert.AreEqual(PickPlaceOutcome.Unavailable, mtc.PickPlace.LastOutcome);

            // A demonstration can be recorded, replayed and "published" without anything throwing.
            Demonstrations.StartDemonstration(Arm);
            yield return DragEndEffector(Arm, target + RosOffset(Arm, new Vector3(0f, -0.1f, 0f)));
            yield return LiveRos.Seconds(0.3f);
            Demonstrations.StopDemonstration(Arm);
            Demonstrations.Replay(Arm);
            yield return LiveRos.Seconds(0.5f);
            Demonstrations.StopReplay(Arm);
            Demonstrations.Publish(Arm);
            yield return LiveRos.Seconds(1f);

            AssertNoExceptions();
            AssertNoErrorsFromErupt();
            Assert.IsTrue(Connection.HasConnectionThread, "The connection is still trying.");
        }

        [UnityTest, Order(20)]
        public IEnumerator T02_WhenRosAppears_TheAppConnectsAndCatchesUp()
        {
            yield return Enter();
            float wait = LiveRos.WaitForRosSeconds;
            if (wait <= 0f)
                Assert.Ignore($"Set {LiveRos.WaitForRosVariable}=<seconds> and start the ROS side while this test waits.");

            Debug.Log($"[live] waiting up to {wait:0} s for the ROS side to be started...");
            yield return LiveRos.Until(() => !Connection.HasConnectionError, wait, "the ROS endpoint to appear (start the stack now)");
            Debug.Log("[live] the endpoint is there.");

            // MoveIt takes a while longer than the endpoint; the app notices when it answers.
            yield return LiveRos.Until(() => MoveIt.SessionFor(Arm).Client.Planners.Count > 0, 120f, "MoveIt to answer the planner query after it came up");
            yield return AssertConnected();
            yield return AssertJointStatesMirrored(Arm);

            // Everything Unity has, including what was added while ROS was away, reaches MoveIt.
            Assert.IsNotNull(offlineCube, "The cube added offline is still there.");
            yield return AssertInPlanningScene(Arm, offlineCube, IdOf(offlineCube), 60f);
            yield return AssertEveryPublisherIsInPlanningScene(Arm);

            // And the app works: plan, execute, gripper, pick and place availability.
            MoveIt.ResetPlanningState(Arm);
            yield return AssertPlanAndExecute(Arm, new Vector3(0.10f, -0.12f, 0.12f));
            yield return AssertGripperReachesRos(Arm);
            MtcPlugin.RobotSession mtc = Mtc.SessionFor(Arm);
            yield return LiveRos.Until(() => mtc.Available, 60f, "pick and place to become available once ROS is there");
            string cubeId = IdOf(offlineCube);
            DeleteWithVerb(offlineCube);
            yield return AssertNotInPlanningScene(Arm, cubeId);
            AssertNoExceptions();
            AssertNoErrorsFromErupt();
            AssertNoPlanningSceneComplaints();
        }
    }
}
