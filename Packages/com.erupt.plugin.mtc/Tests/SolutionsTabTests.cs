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
using RosMessageTypes.Trajectory;
using Erupt.Robot.Tests;
using GetSolutionRequest = RosMessageTypes.StudyInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// The tier 3 MTC tab: solution browser, execution highlight, element budget, Teach-mode
    /// gate on recording, preview scope and the stage attempts previewed from their start scene.
    /// </summary>
    public class SolutionsTabTests
    {
        const string k_Task = "introspection_host_42_0xbeef:1";
        const string k_TopicTask = "introspection_host_42_0xbeef";

        private FakeRosBus bus;
        private GameObject pluginGo, canvasGo, modesGo;
        private MtcPlugin plugin;
        private PickPlaceClient client;
        private MtcSolutionPlayer player;
        private FakeRobotModel robot;
        private FakeUiHost ui;
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
            player = pluginGo.AddComponent<MtcSolutionPlayer>();   // before the plugin, which finds its siblings in Awake
            plugin = pluginGo.AddComponent<MtcPlugin>();
            robot = new FakeRobotModel("fr3_joint1");
            ui = new FakeUiHost();
            yield return null;                                   // PickPlaceClient.Start subscribes
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = ui, Modes = modes, Undo = new UndoStack(), Robot = robot });
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
        public IEnumerator ManySolutions_ShowAtMostThreeInServerOrder_WithinSevenElements()
        {
            yield return Plan(8, 7, 6, 5, 4, 3, 2, 1);

            Assert.AreEqual(8, client.SolutionIds.Count);
            Assert.AreEqual(SolutionsTab.MaxSolutionButtons, tab.SolutionButtons.Count);
            Assert.LessOrEqual(tab.InteractiveElementCount, 7, "record + 3 solutions + scope + attempt + cancel");
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

        // --- preview scope ----------------------------------------------------------------

        [UnityTest]
        public IEnumerator ScopingToAStage_PreviewsOnlyItsSteps()
        {
            yield return Plan(7, 4);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, plugin.PreviewableStages, "stages with steps, in tree order");
            Assert.That(ButtonText(tab.ScopeButton), Is.EqualTo("Preview: whole solution"));

            tab.ScopeButton.onClick.Invoke();
            Assert.AreEqual(1u, plugin.PreviewStageId);
            Assert.That(ButtonText(tab.ScopeButton), Is.EqualTo("Preview: pick and place (steps 1-2)"), "the container covers its children's steps");
            tab.ScopeButton.onClick.Invoke();
            Assert.AreEqual(2u, plugin.PreviewStageId);
            Assert.That(ButtonText(tab.ScopeButton), Is.EqualTo("Preview: move to pick (step 1)"));

            robot.Applied.Clear();
            ui.Bound["preview"](plugin.Results.Single());              // the shared tier 2 verb on the handle
            yield return new WaitForSeconds(0.1f);

            Assert.IsTrue(player.IsPlaying, "a partial preview holds its end state");
            Assert.That(tab.StatusText, Does.Contain("Previewing move to pick (step 1) of solution 7"));
            var positions = robot.Applied.Select(a => a.positions[0]).ToList();
            Assert.That(positions, Has.Member(1.0).And.Member(2.0), "step 1 (stage 2) plays");
            Assert.That(positions, Has.No.Member(3.0).And.No.Member(4.0), "step 2 (stage 3) never plays");

            tab.ScopeButton.onClick.Invoke();
            tab.ScopeButton.onClick.Invoke();
            Assert.IsNull(plugin.PreviewStageId, "wraps back to the whole solution");
            player.Stop();
        }

        // --- stage attempts (partial / failed) -----------------------------------------

        [UnityTest]
        public IEnumerator PartialStageAttempt_IsFetchedWithItsStartScene_Previewed_AndNeverExecutable()
        {
            var requests = RecordGetSolution(id => new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);
            tab.ScopeButton.onClick.Invoke();
            tab.ScopeButton.onClick.Invoke();                         // stage 2: solved 100..103, none complete
            Assert.That(ButtonText(tab.AttemptButton), Is.EqualTo("Attempt: 4 to preview"));
            Assert.IsTrue(tab.AttemptButton.interactable);
            int before = requests.Count;

            tab.AttemptButton.onClick.Invoke();

            Assert.AreEqual(before + 1, requests.Count);
            Assert.AreEqual(100u, requests[^1].solution_id);
            Assert.IsTrue(requests[^1].include_start_scene, "A partial solution starts mid-task; its start scene is needed.");
            Assert.IsTrue(player.IsPlaying, "The tap must end in a preview.");
            Assert.That(plugin.PreviewStatus, Does.Contain("Previewing stage solution 100"));
            Assert.That(ButtonText(tab.AttemptButton), Is.EqualTo("Attempt: #100 partial (1/4)"));
            Assert.AreEqual(7u, plugin.SelectedSolutionId, "Partial solutions must never become the executable selection.");
            Assert.AreEqual(1, plugin.Results.Count, "and get no handle, so execute can never reach them");
            player.Stop();
        }

        [UnityTest]
        public IEnumerator FailedAttempt_ShowsWhyItFailed()
        {
            RecordGetSolution(id => new GetSolutionResponse(true, "", new SolutionMsg
            {
                sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, stage_id = 3, comment = "object in collision with table" } } },
            }));
            yield return Plan(7, 4);
            var stats = PickPlaceClientTests.Statistics(k_TopicTask, 7, 4);
            stats.stages[2].failed = new uint[] { 300 };
            bus.Inbound("/pick_place/statistics", stats);

            var attempts = plugin.AttemptsOf(3);
            Assert.AreEqual(2, attempts.Count, "the partial 200 and the failed 300");
            Assert.IsTrue(attempts[1].Failed);

            plugin.PreviewStageSolution(300, failed: true);

            Assert.That(plugin.PreviewStatus, Does.Contain("object in collision with table"));
            Assert.That(tab.StatusText, Does.Contain("object in collision with table"));
            Assert.AreEqual(7u, plugin.SelectedSolutionId);
            Assert.IsFalse(player.IsPlaying, "nothing to play; the reason is the preview");
        }

        [UnityTest]
        public IEnumerator ServerRefusingAPartialSolution_IsShown_AndTheBrowserStaysUsable()
        {
            RecordGetSolution(id => id >= 100
                ? new GetSolutionResponse(false, "unknown solution id 100 for task t", new SolutionMsg())
                : new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);

            plugin.PreviewStageSolution(100);
            Assert.That(plugin.PreviewStatus, Does.Contain("unknown solution id 100"));

            tab.SolutionButtons[1].onClick.Invoke();
            Assert.AreEqual(4u, plugin.SelectedSolutionId);
            Assert.IsNotNull(plugin.SelectedSolution, "An older server rejecting partial ids must not break the browser.");
        }

        [UnityTest]
        public IEnumerator NoAttemptPreviewWhileExecuting()
        {
            var requests = RecordGetSolution(id => new GetSolutionResponse(true, "", Solution(id)));
            yield return Plan(7, 4);
            plugin.Execute(plugin.Results.Single().Result, _ => { });
            execute.Accept();
            yield return null;
            int before = requests.Count;

            plugin.PreviewStageSolution(100);

            Assert.AreEqual(before, requests.Count);
            Assert.That(plugin.PreviewStatus, Does.Contain("executing"));
            Assert.IsFalse(tab.AttemptButton.interactable);
        }

        private System.Collections.Generic.List<GetSolutionRequest> RecordGetSolution(System.Func<uint, GetSolutionResponse> respond)
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

        // Two steps: stage 2 moves fr3_joint1 from 1 to 2, stage 3 from 3 to 4.
        private static SolutionMsg Solution(uint id) => new SolutionMsg
        {
            sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, cost = 1f, stage_id = 1 } } },
            sub_trajectory = new[]
            {
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 10, cost = 0.5f, stage_id = 2 }, trajectory = Motion(1.0, 2.0) },
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 20, cost = 0.5f, stage_id = 3 }, trajectory = Motion(3.0, 4.0) },
            }
        };

        private static RobotTrajectoryMsg Motion(double from, double to) => new RobotTrajectoryMsg
        {
            joint_trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "fr3_joint1" },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { from } },
                    new JointTrajectoryPointMsg { positions = new[] { to }, time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg(0, 10_000_000) },
                }
            }
        };
    }
}
