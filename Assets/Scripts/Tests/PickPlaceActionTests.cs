using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RosMessageTypes.Geometry;
using RosMessageTypes.StudyInterfaces;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Covers the /pick_place action path: the goal Unity puts on the wire, the
    /// per-stage feedback, and every terminal state the demo node can report.
    /// </summary>
    public class PickPlaceActionTests
    {
        const string k_ActionName = "/pick_place";

        FakeRosBus bus;
        GameObject clientObject;
        PickPlaceActionClient pickPlace;
        FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> client;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            clientObject = new GameObject("PickPlaceActionClient test");
            pickPlace = clientObject.AddComponent<PickPlaceActionClient>();
            yield return null;
            client = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>(k_ActionName);
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            if (clientObject != null) Object.DestroyImmediate(clientObject);
        }

        [UnityTest]
        public IEnumerator Goal_CarriesObjectIdAndPlacePose_AndFeedbackArrivesOnMainThread()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int feedbackThread = -1;
            PickPlaceFeedback observed = null;
            pickPlace.OnFeedback += feedback =>
            {
                feedbackThread = Thread.CurrentThread.ManagedThreadId;
                observed = feedback;
            };

            Task<RosActionResult<PickPlaceResult>> goal = pickPlace.SendGoalAsync(
                "object", Pose(0.6, -0.15, 0.0));

            Assert.AreEqual("object", client.LastGoal.object_id);
            Assert.AreEqual("world", client.LastGoal.place_pose.header.frame_id);
            Assert.AreEqual(0.6, client.LastGoal.place_pose.pose.position.x, 1e-6);
            Assert.AreEqual(-0.15, client.LastGoal.place_pose.pose.position.y, 1e-6);
            Assert.AreEqual(1.0, client.LastGoal.place_pose.pose.orientation.w, 1e-6);
            Assert.IsTrue(client.LastGoal.execute, "execute defaults to true, matching the .action default");

            for (int frame = 0; frame < 3; frame++)
            {
                Assert.IsFalse(goal.IsCompleted, "Awaiting goal acceptance must not block Unity frames");
                yield return null;
            }

            var accepted = client.Accept();
            yield return null;
            client.Feedback(new PickPlaceFeedback("planning"));
            Assert.AreEqual(mainThread, feedbackThread);
            Assert.AreEqual("planning", observed.stage);
            Assert.AreEqual("planning", pickPlace.LastStage);
            Assert.AreEqual("PLANNING", pickPlace.LastStatus);

            client.Feedback(new PickPlaceFeedback("executing"));
            Assert.AreEqual("EXECUTING", pickPlace.LastStatus);

            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "done"));
            yield return WaitFor(goal);
            Assert.AreEqual(RosActionGoalStatus.Succeeded, goal.Result.Status);
            Assert.IsTrue(goal.Result.Result.success);
            Assert.AreEqual("SUCCEEDED: done", pickPlace.LastStatus);
            Assert.IsFalse(pickPlace.IsRunning);
        }

        [UnityTest]
        public IEnumerator PlanOnlyGoal_SetsExecuteFalse()
        {
            Task<RosActionResult<PickPlaceResult>> goal = pickPlace.SendGoalAsync(
                "object", Pose(0.6, -0.15, 0.0), execute: false);
            Assert.IsFalse(client.LastGoal.execute);

            var accepted = client.Accept();
            yield return null;
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "planned"));
            yield return WaitFor(goal);
            Assert.AreEqual("SUCCEEDED: planned", pickPlace.LastStatus);
        }

        [UnityTest]
        public IEnumerator RejectionAndUnsupportedEndpoint_AreExplicitStates()
        {
            Task<RosActionResult<PickPlaceResult>> goal = pickPlace.SendGoalAsync("object", Pose(0, 0, 0));
            client.Reject();
            yield return WaitFor(goal);
            Assert.IsTrue(goal.IsFaulted);
            Assert.AreEqual("REJECTED", pickPlace.LastStatus);

            client.SetState(RosActionClientState.Unsupported);
            Assert.IsFalse(pickPlace.Available);
            Assert.That(pickPlace.LastStatus, Does.Contain("UPGRADE REQUIRED"));
        }

        [UnityTest]
        public IEnumerator AbortCancelAndDisconnect_AreExplicitStates()
        {
            Task<RosActionResult<PickPlaceResult>> aborted = pickPlace.SendGoalAsync("object", Pose(0, 0, 0));
            var abortedGoal = client.Accept();
            yield return null;
            abortedGoal.Complete(RosActionGoalStatus.Aborted, new PickPlaceResult(false, "planning failed"));
            yield return WaitFor(aborted);
            Assert.AreEqual("ABORTED: planning failed", pickPlace.LastStatus);

            Task<RosActionResult<PickPlaceResult>> canceled = pickPlace.SendGoalAsync("object", Pose(0, 0, 0));
            var canceledGoal = client.Accept();
            yield return null;
            Task<RosActionCancelResponse> cancel = pickPlace.CancelAsync();
            canceledGoal.CompleteCancel(RosActionCancelReturnCode.None);
            yield return WaitFor(cancel);
            Assert.AreEqual("CANCELING", pickPlace.LastStatus);
            canceledGoal.Complete(RosActionGoalStatus.Canceled, new PickPlaceResult(false, "preempted"));
            yield return WaitFor(canceled);
            Assert.AreEqual("CANCELED: preempted", pickPlace.LastStatus);

            Task<RosActionResult<PickPlaceResult>> disconnected = pickPlace.SendGoalAsync("object", Pose(0, 0, 0));
            client.Accept();
            yield return null;
            client.Fail(new RosActionException("connection_lost", "TCP connection lost"));
            yield return WaitFor(disconnected);
            Assert.IsTrue(disconnected.IsFaulted);
            Assert.That(pickPlace.LastStatus, Does.StartWith("DISCONNECTED"));
        }

        static PoseStampedMsg Pose(double x, double y, double z) =>
            new PoseStampedMsg(
                new RosMessageTypes.Std.HeaderMsg(
                    new RosMessageTypes.BuiltinInterfaces.TimeMsg(), "world"),
                new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)));

        static IEnumerator WaitFor(Task task)
        {
            while (!task.IsCompleted) yield return null;
        }
    }
}
