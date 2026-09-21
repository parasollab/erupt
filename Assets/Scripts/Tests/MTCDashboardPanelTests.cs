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

        // ─── partial / failed stage solutions ─────────────────────────────────────

        [UnityTest]
        public IEnumerator PartialStageSolution_IsFetchedWithItsStartScene_Previewed_AndNeverExecutable()
        {
            var requests = RecordGetSolution(id => new GetSolutionResponse(true, "", Solution(id)));
            var player = AddTrajectoryPlayer();
            yield return Plan(7, 4);

            panel.SelectStage(2);
            Assert.Contains("mtcStageSolution100", Rows("mtcStageSolutionsContainer"));
            Assert.That(RowText("mtcStageSolution100"), Does.Contain("preview"));

            panel.PreviewStageSolution(100);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(100u, requests[0].solution_id);
            Assert.IsTrue(requests[0].include_start_scene, "A partial solution starts mid-task; its start scene is needed.");
            Assert.IsTrue(player.IsPlaying, "The tap must end in a preview.");
            Assert.That(panel.StageSolutionStatus, Does.Contain("Previewing stage solution 100"));
            Assert.IsNull(panel.SelectedSolutionId, "Partial solutions must never become the executable selection.");
            Assert.IsFalse(ButtonNamed("mtcExecuteButton").enabledSelf);
            player.Stop();
        }

        [UnityTest]
        public IEnumerator SolutionFetchedWithoutStartScene_IsNotReusedWhenOneIsNeeded()
        {
            var requests = RecordGetSolution(id => new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);

            client.FetchSolution(7, _ => { });
            client.FetchSolution(7, _ => { }, null, includeStartScene: true);
            Assert.AreEqual(2, requests.Count, "The cached no-scene solution must not answer a with-scene request.");
            Assert.IsFalse(requests[0].include_start_scene);
            Assert.IsTrue(requests[1].include_start_scene);

            client.FetchSolution(7, _ => { }, null, includeStartScene: true);
            client.FetchSolution(7, _ => { });
            Assert.AreEqual(2, requests.Count, "Both variants are cached afterwards.");
        }

        [UnityTest]
        public IEnumerator FailedStageSolution_ShowsWhyItFailed()
        {
            RecordGetSolution(id => new GetSolutionResponse(true, "", new SolutionMsg
            {
                sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, stage_id = 3, comment = "object in collision with table" } } },
            }));
            yield return Plan(7, 4);
            var stats = PickPlaceClientTests.Statistics(k_TopicTask, 7, 4);
            stats.stages[2].failed = new uint[] { 300 };
            bus.Inbound("/pick_place/statistics", stats);

            panel.SelectStage(3);
            Assert.Contains("mtcStageFailed300", Rows("mtcStageSolutionsContainer"));

            panel.PreviewStageSolution(300, failed: true);

            Assert.That(panel.StageSolutionStatus, Does.Contain("object in collision with table"));
            Assert.That(LabelNamed("mtcStageSolutionStatus").text, Does.Contain("object in collision with table"));
            Assert.IsNull(panel.SelectedSolutionId);
            Assert.IsFalse(ButtonNamed("mtcExecuteButton").enabledSelf);
        }

        [UnityTest]
        public IEnumerator ServerRefusingAPartialSolution_IsShown_AndThePanelStaysUsable()
        {
            RecordGetSolution(id => id >= 100
                ? new GetSolutionResponse(false, "unknown solution id 100 for task t", new SolutionMsg())
                : new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);

            panel.SelectStage(2);
            panel.PreviewStageSolution(100);
            Assert.That(panel.StageSolutionStatus, Does.Contain("unknown solution id 100"));

            panel.SelectSolution(7);
            Assert.IsNotNull(panel.SelectedSolution, "An older server rejecting partial ids must not break the browser.");
            Assert.IsTrue(ButtonNamed("mtcExecuteButton").enabledSelf);
        }

        [UnityTest]
        public IEnumerator NoStagePreviewWhileExecuting()
        {
            var requests = RecordGetSolution(id => new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);
            panel.SelectSolution(7);
            panel.ExecuteSelected();
            execute.Accept();
            yield return null;
            int before = requests.Count;

            panel.SelectStage(2);
            panel.PreviewStageSolution(100);

            Assert.AreEqual(before, requests.Count);
            Assert.That(panel.StageSolutionStatus, Does.Contain("executing"));
        }

        System.Collections.Generic.List<GetSolutionRequest> RecordGetSolution(System.Func<uint, GetSolutionResponse> respond)
        {
            var requests = new System.Collections.Generic.List<GetSolutionRequest>();
            bus.SetServiceHandler("/get_solution", request =>
            {
                var r = (GetSolutionRequest)request;
                requests.Add(r);
                return respond(r.solution_id);
            });
            return requests;
        }

        // A player with a joint-less controller: enough to tell that a preview started.
        MTCTrajectoryPlayer AddTrajectoryPlayer()
        {
            var ik = panelGo.AddComponent<DirectArticulationIKController>();
            ik.enabled = false;
            var player = panelGo.AddComponent<MTCTrajectoryPlayer>();
            SetPrivate(player, "ikController", ik);
            SetPrivate(panel, "trajectoryPlayer", player);
            return player;
        }

        static void SetPrivate(object target, string field, object value) =>
            target.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(target, value);

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
