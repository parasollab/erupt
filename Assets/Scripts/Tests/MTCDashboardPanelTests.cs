#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.StudyInterfaces;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using GetSolutionRequest = RosMessageTypes.StudyInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// The MTC dashboard on the real MTCDashboard.uxml: solution browser, execution
    /// highlight, cancel, and the one-task-at-a-time gating of its buttons.
    /// </summary>
    public class MTCDashboardPanelTests
    {
        const string k_Task = "introspection_host_42_0xbeef:1";
        const string k_TopicTask = "introspection_host_42_0xbeef";

        FakeRosBus bus;
        GameObject clientGo, panelGo;
        PanelSettings panelSettings;
        PickPlaceClient client;
        MTCDashboardPanel panel;
        FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", Solution(((GetSolutionRequest)request).solution_id)));

            clientGo = new GameObject("PickPlaceClient");
            client = clientGo.AddComponent<PickPlaceClient>();
            clientGo.AddComponent<PickPlaceTaskRecorder>();
            yield return null;                                   // PickPlaceClient.Start registers
            plan = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>("/pick_place");
            execute = bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>("/execute_solution");

            panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelGo = new GameObject("MTCDashboard");
            panelGo.SetActive(false);
            var document = panelGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI Toolkit/MTCDashboard.uxml");
            Assert.IsNotNull(document.visualTreeAsset, "MTCDashboard.uxml moved?");
            panel = panelGo.AddComponent<MTCDashboardPanel>();
            panelGo.SetActive(true);                             // UIDocument builds the tree, then the panel binds
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            Object.DestroyImmediate(panelGo);
            Object.DestroyImmediate(clientGo);
            Object.DestroyImmediate(panelSettings);
        }

        [UnityTest]
        public IEnumerator Browse_ExecuteTheSecondSolution_HighlightFollowsFeedback_CancelShowsCancelled()
        {
            yield return Plan(7, 4);
            CollectionAssert.AreEqual(new[] { "mtcSolution7", "mtcSolution4" }, Rows("mtcSolutionListContainer"),
                "solved[] of stage 1, in the server's order");
            Assert.IsFalse(ButtonNamed("mtcExecuteButton").enabledSelf, "Nothing selected yet");

            panel.SelectSolution(4);
            Assert.AreEqual(4u, panel.SelectedSolutionId);
            Assert.IsNotNull(panel.SelectedSolution);
            Assert.That(RowText("mtcStep0"), Does.Contain("1. move to pick"));
            Assert.That(RowText("mtcStep1"), Does.Contain("2. grasp"), "Steps are labelled from the description by stage_id");
            Assert.IsTrue(ButtonNamed("mtcExecuteButton").enabledSelf);

            panel.ExecuteSelected();
            Assert.AreEqual(4u, execute.LastGoal.solution_id);
            Assert.AreEqual(k_Task, execute.LastGoal.task_id, "task_id goes back verbatim");
            var goal = execute.Accept();
            yield return null;

            Assert.IsFalse(ButtonNamed("mtcPlanRecordButton").enabledSelf, "No new plan while a goal is active");
            Assert.IsFalse(ButtonNamed("mtcExecuteButton").enabledSelf);
            Assert.IsTrue(ButtonNamed("mtcCancelExecuteButton").enabledSelf);
            Assert.That(RowText("mtcStep0"), Does.StartWith("▶"));
            Assert.That(RowText("mtcStage2"), Does.Contain("▶ move to pick"));

            execute.Feedback(new ExecuteSolutionFeedback(0, 2, 2));
            Assert.That(RowText("mtcStep0"), Does.StartWith("✓"));
            Assert.That(RowText("mtcStep1"), Does.StartWith("▶"));
            Assert.That(RowText("mtcStage3"), Does.Contain("▶ grasp"));
            Assert.AreEqual(50f, panel.Root.Q<ProgressBar>("mtcExecProgress").value, 0.01f);

            panel.Cancel();
            Assert.IsTrue(goal.CancelRequested);
            goal.CompleteCancel(RosActionCancelReturnCode.None);
            goal.Complete(RosActionGoalStatus.Canceled,
                new ExecuteSolutionResult(false, "", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.PREEMPTED, "", "")));
            while (client.Busy) yield return null;
            yield return null;

            Assert.AreEqual("CANCELED", LabelNamed("mtcExecStatusLabel").text);
            Assert.IsFalse(ButtonNamed("mtcCancelExecuteButton").enabledSelf);
            Assert.IsTrue(ButtonNamed("mtcPlanRecordButton").enabledSelf);
            Assert.IsTrue(ButtonNamed("mtcExecuteButton").enabledSelf);
        }

        [UnityTest]
        public IEnumerator Replan_ClearsTheBrowser_AndARejectionIsAStatusNotAHang()
        {
            yield return Plan(7, 4);
            panel.SelectSolution(7);
            Assert.IsNotEmpty(Rows("mtcBreakdownContainer"));

            Task replan = client.PlanAsync("object", new PoseStampedMsg());
            Assert.IsNull(panel.SelectedSolutionId);
            Assert.IsEmpty(Rows("mtcSolutionListContainer"));
            Assert.IsEmpty(Rows("mtcBreakdownContainer"));
            Assert.IsEmpty(Rows("mtcStageTreeContainer"));
            Assert.IsTrue(ButtonNamed("mtcPlanCancelButton").enabledSelf, "Planning can be cancelled");
            Assert.IsFalse(ButtonNamed("mtcPlanRecordButton").enabledSelf);

            plan.Reject();
            while (!replan.IsCompleted) yield return null;
            Assert.That(LabelNamed("mtcPlanStatusLabel").text, Does.StartWith("REJECTED"));
            Assert.IsTrue(ButtonNamed("mtcPlanRecordButton").enabledSelf);
            Assert.IsFalse(ButtonNamed("mtcPlanCancelButton").enabledSelf);
        }

        [UnityTest]
        public IEnumerator StageTree_RootsAtTheContainer_WithCounts()
        {
            yield return Plan(7, 4);
            CollectionAssert.AreEqual(new[] { "mtcStage1", "mtcStage2", "mtcStage3" }, Rows("mtcStageTreeContainer"),
                "Stage 0 is never published; stage 1 is the root");
            Assert.That(RowText("mtcStage1"), Does.Contain("pick and place").And.Contain("✓2"));
        }

        IEnumerator Plan(params uint[] ids)
        {
            Task goal = client.PlanAsync("object", new PoseStampedMsg());
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound("/pick_place/description", PickPlaceClientTests.Description(k_TopicTask));
            bus.Inbound("/pick_place/statistics", PickPlaceClientTests.Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0, new MoveItErrorCodesMsg()));
            while (!goal.IsCompleted) yield return null;
        }

        string[] Rows(string container) =>
            panel.Root.Q<VisualElement>(container).Children().Select(c => c.name).Where(n => !string.IsNullOrEmpty(n)).ToArray();

        string RowText(string row) =>
            string.Join(" ", panel.Root.Q<VisualElement>(row).Query<Label>().ToList().Select(l => l.text));

        Button ButtonNamed(string name) => panel.Root.Q<Button>(name);
        Label LabelNamed(string name) => panel.Root.Q<Label>(name);

        static SolutionMsg Solution(uint id) => new SolutionMsg
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
#endif
