using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>
    /// What a first-time user meets, with three robots on the fake bus: each robot shows where
    /// its ROS robot is as soon as that is known (once, unless the user got there first), and
    /// pressing Plan or Execute when it cannot work says why instead of doing nothing.
    /// </summary>
    public class MoveItFirstRunTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private readonly List<RobotProfile> profiles = new();
        private GameObject pluginGo, sceneHost, routerGo, selectionGo;
        private MoveItPlugin plugin;
        private FakeUiHost ui;
        private InteractionRouter router;
        private SelectionService selection;
        private readonly List<InteractionRefusal> refusals = new();

        // Built by each test, so a test can set the plugin's switches and the robots' limits first.
        private IEnumerator Build(bool adopt = true, System.Func<string, IRobotModel> model = null, string commandTopic = "arm_controller/joint_trajectory")
        {
            bus = new FakeRosBus();
            profiles.Clear();
            robots = FakeRobots.Three(bus, model, id =>
            {
                var profile = ScriptableObject.CreateInstance<RobotProfile>();
                profile.armGroup = id + "_arm";
                profile.trajectoryCommandTopic = commandTopic;
                profiles.Add(profile);
                return profile;
            });
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];
            foreach (RobotInstance robot in new[] { left, right }) robot.gameObject.AddComponent<JointTrajectoryPlayer>();

            sceneHost = new GameObject("planning scene");
            sceneHost.AddComponent<CollisionObjectsListenerSimple>().requestInitialPlanningScene = false;
            yield return null;

            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            router.RefusalCoalesceSeconds = 0f;
            refusals.Clear();
            router.Refused += refusals.Add;
            selectionGo = new GameObject("selection");
            selection = selectionGo.AddComponent<SelectionService>();

            foreach (string id in FakeRobots.ThreeIds)
            {
                string ns = id;
                bus.SetServiceHandler($"/{ns}/plan_kinematic_path", _ => new GetMotionPlanResponse
                {
                    motion_plan_response = new MotionPlanResponseMsg
                    {
                        error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                        trajectory = new RobotTrajectoryMsg
                        {
                            joint_trajectory = new JointTrajectoryMsg
                            {
                                joint_names = new[] { ns + "_joint1", ns + "_joint2" },
                                points = new[]
                                {
                                    new JointTrajectoryPointMsg { positions = new[] { 0.0, 0.0 } },
                                    new JointTrajectoryPointMsg { positions = new[] { 0.1, 0.2 } }
                                }
                            }
                        }
                    }
                });
            }

            ui = new FakeUiHost();
            pluginGo = new GameObject("moveit");
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            plugin.AdoptRobotStateOnConnect = adopt;
            ((IEruptPlugin)plugin).OnRegister(new EruptContext
            {
                Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack(), Router = router, Selection = selection
            });
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(sceneHost);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(selectionGo);
            FakeRobots.DestroyAll(robots);
            foreach (RobotProfile profile in profiles) Object.DestroyImmediate(profile);
            foreach (GameObject prop in previewProps) if (prop != null) Object.DestroyImmediate(prop);
            previewProps.Clear();
            foreach (TranslucentOverride ghost in Object.FindObjectsByType<TranslucentOverride>(FindObjectsSortMode.None))
                if (ghost != null) Object.DestroyImmediate(ghost.gameObject);
        }

        private static FakeRobotModel Model(RobotInstance robot) => (FakeRobotModel)robot.Model;

        private void Report(RobotInstance robot, params (string joint, double position)[] joints) =>
            bus.Inbound($"/{robot.Id}/joint_states", new JointStateMsg
            {
                name = joints.Select(j => $"{robot.Id}_{j.joint}").ToArray(),
                position = joints.Select(j => j.position).ToArray()
            });

        private void ReportAll(RobotInstance robot, double joint1, double joint2, double finger) =>
            Report(robot, ("joint1", joint1), ("joint2", joint2), ("finger", finger));

        // ---------- the robot shows where the ROS robot is ----------

        [UnityTest]
        public IEnumerator TheFirstJointState_IsShownOnTheRobot_OncePerRobot()
        {
            yield return Build();
            Assert.IsTrue(plugin.AdoptRobotStateOnConnect, "On by default.");
            var adopted = new List<string>();
            foreach (RobotInstance robot in robots.All)
            {
                string id = robot.Id;
                plugin.SessionFor(robot).Client.StateAdopted += () => adopted.Add(id);
                Assert.IsTrue(plugin.SessionFor(robot).Client.IsWaitingToAdopt);
            }

            ReportAll(right, 0.4, -0.3, 0.02);
            CollectionAssert.AreEqual(new[] { 0.4f, -0.3f, 0.02f }, Model(right).Positions, "The robot stands where its ROS robot stands.");
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f }, Model(left).Positions, "Nobody else's robot moved.");
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f }, Model(third).Positions);
            Assert.IsFalse(plugin.SessionFor(right).Client.IsWaitingToAdopt);
            Assert.IsFalse(plugin.SessionFor(right).Client.IsMirroring, "It is one state, not a mirror.");

            // Once: the next state the ROS robot reports is not shown (the mirror is off).
            ReportAll(right, 0.9, 0.9, 0.0);
            CollectionAssert.AreEqual(new[] { 0.4f, -0.3f, 0.02f }, Model(right).Positions);

            // Every robot gets its own.
            ReportAll(left, -0.1, 0.2, 0.01);
            ReportAll(third, 1.0, 1.1, 0.03);
            CollectionAssert.AreEqual(new[] { -0.1f, 0.2f, 0.01f }, Model(left).Positions);
            CollectionAssert.AreEqual(new[] { 1.0f, 1.1f, 0.03f }, Model(third).Positions);
            CollectionAssert.AreEqual(new[] { "right", "left", "third" }, adopted);
            Assert.IsEmpty(refusals);
        }

        [UnityTest]
        public IEnumerator ARobotTheUserMovedFirst_KeepsTheUsersPose()
        {
            yield return Build();
            // The user drags left before its ROS side has said anything; right gets a preview.
            Model(left).BeginInteraction();
            Model(left).Positions[0] = 0.7f;
            Model(left).EndInteraction();
            Model(right).TakeControl();

            ReportAll(left, 0.1, 0.1, 0.0);
            ReportAll(right, 0.2, 0.2, 0.0);
            ReportAll(third, 0.3, 0.3, 0.0);

            Assert.AreEqual(0.7f, Model(left).Positions[0], "What the user did stands.");
            Assert.IsEmpty(Model(left).Applied);
            Assert.IsEmpty(Model(right).Applied);
            CollectionAssert.AreEqual(new[] { 0.3f, 0.3f, 0f }, Model(third).Positions, "A robot nobody touched still adopts its state.");
        }

        [UnityTest]
        public IEnumerator AfterAReconnect_TheStateIsAdoptedAgain_UnlessTheUserMovedTheRobotSince()
        {
            yield return Build();
            ReportAll(left, 0.1, 0.1, 0.0);
            ReportAll(right, 0.2, 0.2, 0.0);
            // The user poses left; then ROS goes away and comes back (restarted, say).
            Model(left).BeginInteraction();
            Model(left).Positions[0] = 0.7f;
            Model(left).EndInteraction();

            plugin.ResyncWithRos();
            Assert.IsTrue(plugin.SessionFor(left).Client.IsWaitingToAdopt);
            // After the reconnect the user moves right before its first state arrives.
            Model(right).BeginInteraction();
            Model(right).EndInteraction();

            ReportAll(left, -0.5, -0.5, 0.01);
            ReportAll(right, -0.6, -0.6, 0.01);
            CollectionAssert.AreEqual(new[] { -0.5f, -0.5f, 0.01f }, Model(left).Positions,
                "Not moved since the reconnect: the robot shows where the ROS robot is now.");
            CollectionAssert.AreEqual(new[] { 0.2f, 0.2f, 0f }, Model(right).Positions, "Moved since the reconnect: left alone.");
        }

        [UnityTest]
        public IEnumerator AStartTheUserHasSet_IsKept()
        {
            yield return Build();
            Model(left).Positions[0] = 0.25f;   // the profile's initial pose, say
            plugin.SetStartFromRobot(left);
            Assert.IsTrue(plugin.SessionFor(left).HasStart);

            ReportAll(left, 0.9, 0.8, 0.0);

            CollectionAssert.AreEqual(new[] { 0.9f, 0.8f, 0f }, Model(left).Positions, "The robot adopts the reported state.");
            MoveItRobotSession session = plugin.SessionFor(left);
            Assert.IsTrue(session.HasStart, "The start was the user's choice.");
            Assert.AreEqual(0.25, session.StartState.joint_state.position[0], 1e-6, "And it is the pose they chose.");
        }

        [UnityTest]
        public IEnumerator AStateOutsideTheModelsLimits_IsShownClamped_AndReported()
        {
            yield return Build(model: id => new FakeRobotModel(id + "_joint1", id + "_joint2").WithGripper(id + "_finger")
                .WithLimits(new JointLimit(id + "_joint1", JointKind.Revolute, -1f, 1f)));

            ReportAll(left, 2.0, 0.5, 0.0);

            Assert.AreEqual(1f, Model(left).Positions[0], "Clamped like every other joint write.");
            Assert.AreEqual(0.5f, Model(left).Positions[1]);
            Assert.AreEqual(1, refusals.Count, "The user is told the model and the robot disagree.");
            Assert.AreEqual(RefusalCode.LimitMismatch, refusals[0].Code);
            StringAssert.Contains("left_joint1", refusals[0].Reason);
        }

        [UnityTest]
        public IEnumerator JointsReportedBySeparatePublishers_AreEachAdoptedOnce()
        {
            yield return Build();
            // The gripper's publisher first, then the arm's.
            Report(left, ("finger", 0.03));
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0.03f }, Model(left).Positions);
            Assert.IsTrue(plugin.SessionFor(left).Client.IsWaitingToAdopt, "The arm has not been heard yet.");

            Report(left, ("finger", 0.01));
            Assert.AreEqual(0.03f, Model(left).Positions[2], "Each joint once.");

            Report(left, ("joint1", 0.4), ("joint2", 0.5));
            CollectionAssert.AreEqual(new[] { 0.4f, 0.5f, 0.03f }, Model(left).Positions);
            Assert.IsFalse(plugin.SessionFor(left).Client.IsWaitingToAdopt);

            Report(left, ("joint1", 0.9), ("joint2", 0.9));
            CollectionAssert.AreEqual(new[] { 0.4f, 0.5f, 0.03f }, Model(left).Positions);
        }

        [UnityTest]
        public IEnumerator TheCheckbox_TurnsItOff_AndTheMirrorMakesItMoot()
        {
            yield return Build(adopt: false);
            ReportAll(left, 0.4, 0.4, 0.0);
            Assert.IsEmpty(Model(left).Applied, "Off: the robot stays in its initial pose until the mirror is switched on.");
            Assert.IsFalse(plugin.SessionFor(left).Client.IsWaitingToAdopt);

            // Switched on at runtime, it applies from the next connection.
            plugin.AdoptRobotStateOnConnect = true;
            plugin.ResyncWithRos();
            plugin.SessionFor(right).Client.SetMirroring(true);
            ReportAll(left, 0.5, 0.5, 0.0);
            ReportAll(right, 0.6, 0.6, 0.0);
            ReportAll(right, 0.7, 0.7, 0.0);
            CollectionAssert.AreEqual(new[] { 0.5f, 0.5f, 0f }, Model(left).Positions);
            CollectionAssert.AreEqual(new[] { 0.7f, 0.7f, 0f }, Model(right).Positions, "A mirror shows every state.");
            Assert.IsFalse(plugin.SessionFor(right).Client.IsWaitingToAdopt);
        }

        // ---------- buttons that used to do nothing ----------

        [UnityTest]
        public IEnumerator Plan_ForARobotMoveItDoesNotServe_OrForNoRobot_IsARefusal()
        {
            yield return Build();
            string onTab = null;
            plugin.PlanFailed += m => onTab = m;

            // No robot at all (the tab's Plan button with no active robot).
            PlanResult result = new PlanResult();
            plugin.RequestPlan(null, new PlanPreferences(), r => result = r);
            Assert.IsNull(result);
            Assert.AreEqual(1, refusals.Count, "RequestPlan used to return without a word.");
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals[0].Code);
            Assert.AreEqual("There is no robot to plan for.", refusals[0].Reason);
            Assert.AreEqual("There is no robot to plan for.", onTab, "The tab shows it too.");

            // A robot the plugin has no session for.
            var elsewhere = FakeRobots.Registry(bus);
            RobotInstance stranger = FakeRobots.Add(elsewhere, "stranger", "stranger", new FakeRobotModel("j1"));
            var tool = new GameObject("tool");
            ((FakeRobotModel)stranger.Model).EndEffector = tool.transform;
            tool.transform.position = new Vector3(3f, 2f, 1f);
            try
            {
                result = new PlanResult();
                plugin.RequestPlan(stranger, new PlanPreferences(), r => result = r);
                Assert.IsNull(result);
                Assert.AreEqual(2, refusals.Count);
                Assert.AreEqual("MoveIt is not connected for robot 'stranger'.", refusals[1].Reason);
                Assert.AreEqual(tool.transform.position, refusals[1].WorldPoint, "At that robot's end effector.");
            }
            finally
            {
                FakeRobots.DestroyAll(elsewhere);
                Object.DestroyImmediate(tool);
            }
        }

        [UnityTest]
        public IEnumerator Execute_OnARobotWithNoCommandTopic_SaysSo_AtThePlan()
        {
            yield return Build(commandTopic: "");
            plugin.SetStartFromRobot(left);
            plugin.SetGoalFromRobot(left);
            PlanResult plan = null;
            plugin.RequestPlan(left, new PlanPreferences(), r => plan = r);
            Assert.IsNotNull(plan);
            TrajectorySelectable handle = plugin.Results.Single(r => r.Result == plan);
            handle.transform.position = new Vector3(1f, 1.5f, 0.5f);
            refusals.Clear();

            ui.Bound["execute"](handle);

            Assert.AreEqual(1, refusals.Count, "The execute verb used to drop the status.");
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals[0].Code);
            Assert.AreEqual("The robot's profile names no trajectory command topic.", refusals[0].Reason);
            Assert.AreEqual(handle.transform.position, refusals[0].WorldPoint);
            Assert.AreEqual(0, bus.Publishes.Count(p => p.Topic.EndsWith("joint_trajectory")));
        }

        [UnityTest]
        public IEnumerator Preview_OnARobotThatCannotPlayOne_SaysSo()
        {
            yield return Build();
            // third has no trajectory player.
            plugin.SetStartFromRobot(third);
            plugin.SetGoalFromRobot(third);
            refusals.Clear();
            PlanResult plan = null;
            plugin.RequestPlan(third, new PlanPreferences(), r => plan = r);
            Assert.IsNotNull(plan);

            Assert.AreEqual(1, refusals.Count, "The preview that starts with a new plan could not play.");
            Assert.AreEqual(RefusalCode.NotControllable, refusals[0].Code);
            StringAssert.Contains("'third' cannot play a preview", refusals[0].Reason);
        }

        [UnityTest]
        public IEnumerator ANewPlan_BecomesTheSelection_ForTheRobotTheUserIsWorkingWith()
        {
            yield return Build();
            Assert.AreSame(left, robots.Active);
            foreach (RobotInstance robot in new[] { left, right })
            {
                plugin.SetStartFromRobot(robot);
                plugin.SetGoalFromRobot(robot);
            }

            plugin.RequestPlan(right, new PlanPreferences(), _ => { });
            Assert.IsNull(selection.Current, "A plan for another robot leaves the selection alone.");

            PlanResult plan = null;
            plugin.RequestPlan(left, new PlanPreferences(), r => plan = r);
            Assert.AreSame(plugin.Results.Single(r => r.Result == plan), selection.Current, "Its menu (Preview, Execute) is there.");

            plugin.SelectNewPlan = false;
            selection.ClearSelection();
            plugin.RequestPlan(left, new PlanPreferences(), _ => { });
            Assert.IsNull(selection.Current, "The checkbox leaves the selection where it was.");
        }

        // ---------- the preview: a blue copy moves, the robot does not ----------

        private readonly List<GameObject> previewProps = new();

        // Gives a fake robot what a shipped robot has: a ghost prefab whose bodies are named
        // like the links the robot's joints move.
        private RobotGhosts GiveGhosts(RobotInstance robot)
        {
            var prefab = new GameObject(robot.Id + " ghost prefab");
            previewProps.Add(prefab);
            prefab.AddComponent<ArticulationBody>();
            Transform parent = prefab.transform;
            foreach (string joint in new[] { "joint1", "joint2" })
            {
                string linkName = $"{robot.Id}_link_of_{joint}";
                var body = new GameObject(linkName);
                body.transform.SetParent(parent, false);
                body.transform.localPosition = Vector3.up * 0.2f;
                ArticulationBody articulation = body.AddComponent<ArticulationBody>();
                articulation.jointType = ArticulationJointType.RevoluteJoint;
                parent = body.transform;

                var link = new GameObject(linkName);
                link.transform.SetParent(robot.transform, false);
                Model(robot).JointLinks[$"{robot.Id}_{joint}"] = link.transform;
            }
            prefab.SetActive(false);

            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();
            ghosts.robotPrefab = prefab;
            return ghosts;
        }

        private string[] PlanMenu(TrajectorySelectable handle) =>
            ui.Verbs.For(SelectionKind.Trajectory, handle).Where(v => v.IsAvailable).Select(v => v.Id).ToArray();

        [UnityTest]
        public IEnumerator APlan_IsPreviewedOnABlueCopy_TheRobotStaysPut_AndStopPreviewStopsIt()
        {
            yield return Build();
            RobotGhosts ghosts = GiveGhosts(left);
            Model(left).Positions[0] = 0.05f;
            plugin.SetStartFromRobot(left);
            plugin.SetGoalFromRobot(left);
            int writesBefore = Model(left).Applied.Count;

            PlanResult plan = null;
            plugin.RequestPlan(left, new PlanPreferences(), r => plan = r);
            Assert.IsNotNull(plan);
            TrajectorySelectable handle = plugin.Results.Single(r => r.Result == plan);

            // The new plan plays by itself, on the copy.
            Assert.IsTrue(plugin.SessionFor(left).IsPreviewing);
            Assert.IsTrue(plugin.IsPreviewPlaying(plan));
            Assert.IsTrue(ghosts.IsPreviewPlaying, "The preview plays on the robot's blue copy.");
            GameObject copy = ghosts.PreviewGhost;
            ArticulationBody copyJoint2 = copy.GetComponentsInChildren<ArticulationBody>().Single(b => b.name == "left_link_of_joint2");

            bool moved = false;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!moved && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                moved = Mathf.Abs(copyJoint2.jointPosition[0] - 0.2f) < 1e-3f;
            }
            Assert.IsTrue(moved, "The copy moves along the plan.");
            Assert.AreEqual(writesBefore, Model(left).Applied.Count, "Nothing was written to the robot itself.");
            Assert.AreEqual(0.05f, Model(left).Positions[0], 1e-6f);
            Assert.IsFalse(left.TrajectoryPlayer.IsReplaying, "The robot's own player is not used.");

            // While it plays, the plan's menu offers to stop it where it offered to play it.
            CollectionAssert.Contains(PlanMenu(handle), PlanningPlugin.StopPreviewVerb);
            CollectionAssert.DoesNotContain(PlanMenu(handle), "preview");
            CollectionAssert.Contains(PlanMenu(handle), "execute");

            int menuRebuilds = 0;
            ui.Verbs.Changed += () => menuRebuilds++;
            ui.Bound[PlanningPlugin.StopPreviewVerb](handle);
            Assert.IsFalse(plugin.SessionFor(left).IsPreviewing, "Stop Preview stops it.");
            Assert.IsFalse(plugin.IsPreviewPlaying(plan));
            Assert.IsFalse(ghosts.IsPreviewPlaying);
            Assert.Greater(menuRebuilds, 0, "The open menu is told to build itself again.");
            CollectionAssert.Contains(PlanMenu(handle), "preview");
            CollectionAssert.DoesNotContain(PlanMenu(handle), PlanningPlugin.StopPreviewVerb);
            yield return null;
            Assert.IsTrue(copy == null, "The copy is gone.");
            Assert.IsEmpty(refusals);

            // Preview plays it again; Execute ends the preview and leaves the robot to the real one.
            ui.Bound["preview"](handle);
            Assert.IsTrue(plugin.IsPreviewPlaying(plan));
            Assert.IsTrue(ghosts.IsPreviewPlaying);
            CollectionAssert.Contains(PlanMenu(handle), PlanningPlugin.StopPreviewVerb);
            ui.Bound["execute"](handle);
            Assert.IsFalse(plugin.IsPreviewPlaying(plan), "Executing a plan ends its preview.");
            Assert.IsFalse(ghosts.IsPreviewPlaying);
            Assert.AreEqual(1, bus.CountOn("/left/arm_controller/joint_trajectory"), "The plan went to the robot.");
        }

        [UnityTest]
        public IEnumerator StopPreview_OnlyShowsOnThePlanThatIsPlaying_AndAResetOrANewPlanEndsThePreview()
        {
            yield return Build();
            RobotGhosts ghosts = GiveGhosts(left);
            plugin.SetStartFromRobot(left);
            plugin.SetGoalFromRobot(left);
            PlanResult first = null, second = null;
            plugin.RequestPlan(left, new PlanPreferences(), r => first = r);
            plugin.RequestPlan(left, new PlanPreferences(), r => second = r);
            TrajectorySelectable firstHandle = plugin.Results.Single(r => r.Result == first);
            TrajectorySelectable secondHandle = plugin.Results.Single(r => r.Result == second);

            Assert.IsTrue(plugin.IsPreviewPlaying(second), "The newest plan is the one that plays.");
            Assert.IsFalse(plugin.IsPreviewPlaying(first));
            CollectionAssert.Contains(PlanMenu(secondHandle), PlanningPlugin.StopPreviewVerb);
            CollectionAssert.DoesNotContain(PlanMenu(firstHandle), PlanningPlugin.StopPreviewVerb);
            CollectionAssert.Contains(PlanMenu(firstHandle), "preview");

            // Previewing the other plan moves the preview to it.
            ui.Bound["preview"](firstHandle);
            Assert.IsTrue(plugin.IsPreviewPlaying(first));
            Assert.IsFalse(plugin.IsPreviewPlaying(second));
            Assert.IsTrue(ghosts.IsPreviewPlaying);
            yield return null;
            Assert.AreEqual(1, Object.FindObjectsByType<TranslucentOverride>(FindObjectsSortMode.None).Count(o => o.name == "PreviewGhost"),
                "One copy plays, however many previews were asked for.");

            plugin.ResetPlanningState(left);
            Assert.IsFalse(plugin.SessionFor(left).IsPreviewing, "Resetting start and goal ends the preview.");
            Assert.IsFalse(ghosts.IsPreviewPlaying);
        }

        [UnityTest]
        public IEnumerator ARobotWithoutAGhostPrefab_PreviewsOnItself_AndGetsItsPoseBackWhenStopped()
        {
            yield return Build();
            // right has a trajectory player and no ghosts.
            Model(right).Positions[0] = 0.7f;
            Model(right).Positions[1] = -0.4f;
            plugin.SetStartFromRobot(right);
            plugin.SetGoalFromRobot(right);
            PlanResult plan = null;
            plugin.RequestPlan(right, new PlanPreferences(), r => plan = r);
            TrajectorySelectable handle = plugin.Results.Single(r => r.Result == plan);
            Assert.IsTrue(plugin.IsPreviewPlaying(plan));

            bool moved = false;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!moved && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                moved = Mathf.Abs(Model(right).Positions[1] - 0.2f) < 1e-3f;
            }
            Assert.IsTrue(moved, "With no copy to show it on, the plan plays on the robot.");

            ui.Bound[PlanningPlugin.StopPreviewVerb](handle);
            Assert.IsFalse(plugin.IsPreviewPlaying(plan));
            yield return null;
            yield return null;
            Assert.AreEqual(0.7f, Model(right).Positions[0], 1e-6f, "Stopping gives the robot its pose back.");
            Assert.AreEqual(-0.4f, Model(right).Positions[1], 1e-6f);
            Assert.IsFalse(right.TrajectoryPlayer.IsReplaying);
        }

        [UnityTest]
        public IEnumerator TheEndEffectorMenu_ListsPlanningStepsBeforeGripperActions()
        {
            yield return Build();
            CollectionAssert.AreEqual(new[] { "set-start", "set-goal", "plan", "gripper", "preview-grasp" },
                ui.Verbs.For(SelectionKind.EndEffector).Select(v => v.Id).ToArray(),
                "The planning steps precede the gripper actions.");
        }
    }
}
