using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.StudyInterfaces;
using GetSolutionRequest = RosMessageTypes.StudyInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>The tier 3 MTC tab: solution browser, execution highlight, element budget, Teach-mode gate on recording.</summary>
    public class SolutionsTabTests
    {
        const string k_Task = "introspection_host_42_0xbeef:1";
        const string k_TopicTask = "introspection_host_42_0xbeef";

        private FakeRosBus bus;
        private GameObject pluginGo, canvasGo, modesGo;
        private MtcPlugin plugin;
        private PickPlaceClient client;
        private ModeManager modes;
        private SolutionsTab tab;
        private FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        private FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", Solution(((GetSolutionRequest)request).solution_id)));
            modesGo = new GameObject("modes");
            modes = modesGo.AddComponent<ModeManager>();

            pluginGo = new GameObject("mtc");
            client = pluginGo.AddComponent<PickPlaceClient>();
            pluginGo.AddComponent<PickPlaceTaskRecorder>();
            plugin = pluginGo.AddComponent<MtcPlugin>();
            yield return null;                                   // PickPlaceClient.Start subscribes
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = new FakeUiHost(), Modes = modes, Undo = new UndoStack() });
            plan = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>("/pick_place");
            execute = bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>("/execute_solution");

            var canvas = UiBuilder.CreateWorldCanvas("Tier3", null, new Vector2(800f, 900f));
            canvasGo = canvas.gameObject;
            tab = new SolutionsTab(plugin, UiBuilder.CreatePanel("Content", canvas.transform, Color.clear));
        }

        [TearDown]
        public void TearDown()
        {
            tab.Dispose();
            RosBus.Reset();
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(modesGo);
        }

        [UnityTest]
        public IEnumerator ManySolutions_ShowAtMostFiveInServerOrder_WithinSevenElements()
        {
            yield return Plan(8, 7, 6, 5, 4, 3, 2, 1);

            Assert.AreEqual(8, client.SolutionIds.Count);
            Assert.AreEqual(SolutionsTab.MaxSolutionButtons, tab.SolutionButtons.Count);
            Assert.LessOrEqual(tab.InteractiveElementCount, 7, "record + 5 solutions + cancel");
            Assert.That(ButtonText(tab.SolutionButtons[0]), Does.Contain("solution 8"), "statistics lists solved[] by ascending cost");
        }

        [UnityTest]
        public IEnumerator FirstSolution_IsFetchedSelectedAndGetsAWorldHandle()
        {
            yield return Plan(7, 4);

            Assert.AreEqual(7u, plugin.SelectedSolutionId);
            Assert.IsNotNull(plugin.SelectedSolution);
            var selectable = plugin.Results.Single();
            Assert.IsTrue(selectable.gameObject.activeSelf);
            Assert.IsNotNull(selectable.GetComponentInChildren<Collider>(), "A plan needs a collider to be ray-selectable.");
            Assert.AreEqual(SelectionKind.Trajectory, selectable.Kind);
            Assert.That(tab.BreakdownText, Does.Contain("1. move to pick").And.Contain("2. grasp"), "Steps are labelled from the description by stage_id");
        }

        [UnityTest]
        public IEnumerator ExecutingTheSecondSolution_HighlightFollowsFeedback_AndCancelShowsAsCancelled()
        {
            yield return Plan(7, 4);
            tab.SolutionButtons[1].onClick.Invoke();
            Assert.AreEqual(4u, plugin.SelectedSolutionId);
            var result = plugin.Results.Single(r => r.gameObject.activeSelf).Result;

            ExecutionStatus last = default;
            plugin.Execute(result, s => last = s);
            Assert.AreEqual(4u, execute.LastGoal.solution_id);
            Assert.AreEqual(k_Task, execute.LastGoal.task_id);
            var goal = execute.Accept();
            yield return null;

            var record = tab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Record");
            var cancel = tab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Cancel");
            Assert.IsFalse(record.interactable, "No new plan while a goal is active");
            Assert.IsTrue(cancel.interactable);
            Assert.That(tab.BreakdownText, Does.Contain("▶ 1. move to pick"));
            Assert.That(tab.StagesText, Does.Contain("▶ move to pick"));

            execute.Feedback(new ExecuteSolutionFeedback(0, 2, 2));
            Assert.That(tab.BreakdownText, Does.Contain("✓ 1. move to pick").And.Contain("▶ 2. grasp"));
            Assert.That(tab.StagesText, Does.Contain("▶ grasp"));

            cancel.onClick.Invoke();
            Assert.IsTrue(goal.CancelRequested);
            goal.CompleteCancel(RosActionCancelReturnCode.None);
            goal.Complete(RosActionGoalStatus.Canceled,
                new ExecuteSolutionResult(false, "", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.PREEMPTED, "", "")));
            while (client.Busy) yield return null;
            yield return null;

            Assert.AreEqual("CANCELED", tab.StatusText);
            Assert.AreEqual(ExecutionPhase.Canceled, last.Phase);
            Assert.IsFalse(cancel.interactable);
        }

        [UnityTest]
        public IEnumerator Replanning_ClearsTheBrowser()
        {
            yield return Plan(7, 4);
            Assert.AreEqual(2, tab.SolutionButtons.Count);

            Task replan = client.PlanAsync("object", new PoseStampedMsg());
            yield return null;
            Assert.AreEqual(0, tab.SolutionButtons.Count);
            Assert.IsNull(plugin.SelectedSolutionId);
            Assert.IsEmpty(plugin.Results);
            Assert.AreEqual("", tab.BreakdownText);

            plan.Reject();
            while (!replan.IsCompleted) yield return null;
            Assert.That(tab.StatusText, Does.StartWith("REJECTED"));
        }

        [Test]
        public void Record_IsGatedOnTeachMode()
        {
            Assert.IsFalse(plugin.InTeachMode);
            var record = tab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Record");
            Assert.IsFalse(record.interactable, "Recording is a Teach-mode activity (Part 5).");

            modes.SetMode(AppMode.Teach);
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            Assert.IsTrue(plugin.InTeachMode);
            Assert.IsTrue(record.interactable);
        }

        private IEnumerator Plan(params uint[] ids)
        {
            Task goal = client.PlanAsync("object", new PoseStampedMsg());
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound("/pick_place/description", PickPlaceClientTests.Description(k_TopicTask));
            bus.Inbound("/pick_place/statistics", PickPlaceClientTests.Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0, new MoveItErrorCodesMsg()));
            while (!goal.IsCompleted) yield return null;
            yield return null;                                   // destroyed solution buttons leave the hierarchy
        }

        private static string ButtonText(UnityEngine.UI.Button button) =>
            button.GetComponentInChildren<TMPro.TextMeshProUGUI>().text;

        private static SolutionMsg Solution(uint id) => new SolutionMsg
        {
            sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, cost = 1f, stage_id = 1 } } },
            sub_trajectory = new[]
            {
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 10, cost = 0.5f, stage_id = 2 } },
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 20, cost = 0.5f, stage_id = 3 } },
            }
        };
    }
}
