using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    public class MTCExecutionTests
    {
        const string k_ActionName = "/execute_task_solution";

        FakeRosBus bus;
        GameObject managerObject;
        MTCDataManager manager;
        FakeRosBus.FakeActionClient<ExecuteTaskSolutionGoal,
            ExecuteTaskSolutionResult, ExecuteTaskSolutionFeedback> client;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            managerObject = new GameObject("MTCDataManager action test");
            manager = managerObject.AddComponent<MTCDataManager>();
            yield return null;
            client = bus.ActionClient<ExecuteTaskSolutionGoal,
                ExecuteTaskSolutionResult, ExecuteTaskSolutionFeedback>(k_ActionName);
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            if (managerObject != null) Object.DestroyImmediate(managerObject);
        }

        [UnityTest]
        public IEnumerator LongRunningGoal_YieldsFramesAndDeliversFeedbackOnMainThread()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int feedbackThread = -1;
            ExecuteTaskSolutionFeedback observedFeedback = null;
            manager.OnExecutionFeedback += feedback =>
            {
                feedbackThread = Thread.CurrentThread.ManagedThreadId;
                observedFeedback = feedback;
            };

            var solution = new SolutionMsg { task_id = "complete-solution" };
            Task<RosActionResult<ExecuteTaskSolutionResult>> execution =
                manager.ExecuteSolutionAsync(solution);

            Assert.AreSame(solution, client.LastGoal.solution,
                "The native action goal must contain the complete selected SolutionMsg");
            for (int frame = 0; frame < 3; frame++)
            {
                Assert.IsFalse(execution.IsCompleted,
                    "Awaiting action acceptance must not block Unity frame updates");
                yield return null;
            }

            var goal = client.Accept();
            yield return null;
            client.Feedback(new ExecuteTaskSolutionFeedback(1, 4));
            Assert.AreEqual(mainThread, feedbackThread);
            Assert.AreEqual(1u, observedFeedback.sub_id);
            Assert.AreEqual("EXECUTING", manager.LastExecutionStatus);

            goal.Complete(
                RosActionGoalStatus.Succeeded,
                Result(MoveItErrorCodesMsg.SUCCESS));
            yield return WaitFor(execution);
            Assert.AreEqual("SUCCEEDED", manager.LastExecutionStatus);
            Assert.AreEqual(RosActionGoalStatus.Succeeded, execution.Result.Status);
        }

        [UnityTest]
        public IEnumerator RejectionAndUnsupportedEndpoint_AreExplicitStates()
        {
            Task<RosActionResult<ExecuteTaskSolutionResult>> execution =
                manager.ExecuteSolutionAsync(new SolutionMsg());
            client.Reject();
            yield return WaitFor(execution);
            Assert.IsTrue(execution.IsFaulted);
            Assert.AreEqual("REJECTED", manager.LastExecutionStatus);

            client.SetState(RosActionClientState.Unsupported);
            Assert.IsFalse(manager.ExecutionAvailable);
            Assert.That(manager.LastExecutionStatus, Does.Contain("UPGRADE REQUIRED"));
        }

        [UnityTest]
        public IEnumerator AbortCancelAndDisconnect_AreExplicitStates()
        {
            Task<RosActionResult<ExecuteTaskSolutionResult>> aborted =
                manager.ExecuteSolutionAsync(new SolutionMsg());
            var abortedGoal = client.Accept();
            yield return null;
            abortedGoal.Complete(RosActionGoalStatus.Aborted,
                Result(MoveItErrorCodesMsg.CONTROL_FAILED));
            yield return WaitFor(aborted);
            Assert.That(manager.LastExecutionStatus, Does.StartWith("ABORTED"));

            Task<RosActionResult<ExecuteTaskSolutionResult>> canceled =
                manager.ExecuteSolutionAsync(new SolutionMsg());
            var canceledGoal = client.Accept();
            yield return null;
            Task<RosActionCancelResponse> cancel = manager.CancelExecutionAsync();
            canceledGoal.CompleteCancel(RosActionCancelReturnCode.None);
            yield return WaitFor(cancel);
            Assert.AreEqual("CANCELING", manager.LastExecutionStatus);
            canceledGoal.Complete(RosActionGoalStatus.Canceled,
                Result(MoveItErrorCodesMsg.PREEMPTED));
            yield return WaitFor(canceled);
            Assert.AreEqual("CANCELED", manager.LastExecutionStatus);

            Task<RosActionResult<ExecuteTaskSolutionResult>> disconnected =
                manager.ExecuteSolutionAsync(new SolutionMsg());
            client.Accept();
            yield return null;
            client.Fail(new RosActionException("connection_lost", "TCP connection lost"));
            yield return WaitFor(disconnected);
            Assert.IsTrue(disconnected.IsFaulted);
            Assert.That(manager.LastExecutionStatus, Does.StartWith("DISCONNECTED"));
        }

        static ExecuteTaskSolutionResult Result(int errorCode) =>
            new ExecuteTaskSolutionResult(new MoveItErrorCodesMsg(errorCode, "", ""));

        static IEnumerator WaitFor(Task task)
        {
            while (!task.IsCompleted) yield return null;
        }
    }
}
