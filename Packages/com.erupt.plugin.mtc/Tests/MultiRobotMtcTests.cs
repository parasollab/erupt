using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.EruptInterfaces;
using RosMessageTypes.Trajectory;
using GetSolutionRequest = RosMessageTypes.EruptInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.EruptInterfaces.GetSolutionResponse;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// One MTC plugin, three robots (left, right, third), each with its own pick-and-place
    /// server in its namespace: sessions, wire names, solutions per robot, the facade following
    /// the active robot, and a robot whose ROS side has no server.
    /// </summary>
    public class MultiRobotMtcTests
    {
        const string k_Task = "introspection_host_42_0xbeef:1";
        const string k_TopicTask = "introspection_host_42_0xbeef";

        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private GameObject pluginGo, canvasGo;
        private MtcPlugin plugin;
        private PickPlaceClient ownClient;
        private FakeUiHost ui;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            foreach (string ns in FakeRobots.ThreeIds)
            {
                string captured = ns;
                bus.SetServiceHandler($"/{ns}/get_solution", request =>
                    new GetSolutionResponse(true, "", Solution(((GetSolutionRequest)request).solution_id, captured + "_joint1")));
            }

            pluginGo = new GameObject("mtc");
            ownClient = pluginGo.AddComponent<PickPlaceClient>();
            pluginGo.AddComponent<PickPlaceTaskRecorder>();
            pluginGo.AddComponent<MtcSolutionPlayer>();   // before the plugin, which finds its siblings in Awake
            plugin = pluginGo.AddComponent<MtcPlugin>();
            ui = new FakeUiHost();
            yield return null;

            robots = FakeRobots.Three(bus);
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = ui, Undo = new UndoStack(), Robots = robots });
            yield return null;   // Start of the copies made for the second and third robot: they connect
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            if (canvasGo != null) Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(pluginGo);
            FakeRobots.DestroyAll(robots);
        }

        private FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> PlanAction(string ns) =>
            bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>($"/{ns}/pick_place");

        private FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> ExecuteAction(string ns) =>
            bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>($"/{ns}/execute_solution");

        // Runs a plan on one robot's server, which answers with these solution ids.
        private IEnumerator Plan(RobotInstance robot, params uint[] ids)
        {
            string ns = robot.Namespace;
            Task goal = plugin.SessionFor(robot).PickPlace.PlanAsync("object", new PoseStampedMsg());
            var accepted = PlanAction(ns).Accept();
            yield return null;
            PlanAction(ns).Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound($"/{ns}/pick_place/description", PickPlaceClientTests.Description(k_TopicTask));
            bus.Inbound($"/{ns}/pick_place/statistics", PickPlaceClientTests.Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0, new MoveItErrorCodesMsg()));
            while (!goal.IsCompleted) yield return null;
            yield return null;
        }

        // Record, then Stop with nothing grabbed and put down: there is no task to send, and
        // the user is told so where they work instead of in the log.
        [Test]
        public void ARecordingThatEndsWithNothingToSend_IsARefusal_OnThatRobot()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            var selectionGo = new GameObject("selection");
            try
            {
                var router = routerGo.AddComponent<InteractionRouter>();
                var refusals = new System.Collections.Generic.List<InteractionRefusal>();
                router.Refused += refusals.Add;
                ((EruptContext)plugin.Context).Router = router;
                selectionGo.AddComponent<SelectionService>();
                var highlighter = selectionGo.AddComponent<Erupt.Environment.SelectionHighlighter>();
                var tool = new GameObject("tool");
                tool.transform.SetParent(right.transform, false);
                tool.transform.position = new Vector3(2f, 1f, 0.5f);
                ((FakeRobotModel)right.Model).EndEffector = tool.transform;

                PickPlaceTaskRecorder recorder = plugin.SessionFor(right).Recorder;
                typeof(PickPlaceTaskRecorder).GetField("selectionManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(recorder, highlighter);
                recorder.StartRecording();
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("nothing sent"));
                recorder.StopRecording();

                Assert.AreEqual(1, refusals.Count, "It used to be a line in the log.");
                StringAssert.Contains("No pick & place was recorded: no object was selected", refusals[0].Reason);
                Assert.AreEqual(tool.transform.position, refusals[0].WorldPoint, "At the end effector of the robot it was recorded for.");
            }
            finally
            {
                ((EruptContext)plugin.Context).Router = null;
                Object.DestroyImmediate(routerGo);
                Object.DestroyImmediate(selectionGo);
            }
        }

        [Test]
        public void EveryRobot_HasItsOwnClientPlayerAndRecorder_InItsOwnNamespace()
        {
            Assert.AreEqual(3, plugin.Sessions.Count);
            Assert.AreEqual(new[] { "planner-mtc" }, ui.Tabs, "One plugin, one tab.");

            foreach (string ns in FakeRobots.ThreeIds)
            {
                CollectionAssert.Contains(bus.Subscriptions, $"/{ns}/pick_place/description");
                CollectionAssert.Contains(bus.Subscriptions, $"/{ns}/pick_place/statistics");
                CollectionAssert.Contains(bus.RegisteredServices, $"/{ns}/get_solution");
                Assert.IsNotNull(PlanAction(ns));
                Assert.IsNotNull(ExecuteAction(ns));
            }
            Assert.AreEqual(6, bus.Subscriptions.Count, "Nothing is subscribed outside the robots' namespaces.");

            var sessions = plugin.Sessions.ToList();
            Assert.AreSame(ownClient, sessions[0].PickPlace, "The plugin's own components serve the first robot.");
            Assert.AreEqual(3, sessions.Select(s => s.PickPlace).Distinct().Count());
            Assert.AreEqual(3, sessions.Select(s => s.Player).Distinct().Count());
            Assert.AreEqual(3, sessions.Select(s => s.Recorder).Distinct().Count());
            Assert.IsTrue(sessions.All(s => s.PickPlace != null && s.Player != null && s.Recorder != null));
            Assert.IsTrue(sessions.All(s => s.Available));
        }

        [UnityTest]
        public IEnumerator Solutions_BelongToTheRobotWhoseServerPlannedThem()
        {
            yield return Plan(third, 5, 6);

            MtcPlugin.RobotSession thirdSession = plugin.SessionFor(third);
            Assert.AreEqual(5u, thirdSession.SelectedSolutionId);
            Assert.IsNull(plugin.SessionFor(left).SelectedSolutionId);
            Assert.IsNull(plugin.SelectedSolutionId, "The facade is the active robot's, which planned nothing.");
            Assert.AreEqual(0, plugin.SessionFor(left).PickPlace.SolutionIds.Count);
            Assert.AreEqual(0, plugin.SessionFor(right).PickPlace.SolutionIds.Count);

            TrajectorySelectable solution = plugin.Results.Single();
            Assert.AreEqual("third", solution.RobotId);
            Assert.AreSame(third, robots.OwnerOf(solution.gameObject));
            StringAssert.Contains("third", solution.DisplayName);

            // Executing it goes to the third robot's server and to no other.
            ((FakeUiHost)plugin.Context.Ui).Bound["execute"](solution);
            Assert.IsNotNull(ExecuteAction("third").LastGoal);
            Assert.AreEqual(5u, ExecuteAction("third").LastGoal.solution_id);
            Assert.IsNull(ExecuteAction("left").LastGoal);
            Assert.IsNull(ExecuteAction("right").LastGoal);
            ExecuteAction("third").Accept().Complete(RosActionGoalStatus.Succeeded, new ExecuteSolutionResult());
            yield return null;

            // A second robot plans: both keep their own solutions.
            yield return Plan(left, 9);
            Assert.AreEqual(9u, plugin.SelectedSolutionId, "The left robot is active.");
            Assert.AreEqual(5u, thirdSession.SelectedSolutionId);
            CollectionAssert.AreEquivalent(new[] { "left", "third" }, plugin.Results.Select(r => r.RobotId));

            // A new plan on one robot clears that robot's solutions only.
            yield return Plan(third, 7);
            CollectionAssert.AreEquivalent(new[] { "left", "third" }, plugin.Results.Select(r => r.RobotId));
            Assert.AreEqual(7u, thirdSession.SelectedSolutionId);
            Assert.AreEqual(9u, plugin.SessionFor(left).SelectedSolutionId);
        }

        [UnityTest]
        public IEnumerator MovingARobotsBase_ForgetsItsTask_IsRefusedWhileItIsBusy_AndLeavesTheOthersAlone()
        {
            yield return Plan(third, 5, 6);
            yield return Plan(left, 9);
            var elsewhere = new Pose(new Vector3(1f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f));

            // Executing: the solution was found for the base where it is.
            ((FakeUiHost)plugin.Context.Ui).Bound["execute"](plugin.Results.Single(r => r.RobotId == "third"));
            var execution = ExecuteAction("third").Accept();
            yield return null;
            Assert.IsFalse(third.PlaceBase(elsewhere, out string reason));
            Assert.AreEqual("Robot 'third' is executing a pick-and-place solution.", reason);
            Assert.AreEqual(Vector3.zero, third.transform.position);
            execution.Complete(RosActionGoalStatus.Succeeded, new ExecuteSolutionResult());
            yield return null;
            yield return null;

            // Planning: the same.
            Task planning = plugin.SessionFor(right).PickPlace.PlanAsync("object", new PoseStampedMsg());
            var accepted = PlanAction("right").Accept();
            yield return null;
            Assert.IsFalse(right.PlaceBase(elsewhere, out reason));
            Assert.AreEqual("A pick-and-place task is being planned for robot 'right'.", reason);
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0, new MoveItErrorCodesMsg()));
            while (!planning.IsCompleted) yield return null;
            yield return null;

            // Idle: the base moves, and the task planned for the old base is forgotten.
            Assert.IsTrue(third.PlaceBase(elsewhere, out reason), reason);
            MtcPlugin.RobotSession thirdSession = plugin.SessionFor(third);
            Assert.AreEqual(0, thirdSession.PickPlace.SolutionIds.Count);
            Assert.IsNull(thirdSession.SelectedSolutionId);
            Assert.IsNull(plugin.LastResultFor(third));
            Assert.IsFalse(plugin.Results.Any(r => r.RobotId == "third"));
            Assert.AreEqual(9u, plugin.SessionFor(left).SelectedSolutionId, "The other robots keep their tasks.");
            Assert.IsTrue(plugin.Results.Any(r => r.RobotId == "left"));
        }

        [UnityTest]
        public IEnumerator TheFacadeAndTheTab_FollowTheActiveRobot()
        {
            yield return Plan(third, 5);
            Assert.AreEqual("MTC · left", ui.TabsById["planner-mtc"].Label);
            Assert.AreSame(plugin.SessionFor(left).PickPlace, plugin.PickPlace);

            robots.SetActive(third);

            Assert.AreEqual("MTC · third", ui.TabsById["planner-mtc"].Label);
            Assert.AreSame(plugin.SessionFor(third).PickPlace, plugin.PickPlace);
            Assert.AreSame(plugin.SessionFor(third).Player, plugin.Player);
            Assert.AreEqual(5u, plugin.SelectedSolutionId);
        }

        [UnityTest]
        public IEnumerator ARobotWithoutAPickAndPlaceServer_SaysSo_AndDoesNotBlockTheOthers()
        {
            // The right robot's ROS side runs no pick-and-place server: its action never becomes ready.
            PlanAction("right").SetState(RosActionClientState.WaitingForConnection);

            Assert.IsFalse(plugin.SessionFor(right).Available);
            Assert.IsTrue(plugin.SessionFor(left).Available);
            Assert.IsTrue(plugin.SessionFor(third).Available);

            // Its tab says so in plain words and offers no recording.
            robots.SetActive(right);
            var canvas = UiBuilder.CreateWorldCanvas("Tier3", null, new Vector2(800f, 900f));
            canvasGo = canvas.gameObject;
            var rightTab = new SolutionsTab(plugin, UiBuilder.CreatePanel("Right", canvas.transform, Color.clear));
            Assert.AreEqual(SolutionsTab.UnavailableText, rightTab.StatusText);
            var record = rightTab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Record");
            Assert.IsFalse(record.interactable);
            rightTab.Dispose();

            // The others plan as usual, and their tab reads as usual.
            yield return Plan(third, 5);
            Assert.AreEqual(5u, plugin.SessionFor(third).SelectedSolutionId);
            robots.SetActive(third);
            var thirdTab = new SolutionsTab(plugin, UiBuilder.CreatePanel("Third", canvas.transform, Color.clear));
            Assert.AreNotEqual(SolutionsTab.UnavailableText, thirdTab.StatusText);
            var thirdRecord = thirdTab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Record");
            Assert.IsTrue(thirdRecord.interactable);
            thirdTab.Dispose();

            // When the server appears, the robot becomes available.
            PlanAction("right").SetState(RosActionClientState.Ready);
            Assert.IsTrue(plugin.SessionFor(right).Available);
        }

        [UnityTest]
        public IEnumerator ARobotThatLeaves_TakesItsSessionWithIt_AndTheOthersKeepTheirs()
        {
            yield return Plan(right, 4);
            yield return Plan(third, 5);
            PickPlaceClient rightClient = plugin.SessionFor(right).PickPlace;

            Object.DestroyImmediate(right.gameObject);
            yield return null;

            Assert.AreEqual(2, plugin.Sessions.Count);
            Assert.IsTrue(rightClient == null, "The copy made for the robot goes with it.");
            CollectionAssert.AreEqual(new[] { "third" }, plugin.Results.Select(r => r.RobotId));
            Assert.AreEqual(5u, plugin.SessionFor(third).SelectedSolutionId);
            Assert.IsTrue(ownClient != null && plugin.SessionFor(left).PickPlace == ownClient);
        }

        // Two steps on one joint of the robot the solution is for.
        private static SolutionMsg Solution(uint id, string joint) => new SolutionMsg
        {
            sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, cost = 1f, stage_id = 1 } } },
            sub_trajectory = new[]
            {
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 10, cost = 0.5f, stage_id = 2 }, trajectory = Motion(joint, 0.1, 0.2) },
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 20, cost = 0.5f, stage_id = 3 }, trajectory = Motion(joint, 0.3, 0.4) },
            }
        };

        private static RobotTrajectoryMsg Motion(string joint, double from, double to) => new RobotTrajectoryMsg
        {
            joint_trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { joint },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { from } },
                    new JointTrajectoryPointMsg { positions = new[] { to }, time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg(0, 10_000_000) },
                }
            }
        };
    }
}
