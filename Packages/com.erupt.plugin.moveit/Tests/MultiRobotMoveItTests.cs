using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// One MoveIt plugin, three robots (left, right, third) in their own namespaces on one bus:
    /// every per-robot name goes out under the right namespace for the right robot, the verbs
    /// act on the owner of the selected object, and the facade follows the active robot.
    /// </summary>
    public class MultiRobotMoveItTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private readonly List<RobotProfile> profiles = new();
        private GameObject pluginGo, sceneHost;
        private MoveItPlugin plugin;
        private FakeUiHost ui;
        private readonly Dictionary<string, int> planCalls = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            profiles.Clear();
            robots = FakeRobots.Three(bus, profile: id =>
            {
                var profile = ScriptableObject.CreateInstance<RobotProfile>();
                profile.armGroup = id + "_arm";
                profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
                profiles.Add(profile);
                return profile;
            });
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];
            foreach (RobotInstance robot in robots.All) robot.gameObject.AddComponent<JointTrajectoryPlayer>();

            sceneHost = new GameObject("planning scene");
            var mirror = sceneHost.AddComponent<CollisionObjectsListenerSimple>();
            mirror.requestInitialPlanningScene = true;
            sceneHost.AddComponent<AttachedCollisionObjectListener>();
            yield return null;   // Start of the scene components

            planCalls.Clear();
            foreach (string id in FakeRobots.ThreeIds)
            {
                string ns = id;
                bus.SetServiceHandler($"/{ns}/plan_kinematic_path", request =>
                {
                    planCalls[ns] = planCalls.TryGetValue(ns, out int n) ? n + 1 : 1;
                    var req = ((GetMotionPlanRequest)request).motion_plan_request;
                    return Plan(req.group_name, ns + "_joint1", ns + "_joint2");
                });
            }

            ui = new FakeUiHost();
            // Active, as in a scene: the attach mirrors made for further robots live under the plugin.
            pluginGo = new GameObject("moveit");
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack() });
            yield return null;   // Start of the attach mirrors made for the second and third robot
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(sceneHost);
            FakeRobots.DestroyAll(robots);
            foreach (RobotProfile profile in profiles) Object.DestroyImmediate(profile);
        }

        private static GetMotionPlanResponse Plan(string group, params string[] joints) => new GetMotionPlanResponse
        {
            motion_plan_response = new MotionPlanResponseMsg
            {
                group_name = group,
                error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                trajectory = new RobotTrajectoryMsg
                {
                    joint_trajectory = new JointTrajectoryMsg
                    {
                        joint_names = joints,
                        points = new[]
                        {
                            new JointTrajectoryPointMsg { positions = new[] { 0.0, 0.0 } },
                            new JointTrajectoryPointMsg { positions = new[] { 0.1, 0.2 } }
                        }
                    }
                }
            }
        };

        [Test]
        public void EveryRobot_HasItsOwnSession_AndItsNamesAreInItsNamespace()
        {
            Assert.AreEqual(3, plugin.Sessions.Count);
            var expectedSubscriptions = new List<string>();
            var expectedServices = new List<string>();
            var expectedPublishers = new List<string>();
            foreach (string ns in FakeRobots.ThreeIds)
            {
                expectedSubscriptions.AddRange(new[] { $"/{ns}/joint_states", $"/{ns}/collision_objects_ros", $"/{ns}/attached_collision_objects_ros" });
                expectedServices.AddRange(new[] { $"/{ns}/plan_kinematic_path", $"/{ns}/query_planner_interface", $"/{ns}/get_planning_scene" });
                expectedPublishers.AddRange(new[] { $"/{ns}/arm_controller/joint_trajectory", $"/{ns}/collision_object" });
            }
            CollectionAssert.AreEquivalent(expectedSubscriptions, bus.Subscriptions);
            CollectionAssert.AreEquivalent(expectedServices, bus.RegisteredServices);
            CollectionAssert.AreEquivalent(expectedPublishers, bus.RegisteredPublishers);

            foreach (RobotInstance robot in robots.All)
            {
                MoveItRobotSession session = plugin.SessionFor(robot);
                Assert.AreSame(robot, session.Robot);
                Assert.AreSame(robot, session.Client.Robot);
                Assert.AreEqual(robot.Id + "_arm", session.Client.PlanningGroup);
                Assert.AreEqual(robot.Namespace, session.Scene.Namespace);
                Assert.AreSame(robot, session.AttachListener.Robot, "Each robot has its own attach mirror.");
            }
            Assert.AreEqual(3, plugin.Sessions.Select(s => s.AttachListener).Distinct().Count());
            Assert.AreEqual(new[] { "planner-moveit" }, ui.Tabs, "One plugin, one tab.");
        }

        [Test]
        public void TheVerbs_ActOnTheOwnerOfTheSelectedEndEffector()
        {
            SelectableMarker rightTool = FakeRobots.EndEffectorOf(right);

            ui.Bound["set-start"](rightTool);
            ui.Bound["set-goal"](rightTool);

            Assert.IsTrue(plugin.SessionFor(right).HasStart && plugin.SessionFor(right).HasGoal);
            Assert.IsFalse(plugin.SessionFor(left).HasStart || plugin.SessionFor(left).HasGoal);
            Assert.IsFalse(plugin.SessionFor(third).HasGoal);
            Assert.IsFalse(plugin.HasGoal, "The facade is the active robot's, which is still the left one.");
            CollectionAssert.AreEqual(
                new[] { "right_joint1", "right_joint2", "right_finger" },
                plugin.SessionFor(right).GoalState.joint_state.name, "The captured state is that robot's.");

            ui.Bound["plan"](rightTool);

            Assert.AreEqual(1, planCalls["right"]);
            Assert.IsFalse(planCalls.ContainsKey("left") || planCalls.ContainsKey("third"), "Only the owner's planner was asked.");
            TrajectorySelectable planned = plugin.Results.Single();
            Assert.AreEqual("right", planned.RobotId);
            Assert.AreSame(right, robots.OwnerOf(planned.gameObject));
            Assert.IsTrue(plugin.SessionFor(right).IsPreviewing, "A new plan is previewed on the robot it is for.");
            Assert.IsFalse(plugin.SessionFor(left).IsPreviewing || plugin.SessionFor(third).IsPreviewing);

            bus.Clear();
            ui.Bound["execute"](planned);
            CollectionAssert.AreEqual(new[] { "/right/arm_controller/joint_trajectory" }, bus.Publishes.Select(p => p.Topic));
            Assert.IsFalse(plugin.SessionFor(right).IsPreviewing);
        }

        [Test]
        public void APlanRequest_NamesTheRobotsOwnGroup_AndReturnsItsJoints()
        {
            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/third/plan_kinematic_path", request =>
            {
                sent = (GetMotionPlanRequest)request;
                return Plan("third_arm", "third_joint1", "third_joint2");
            });
            plugin.SetStartFromRobot(third);
            plugin.SetGoalFromRobot(third);

            PlanResult result = null;
            plugin.RequestPlan(third, new PlanPreferences(), r => result = r);

            Assert.AreEqual("third_arm", sent.motion_plan_request.group_name);
            Assert.AreEqual("third", result.RobotId);
            CollectionAssert.AreEqual(new[] { "third_joint1", "third_joint2" }, result.Trajectory.joint_names);
            Assert.AreSame(result, plugin.LastResultFor(third));
            Assert.IsNull(plugin.LastResultFor(left));
        }

        [Test]
        public void JointStates_MoveOnlyTheRobotInWhoseNamespaceTheyArrive()
        {
            foreach (MoveItRobotSession session in plugin.Sessions) session.Client.SetMirroring(true);

            bus.Inbound("/right/joint_states", new JointStateMsg { name = new[] { "right_joint1" }, position = new[] { 0.7 } });

            Assert.AreEqual(1, ((FakeRobotModel)right.Model).Applied.Count);
            Assert.AreEqual(0, ((FakeRobotModel)left.Model).Applied.Count);
            Assert.AreEqual(0, ((FakeRobotModel)third.Model).Applied.Count);
        }

        [Test]
        public void TheTabAndTheFacade_FollowTheActiveRobot()
        {
            Assert.AreEqual("MoveIt · left", ui.TabsById["planner-moveit"].Label);
            Assert.AreSame(plugin.SessionFor(left).Client, plugin.Client);

            plugin.SetGoalFromRobot(third);
            Assert.IsFalse(plugin.HasGoal);

            robots.SetActive(third);

            Assert.AreEqual("MoveIt · third", ui.TabsById["planner-moveit"].Label);
            Assert.AreSame(plugin.SessionFor(third), plugin.ActiveSession);
            Assert.AreSame(plugin.SessionFor(third).Client, plugin.Client);
            Assert.IsTrue(plugin.HasGoal, "The facade now reads the third robot's session.");
        }

        [UnityTest]
        public IEnumerator ARobotThatJoinsLater_GetsASession_AndOneThatLeavesLosesIt()
        {
            RobotInstance fourth = FakeRobots.Add(robots, "fourth", "fourth", new FakeRobotModel("fourth_joint1"));
            yield return null;

            Assert.AreEqual(4, plugin.Sessions.Count);
            Assert.IsNotNull(plugin.SessionFor(fourth));
            CollectionAssert.Contains(bus.Subscriptions, "/fourth/joint_states");
            CollectionAssert.Contains(bus.RegisteredPublishers, "/fourth/collision_object");
            CollectionAssert.Contains(bus.Subscriptions, "/fourth/collision_objects_ros");

            MoveItPlanningClient rightClient = plugin.SessionFor(right).Client;
            rightClient.SetMirroring(true);
            Object.DestroyImmediate(right.gameObject);

            Assert.AreEqual(3, plugin.Sessions.Count);
            Assert.IsNull(plugin.SessionFor(right));
            Assert.IsNull(plugin.PlanningScene.EndpointIn("right"), "Its planning scene is dropped from the fan-out.");
            Assert.AreEqual(3, plugin.PlanningScene.Endpoints.Count);

            // The others are untouched.
            ui.Bound["set-goal"](FakeRobots.EndEffectorOf(third));
            Assert.IsTrue(plugin.SessionFor(third).HasGoal);
        }

        [Test]
        public void ARobotWithoutABus_IsReportedOnce_AndTheOthersAreServed()
        {
            ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            var busless = new RobotRegistry();   // never given a bus
            RobotInstance lonely = FakeRobots.Add(busless, "lonely", "lonely", new FakeRobotModel());
            try
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Robot 'lonely' has no ROS bus"));
                var context = new EruptContext { Ros = bus, Robots = busless, Ui = new FakeUiHost(), Undo = new UndoStack() };
                ((IEruptPlugin)plugin).OnRegister(context);

                Assert.AreEqual(0, plugin.Sessions.Count);
                Assert.IsFalse(plugin.Serves(lonely));
                // Using the robot again and again does not report it again.
                // (An unexpected error log would fail the test.)
                plugin.SetGoalFromRobot(lonely);
                plugin.RequestPlan(lonely, new PlanPreferences(), _ => { });
            }
            finally
            {
                ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
                FakeRobots.DestroyAll(busless);
            }
        }
    }
}
