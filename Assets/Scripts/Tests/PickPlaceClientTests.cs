using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.StudyInterfaces;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using GetSolutionRequest = RosMessageTypes.StudyInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Covers the mtc_pick_place_server protocol: plan-only goals, task id capture, solution
    /// ids from statistics stage 1, /get_solution, /execute_solution, cancel, and the rules
    /// the server enforces (one task at a time, a new plan invalidates every id).
    /// </summary>
    public class PickPlaceClientTests
    {
        const string k_Task = "introspection_host_42_0xbeef:3";
        const string k_TopicTask = "introspection_host_42_0xbeef";
        const string k_Description = "/pick_place/description";
        const string k_Statistics = "/pick_place/statistics";

        FakeRosBus bus;
        GameObject clientObject;
        PickPlaceClient pickPlace;
        FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            clientObject = new GameObject("PickPlaceClient test");
            pickPlace = clientObject.AddComponent<PickPlaceClient>();
            yield return null;
            plan = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>("/pick_place");
            execute = bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>("/execute_solution");
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            if (clientObject != null) UnityEngine.Object.DestroyImmediate(clientObject);
        }

        [Test]
        public void Start_SubscribesAndRegistersEverythingBeforeAnyGoal()
        {
            CollectionAssert.Contains(bus.Subscriptions, k_Description);
            CollectionAssert.Contains(bus.Subscriptions, k_Statistics);
            CollectionAssert.Contains(bus.RegisteredServices, "/get_solution");
            Assert.IsNotNull(plan);
            Assert.IsNotNull(execute);
            Assert.AreEqual("study_interfaces/PickPlace", PickPlaceGoal.k_RosMessageName);
            Assert.AreEqual("study_interfaces/ExecuteSolution", ExecuteSolutionGoal.k_RosMessageName);
            Assert.AreEqual("study_interfaces/GetSolution", GetSolutionRequest.k_RosMessageName);
            Assert.IsTrue(new PickPlaceGoal().execute, "execute defaults to true, matching the .action default");
        }

        [UnityTest]
        public IEnumerator Plan_IsPlanOnly_CapturesTaskIdFromFirstNonEmptyFeedback_AndNeverPrintsInfinity()
        {
            Task<RosActionResult<PickPlaceResult>> goal = pickPlace.PlanAsync("object", Pose(0.6, -0.15, 0.0));
            Assert.AreEqual("object", plan.LastGoal.object_id);
            Assert.AreEqual("world", plan.LastGoal.place_pose.header.frame_id);
            Assert.IsFalse(plan.LastGoal.execute);
            Assert.AreEqual(3u, plan.LastGoal.max_solutions);
            Assert.IsNotNull(plan.LastGoal.start_scene_diff, "An empty scene diff is a no-op, null would not serialise");
            Assert.IsTrue(pickPlace.Busy);

            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback("", "initializing", 0, float.PositiveInfinity));
            Assert.IsNull(pickPlace.TaskId);
            Assert.AreEqual("INITIALIZING", pickPlace.LastStatus);

            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", 0, float.PositiveInfinity));
            Assert.AreEqual(k_Task, pickPlace.TaskId);
            Assert.That(pickPlace.LastStatus, Does.Not.Contain("Infinity").And.Not.Contain("∞"));

            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", 2, 12.5f));
            Assert.AreEqual("PLANNING: 2 solutions, best cost 12.50", pickPlace.LastStatus);

            accepted.Complete(RosActionGoalStatus.Succeeded, PlanResult(true, "planned"));
            yield return WaitFor(goal);
            Assert.AreEqual(PickPlaceOutcome.Succeeded, pickPlace.LastOutcome);
            Assert.AreEqual("PLANNED: planned", pickPlace.LastStatus);
            Assert.IsFalse(pickPlace.Busy);
        }

        [UnityTest]
        public IEnumerator SolutionIds_ComeFromStageOne_OfTheMatchingTask_EvenWhenTopicsBeatTheTaskId()
        {
            Task goal = pickPlace.PlanAsync("object", Pose(0.6, -0.15, 0.0));
            var accepted = plan.Accept();
            yield return null;

            // Topics can arrive before the feedback that names the task; they are volatile, so they must not be lost.
            bus.Inbound(k_Description, Description(k_TopicTask));
            bus.Inbound(k_Statistics, Statistics(k_TopicTask, 7, 4));
            Assert.IsNull(pickPlace.Description);
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", 0, float.PositiveInfinity));
            Assert.IsNotNull(pickPlace.Description, "Matched with the :<n> suffix stripped");
            CollectionAssert.AreEqual(new uint[] { 7, 4 }, pickPlace.SolutionIds);
            Assert.AreEqual("grasp", pickPlace.StageName(3));

            bus.Inbound(k_Statistics, Statistics("someone_elses_task", 99));
            CollectionAssert.AreEqual(new uint[] { 7, 4 }, pickPlace.SolutionIds);

            bus.Inbound(k_Statistics, Statistics(k_TopicTask, 7, 4, 9));
            CollectionAssert.AreEqual(new uint[] { 7, 4, 9 }, pickPlace.SolutionIds);

            accepted.Complete(RosActionGoalStatus.Succeeded, PlanResult(true, ""));
            yield return WaitFor(goal);
        }

        [UnityTest]
        public IEnumerator GetSolution_SendsTaskIdVerbatim_CachesPerTask_AndSurfacesTheServersMessage()
        {
            yield return PlanWithSolutions(7, 4);

            GetSolutionRequest seen = null;
            int calls = 0;
            bus.SetServiceHandler("/get_solution", request =>
            {
                calls++;
                seen = (GetSolutionRequest)request;
                return seen.solution_id == 4
                    ? new GetSolutionResponse(true, "", new SolutionMsg { task_id = k_TopicTask })
                    : new GetSolutionResponse(false, "unknown solution id", new SolutionMsg());
            });

            SolutionMsg fetched = null;
            pickPlace.FetchSolution(4, s => fetched = s);
            Assert.IsNotNull(fetched);
            Assert.AreEqual(k_Task, seen.task_id, "task_id goes back verbatim, :<n> included");
            Assert.AreEqual(4u, seen.solution_id);
            Assert.IsFalse(seen.include_start_scene);

            pickPlace.FetchSolution(4, s => fetched = s);
            Assert.AreEqual(1, calls, "Second selection is served from the per-task cache");

            string error = null;
            pickPlace.FetchSolution(7, _ => Assert.Fail("should fail"), e => error = e);
            Assert.AreEqual("unknown solution id", error);
            Assert.That(pickPlace.LastStatus, Does.Contain("unknown solution id"));
        }

        [UnityTest]
        public IEnumerator Execute_SendsTheChosenNonFirstSolution_AndFeedbackDrivesProgress()
        {
            yield return PlanWithSolutions(7, 4);
            bus.Inbound(k_Description, Description(k_TopicTask));

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            Assert.AreEqual(k_Task, execute.LastGoal.task_id);
            Assert.AreEqual(4u, execute.LastGoal.solution_id);
            Assert.AreEqual(PickPlacePhase.Executing, pickPlace.Phase);

            var accepted = execute.Accept();
            yield return null;
            ExecuteSolutionFeedback observed = null;
            pickPlace.OnExecutionFeedback += f => observed = f;
            execute.Feedback(new ExecuteSolutionFeedback(0, 2, 3));
            Assert.AreEqual(3u, observed.stage_id);
            Assert.AreEqual("EXECUTING: step 1/2 done (grasp)", pickPlace.LastStatus);

            accepted.Complete(RosActionGoalStatus.Succeeded,
                new ExecuteSolutionResult(true, "done", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            yield return WaitFor(run);
            Assert.AreEqual(PickPlaceOutcome.Succeeded, pickPlace.LastOutcome);
            Assert.IsFalse(pickPlace.Busy);
        }

        [UnityTest]
        public IEnumerator Cancel_MidPlanAndMidExecution_IsCancelled_NotAnError()
        {
            Task<RosActionResult<PickPlaceResult>> planning = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            var planGoal = plan.Accept();
            yield return null;
            Task<bool> cancel = pickPlace.CancelAsync();
            Assert.IsTrue(planGoal.CancelRequested);
            planGoal.CompleteCancel(RosActionCancelReturnCode.None);
            yield return WaitFor(cancel);
            Assert.AreEqual("CANCELING", pickPlace.LastStatus);
            planGoal.Complete(RosActionGoalStatus.Canceled, new PickPlaceResult(false, "preempted", k_Task, 0,
                new MoveItErrorCodesMsg(MoveItErrorCodesMsg.PREEMPTED, "", "")));
            yield return WaitFor(planning);
            Assert.AreEqual(PickPlaceOutcome.Canceled, pickPlace.LastOutcome);
            Assert.AreEqual("CANCELED", pickPlace.LastStatus);

            yield return PlanWithSolutions(7);
            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(7);
            var runGoal = execute.Accept();
            yield return null;
            _ = pickPlace.CancelAsync();
            Assert.IsTrue(runGoal.CancelRequested);
            runGoal.CompleteCancel(RosActionCancelReturnCode.None);
            runGoal.Complete(RosActionGoalStatus.Canceled,
                new ExecuteSolutionResult(false, "", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.PREEMPTED, "", "")));
            yield return WaitFor(run);
            Assert.AreEqual(PickPlaceOutcome.Canceled, pickPlace.LastOutcome);
            Assert.IsFalse(pickPlace.Busy);
        }

        [UnityTest]
        public IEnumerator CancelBeforeAcceptance_IsSentAsSoonAsTheGoalIsAccepted()
        {
            Task planning = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            _ = pickPlace.CancelAsync();
            var goal = plan.Accept();
            yield return null;
            yield return null;
            Assert.IsTrue(goal.CancelRequested);
            goal.CompleteCancel(RosActionCancelReturnCode.None);
            goal.Complete(RosActionGoalStatus.Canceled, PlanResult(false, ""));
            yield return WaitFor(planning);
        }

        [UnityTest]
        public IEnumerator Replan_ClearsEverything_AndOldIdsNeverReachTheWire()
        {
            yield return PlanWithSolutions(7, 4);
            int resets = 0;
            pickPlace.OnTaskReset += () => resets++;
            int serviceCalls = 0;
            bus.SetServiceHandler("/get_solution", r => { serviceCalls++; return new GetSolutionResponse(); });

            Task replan = pickPlace.PlanAsync("object", Pose(0.6, -0.15, 0.0));
            Assert.AreEqual(1, resets);
            Assert.IsNull(pickPlace.TaskId);
            Assert.IsEmpty(pickPlace.SolutionIds);
            Assert.IsNull(pickPlace.Description);
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback("introspection_host_42_0xbeef:4", "planning", 0, float.PositiveInfinity));
            accepted.Complete(RosActionGoalStatus.Succeeded, PlanResult(true, ""));
            yield return WaitFor(replan);

            var before = execute.LastGoal;
            Task stale = pickPlace.ExecuteAsync(7);
            yield return WaitFor(stale);
            Assert.IsTrue(stale.IsFaulted);
            Assert.AreSame(before, execute.LastGoal, "A solution id from the previous plan is refused locally");
            Assert.AreEqual(PickPlaceOutcome.Rejected, pickPlace.LastOutcome);

            string error = null;
            pickPlace.FetchSolution(7, _ => Assert.Fail("stale"), e => error = e);
            Assert.IsNotNull(error);
            Assert.AreEqual(0, serviceCalls);
        }

        [UnityTest]
        public IEnumerator Rejection_AndBusy_AreExplicit_NotHangs()
        {
            Task rejected = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            plan.Reject();
            yield return WaitFor(rejected);
            Assert.IsTrue(rejected.IsFaulted);
            Assert.AreEqual(PickPlaceOutcome.Rejected, pickPlace.LastOutcome);
            Assert.That(pickPlace.LastStatus, Does.StartWith("REJECTED").And.Contain("busy"));
            Assert.IsFalse(pickPlace.Busy, "A rejected goal frees the plan button again");

            Task running = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            var goal = plan.Accept();
            yield return null;
            Task second = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            Assert.IsInstanceOf<InvalidOperationException>(second.Exception?.InnerException);
            Assert.AreSame(goal, plan.ActiveGoal, "No second goal is sent while one is active");

            yield return PlanExecuteRejected(goal, running);
        }

        IEnumerator PlanExecuteRejected(FakeRosBus.FakeActionGoal<PickPlaceResult> goal, Task running)
        {
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", 1, 1f));
            bus.Inbound(k_Statistics, Statistics(k_TopicTask, 7));
            goal.Complete(RosActionGoalStatus.Succeeded, PlanResult(true, ""));
            yield return WaitFor(running);

            Task stale = pickPlace.ExecuteAsync(7);
            execute.Reject();
            yield return WaitFor(stale);
            Assert.IsTrue(stale.IsFaulted);
            Assert.That(pickPlace.LastStatus, Does.StartWith("REJECTED").And.Contain("plan again"));
            Assert.IsFalse(pickPlace.Busy);
        }

        [UnityTest]
        public IEnumerator Teardown_CancelsTheActiveGoal_BecauseDisconnectingDoesNot()
        {
            Task planning = pickPlace.PlanAsync("object", Pose(0, 0, 0));
            var goal = plan.Accept();
            yield return null;
            UnityEngine.Object.DestroyImmediate(clientObject);
            Assert.IsTrue(goal.CancelRequested);
            goal.CompleteCancel(RosActionCancelReturnCode.None);
            goal.Complete(RosActionGoalStatus.Canceled, PlanResult(false, ""));
            yield return WaitFor(planning);
        }

        [UnityTest]
        public IEnumerator PlanAndExecute_IsTheJustDoItPath_AndDisconnectIsExplicit()
        {
            Task<RosActionResult<PickPlaceResult>> goal = pickPlace.PlanAndExecuteAsync("object", Pose(0.6, -0.15, 0.0));
            Assert.IsTrue(plan.LastGoal.execute);
            plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "executing", 1, 3f));
            Assert.AreEqual(PickPlacePhase.Executing, pickPlace.Phase);
            Assert.AreEqual("EXECUTING", pickPlace.LastStatus);

            plan.Fail(new RosActionException("connection_lost", "TCP connection lost"));
            yield return WaitFor(goal);
            Assert.IsTrue(goal.IsFaulted);
            Assert.AreEqual(PickPlaceOutcome.Disconnected, pickPlace.LastOutcome);
        }

        // --- helpers ---------------------------------------------------------------------

        IEnumerator PlanWithSolutions(params uint[] ids)
        {
            Task goal = pickPlace.PlanAsync("object", Pose(0.6, -0.15, 0.0));
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound(k_Statistics, Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, PlanResult(true, ""));
            yield return WaitFor(goal);
        }

        static PickPlaceResult PlanResult(bool success, string message) =>
            new PickPlaceResult(success, message, k_Task, 0,
                new MoveItErrorCodesMsg(success ? MoveItErrorCodesMsg.SUCCESS : MoveItErrorCodesMsg.FAILURE, "", ""));

        internal static TaskStatisticsMsg Statistics(string taskId, params uint[] solved) => new TaskStatisticsMsg
        {
            task_id = taskId,
            stages = new[]
            {
                new StageStatisticsMsg { id = 1, solved = solved },
                new StageStatisticsMsg { id = 2, solved = new uint[] { 100, 101, 102, 103 } },
                new StageStatisticsMsg { id = 3, solved = new uint[] { 200 } },
            }
        };

        internal static TaskDescriptionMsg Description(string taskId) => new TaskDescriptionMsg
        {
            task_id = taskId,
            stages = new[]
            {
                new StageDescriptionMsg { id = 1, parent_id = 0, name = "pick and place" },
                new StageDescriptionMsg { id = 2, parent_id = 1, name = "move to pick" },
                new StageDescriptionMsg { id = 3, parent_id = 1, name = "grasp" },
            }
        };

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
