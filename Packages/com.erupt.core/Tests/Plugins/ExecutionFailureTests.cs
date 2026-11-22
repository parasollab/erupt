using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// A press on Execute that does not move the robot says why, at the plan the user pressed
    /// it on: every planner's execution statuses go through the execute verb, and whatever
    /// keeps the robot from arriving becomes a refusal on the router.
    /// </summary>
    public class ExecutionFailureTests
    {
        // A planner whose execution reports whatever the test tells it to.
        private sealed class ScriptedPlanner : PlanningPlugin
        {
            public readonly List<ExecutionStatus> Script = new();
            public Action<ExecutionStatus> Later;
            public int PreviewCalls;

            public override string Id => "scripted";
            public override string DisplayName => "Scripted";
            public override InteractionRefusal SetGoal(RobotInstance robot, ISelectable endEffector) => InteractionRefusal.None;
            public override void RequestPlan(RobotInstance robot, PlanPreferences preferences, Action<PlanResult> done) =>
                done?.Invoke(PublishResult(new PlanResult()));
            public override void Preview(PlanResult plan) => PreviewCalls++;
            public override void StopPreview(RobotInstance robot) { }
            public override void Execute(PlanResult plan, Action<ExecutionStatus> status)
            {
                foreach (ExecutionStatus step in Script) status?.Invoke(step);
                Later = status;
            }
            public TrajectorySelectable Place(Vector3 at) => PlaceHandle(LastResult, at);
        }

        private GameObject go, routerGo;
        private ScriptedPlanner planner;
        private FakeUiHost ui;
        private InteractionRouter router;
        private TrajectorySelectable plan;
        private readonly List<InteractionRefusal> refusals = new();
        private static readonly Vector3 HandleAt = new Vector3(1f, 2f, 3f);

        [SetUp]
        public void SetUp()
        {
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            router.RefusalCoalesceSeconds = 0f;
            refusals.Clear();
            router.Refused += refusals.Add;

            go = new GameObject("planner");
            planner = go.AddComponent<ScriptedPlanner>();
            ui = new FakeUiHost();
            ((IEruptPlugin)planner).OnRegister(new EruptContext { Ui = ui, Undo = new UndoStack(), Router = router });
            ui.Bound["plan"](null);
            plan = planner.Place(HandleAt);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(routerGo);
        }

        [Test]
        public void AnExecutionThatIsUnavailable_IsARefusal_AtThePlansHandle()
        {
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Unavailable, "MoveIt is not connected."));

            ui.Bound["execute"](plan);

            Assert.AreEqual(1, refusals.Count, "The button used to do nothing at all.");
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals[0].Code);
            Assert.AreEqual("MoveIt is not connected.", refusals[0].Reason);
            Assert.AreEqual(HandleAt, refusals[0].WorldPoint, "Where the user was pointing.");
        }

        [TestCase(ExecutionPhase.Failed, "No planned trajectory to execute.")]
        [TestCase(ExecutionPhase.Aborted, "The controller aborted the trajectory.")]
        public void AnExecutionThatFails_IsARefusal(ExecutionPhase phase, string message)
        {
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Sending));
            planner.Script.Add(new ExecutionStatus(phase, message));

            ui.Bound["execute"](plan);

            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.ExecutionFailed, refusals[0].Code);
            Assert.AreEqual(message, refusals[0].Reason);
        }

        [Test]
        public void AFailureThatComesLater_IsShownWhereTheHandleIsThen()
        {
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Sending));
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Executing));
            ui.Bound["execute"](plan);
            Assert.IsEmpty(refusals);

            plan.transform.position = new Vector3(4f, 5f, 6f);
            planner.Later(new ExecutionStatus(ExecutionPhase.Failed, "Execution did not finish: the robot did not reach the end of the trajectory."));

            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(new Vector3(4f, 5f, 6f), refusals[0].WorldPoint);
            StringAssert.Contains("did not reach the end", refusals[0].Reason);
        }

        // Succeeded is no refusal; Refused was reported by MayExecute already; Canceled is the user's own doing.
        [TestCase(ExecutionPhase.Succeeded)]
        [TestCase(ExecutionPhase.Refused)]
        [TestCase(ExecutionPhase.Canceled)]
        public void OtherOutcomes_AreNotReportedHere(ExecutionPhase phase)
        {
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Sending));
            planner.Script.Add(new ExecutionStatus(phase));
            ui.Bound["execute"](plan);
            Assert.IsEmpty(refusals);
        }

        [Test]
        public void ExecuteAndReport_PassesEveryStatusOn_AndWithoutAHandleShowsTheRefusalAtThePlugin()
        {
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Sending));
            planner.Script.Add(new ExecutionStatus(ExecutionPhase.Unavailable, "The server is busy with another goal."));
            var seen = new List<ExecutionPhase>();

            planner.ExecuteAndReport(planner.LastResult, null, s => seen.Add(s.Phase));

            CollectionAssert.AreEqual(new[] { ExecutionPhase.Sending, ExecutionPhase.Unavailable }, seen);
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(go.transform.position, refusals[0].WorldPoint);
        }

        [Test]
        public void PreviewAndExecute_OnSomethingThatIsNoPlan_SayThereIsNoPlan()
        {
            var obstacle = new GameObject("o").AddComponent<SelectableMarker>();
            obstacle.transform.position = new Vector3(7f, 0f, 0f);
            try
            {
                ui.Bound["execute"](obstacle);
                ui.Bound["preview"](obstacle);
                Assert.AreEqual(2, refusals.Count);
                Assert.AreEqual(RefusalCode.VerbUnavailable, refusals[0].Code);
                StringAssert.Contains("no plan here to execute", refusals[0].Reason);
                StringAssert.Contains("no plan here to preview", refusals[1].Reason);
                Assert.AreEqual(obstacle.transform.position, refusals[0].WorldPoint);
                Assert.AreEqual(0, planner.PreviewCalls);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obstacle.gameObject);
            }
        }
    }
}
