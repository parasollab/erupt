using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Robotics.ROSTCPConnector;
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
    /// What running the app against a real MoveIt showed, pinned on the fake bus at the level
    /// of the plugin: a plan that does not come is a refusal the user sees, an execution is
    /// followed to its end and then lets the robot go, the gripper verb reaches MoveIt, an
    /// outage of the ROS side is recovered from, and unregistering takes Unity's obstacles out
    /// of the planning scene.
    /// </summary>
    public class MoveItLiveFindingsTests
    {
        private const string ControllerTopic = "/arm_controller/joint_trajectory";

        private FakeRosBus bus;
        private FakeRobotModel robot;
        private RobotProfile profile;
        private RobotRegistry robots;
        private GameObject pluginGo, routerGo, toolGo, sceneHost, obstacle;
        private MoveItPlugin plugin;
        private InteractionRouter router;
        private readonly List<InteractionRefusal> refusals = new List<InteractionRefusal>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            toolGo = new GameObject("tool");
            robot = new FakeRobotModel("joint1", "joint2").WithGripper("finger");
            robot.EndEffector = toolGo.transform;

            profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.armGroup = "arm";
            profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
            profile.gripper = new GripperSpec { kind = GripperKind.Prismatic, driverJoint = "finger", open = 0.04f, closed = 0f, openWidthMetres = 0.08f };
            robots = FakeRobots.Single(bus, robot, "", profile);
            robots.Active.gameObject.AddComponent<JointTrajectoryPlayer>();

            sceneHost = new GameObject("planning scene");
            var mirror = sceneHost.AddComponent<CollisionObjectsListenerSimple>();
            mirror.requestInitialPlanningScene = false;
            yield return null;

            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;

            pluginGo = new GameObject("moveit");
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            ((IEruptPlugin)plugin).OnRegister(new EruptContext
            {
                Ros = bus, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack(), Router = router
            });
            plugin.SessionFor(robots.Active).Client.ExecutionSettleSeconds = 0f;
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(sceneHost);
            Object.DestroyImmediate(toolGo);
            if (obstacle != null) Object.DestroyImmediate(obstacle);
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(profile);
        }

        private static GetMotionPlanResponse Answer(int code, string message = "") => new GetMotionPlanResponse
        {
            motion_plan_response = new MotionPlanResponseMsg
            {
                error_code = new MoveItErrorCodesMsg { val = code, message = message },
                trajectory = new RobotTrajectoryMsg
                {
                    joint_trajectory = new JointTrajectoryMsg
                    {
                        joint_names = new[] { "joint1", "joint2" },
                        points = new[]
                        {
                            new JointTrajectoryPointMsg { positions = new[] { 0.0, 0.0 } },
                            new JointTrajectoryPointMsg { positions = new[] { 0.3, 0.2 } }
                        }
                    }
                }
            }
        };

        private PlanResult Plan()
        {
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();
            bus.SetServiceHandler("/plan_kinematic_path", _ => Answer(MoveItErrorCodesMsg.SUCCESS));
            PlanResult plan = null;
            plugin.RequestPlan(robots.Active, new PlanPreferences(), r => plan = r);
            Assert.IsNotNull(plan);
            plugin.StopPreview();
            return plan;
        }

        private GameObject MakeObstacle(string id)
        {
            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = id;
            publisher.publishRateHz = 100f;
            return obstacle;
        }

        // ---------- plans that do not come ----------

        [UnityTest]
        public IEnumerator APlanMoveItRefuses_IsARefusalAtTheEndEffector_WithTheReasonInWords()
        {
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();
            bus.SetServiceHandler("/plan_kinematic_path", _ => Answer(MoveItErrorCodesMsg.GOAL_IN_COLLISION));
            string onTab = null;
            plugin.PlanFailed += m => onTab = m;
            PlanResult plan = new PlanResult();
            plugin.RequestPlan(robots.Active, new PlanPreferences(), r => plan = r);
            yield return null;

            Assert.IsNull(plan);
            StringAssert.Contains("the goal is in collision", onTab, "The tab still gets the text.");
            Assert.AreEqual(1, refusals.Count, "Pressing Plan on the end effector with the tab closed used to say nothing at all.");
            Assert.AreEqual(RefusalCode.PlanFailed, refusals[0].Code);
            StringAssert.Contains("Planning failed: the goal is in collision", refusals[0].Reason);
            Assert.AreSame(toolGo.transform, refusals[0].Subject);
        }

        [Test]
        public void PlanningWithoutAStartOrAGoal_SaysWhichIsMissing()
        {
            plugin.RequestPlan(robots.Active, new PlanPreferences(), _ => { });
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlanFailed, refusals[0].Code);
            StringAssert.Contains("Set a start first", refusals[0].Reason);
        }

        [Test]
        public void PlanningWhileRosIsAway_IsRefusedAsUnavailable()
        {
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();
            bus.HasConnectionError = true;
            PlanResult plan = new PlanResult();
            plugin.RequestPlan(robots.Active, new PlanPreferences(), r => plan = r);
            Assert.IsNull(plan);
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals.Single().Code);
            StringAssert.Contains("not connected", refusals[0].Reason);
        }

        // ---------- execution ----------

        [Test]
        public void AnExecution_ReportsSuccess_WhenTheRobotReportsTheEndOfTheTrajectory()
        {
            PlanResult plan = Plan();
            var statuses = new List<ExecutionStatus>();
            plugin.Execute(plan, statuses.Add);
            Assert.AreEqual(1, bus.CountOn(ControllerTopic));
            Assert.AreEqual(ExecutionPhase.Executing, statuses.Last().Phase);
            Assert.IsTrue(plugin.Client.IsMirroring);

            bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "joint1", "joint2", "finger" }, position = new[] { 0.3, 0.2, 0.04 } });
            Assert.AreEqual(ExecutionPhase.Succeeded, statuses.Last().Phase);
            Assert.IsFalse(plugin.Client.IsMirroring, "The robot is the user's again: the next goal is set by posing it.");
            Assert.AreEqual(0.3f, robot.Positions[0], 1e-6f, "It rests where the real robot stopped.");
        }

        [UnityTest]
        public IEnumerator APreview_TakesTheRobotFromTheMirror()
        {
            PlanResult plan = Plan();
            yield return null;   // the preview that started with the plan has wound down
            yield return null;
            plugin.Client.SetMirroring(true);
            plugin.Preview(plan);
            yield return null;
            Assert.IsFalse(plugin.Client.IsMirroring, "A preview under a running mirror was overwritten by every joint state.");
            plugin.StopPreview();
        }

        [Test]
        public void ResettingStartAndGoal_KeepsThePlansHandles()
        {
            PlanResult plan = Plan();
            TrajectorySelectable handle = plugin.Results.Single(r => r.Result == plan);
            plugin.ResetPlanningState(robots.Active);
            Assert.IsTrue(handle != null, "The handle rode the goal ghost and was destroyed with it.");
            Assert.AreSame(pluginGo.transform, handle.transform.parent);
        }

        // ---------- gripper ----------

        [Test]
        public void TheGripperVerb_ReachesMoveIt()
        {
            var action = bus.ActionClient<ExecuteTrajectoryGoal, ExecuteTrajectoryResult, ExecuteTrajectoryFeedback>("/execute_trajectory");
            Assert.IsNull(action.LastGoal);

            // What PluginHost's gripper verb does: toggle the robot model's gripper (it starts open).
            robots.Active.Gripper.ToggleGripper();

            Assert.IsNotNull(action.LastGoal, "The gripper verb used to move the Unity gripper only.");
            JointTrajectoryMsg sent = action.LastGoal.trajectory.joint_trajectory;
            CollectionAssert.AreEqual(new[] { "finger" }, sent.joint_names);
            Assert.AreEqual(0.0, sent.points.Last().positions[0], 1e-9, "Closed.");

            action.Accept().Complete(RosActionGoalStatus.Succeeded, new ExecuteTrajectoryResult(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS }));
            robots.Active.Gripper.ToggleGripper();
            Assert.AreEqual(0.04, action.LastGoal.trajectory.joint_trajectory.points.Last().positions[0], 1e-6, "Open again.");
            CollectionAssert.IsEmpty(refusals);
        }

        // ---------- the ROS side goes away and comes back ----------

        [UnityTest]
        public IEnumerator AfterAnOutage_TheEnvironmentIsSentAgain_AndWaitingPlansFail()
        {
            MakeObstacle("crate");
            yield return new WaitForSeconds(0.05f);
            Assert.AreEqual(1, bus.CountOn("/collision_object"));

            // A plan is in flight when the endpoint goes away: it is never answered.
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();
            int answers = 0;
            string failure = null;
            plugin.PlanFailed += m => failure = m;
            plugin.RequestPlan(robots.Active, new PlanPreferences(), _ => answers++);
            Assert.AreEqual(0, answers);

            bool reconnected = false;
            plugin.Reconnected += () => reconnected = true;
            bus.HasConnectionError = true;
            yield return new WaitForSecondsRealtime(1.8f);
            Assert.IsFalse(reconnected);
            // What changes while ROS is away waits; it is not sent into the void.
            obstacle.transform.position += Vector3.right;
            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(1, bus.CountOn("/collision_object"), "Nothing is sent while nothing listens.");
            bus.Clear();

            bus.HasConnectionError = false;
            yield return null;
            Assert.AreEqual(0, bus.Publishes.Count, "Not in the first moment either: the connection has yet to register its topics.");
            yield return new WaitForSecondsRealtime(0.5f);

            Assert.IsTrue(reconnected, "The plugin noticed that ROS is back.");
            var again = bus.PublishedOn("/collision_object").Cast<CollisionObjectMsg>().ToList();
            Assert.GreaterOrEqual(again.Count, 1, "The obstacle is sent again: a restarted ROS side has forgotten it.");
            Assert.AreEqual(CollisionObjectMsg.ADD, again[0].operation);
            Assert.AreEqual(1, again[0].primitives.Length);
            Assert.AreEqual(1.0, again.Last().pose.position.x, 1e-4, "With where it is now.");
            Assert.AreEqual(1, answers, "The plan that was waiting is failed.");
            StringAssert.Contains("connection to ROS was lost", failure);
        }

        [UnityTest]
        public IEnumerator AHiccup_HoldsTheTrafficBack_ButIsNotAnOutage()
        {
            MakeObstacle("crate");
            yield return new WaitForSeconds(0.05f);
            bus.Clear();
            bool reconnected = false;
            plugin.Reconnected += () => reconnected = true;

            bus.HasConnectionError = true;
            yield return new WaitForSecondsRealtime(0.2f);
            obstacle.transform.position += Vector3.right;
            yield return new WaitForSeconds(0.05f);
            Assert.AreEqual(0, bus.Publishes.Count);

            bus.HasConnectionError = false;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(reconnected, "Everything is not sent again for a hiccup.");
            var sent = bus.PublishedOn("/collision_object").Cast<CollisionObjectMsg>().ToList();
            Assert.AreEqual(1, sent.Count, "What changed meanwhile goes out once the connection is back.");
            Assert.AreEqual(CollisionObjectMsg.MOVE, sent[0].operation);
        }

        [UnityTest]
        public IEnumerator StartedBeforeRos_NothingIsSentUntilTheConnectionIsUp_ThenEverythingIs()
        {
            // A second plugin, registered while the endpoint is not there yet: how the app starts
            // on a headset, where the socket opens after the first frame.
            ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            bus.Clear();
            bus.HasConnectionError = true;
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack(), Router = router });
            bool reconnected = false;
            plugin.Reconnected += () => reconnected = true;

            MakeObstacle("crate");
            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "An ADD sent before the connection has registered its publisher is thrown away by the endpoint, and Unity would believe it delivered.");

            bus.HasConnectionError = false;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsTrue(reconnected);
            var sent = bus.PublishedOn("/collision_object").Cast<CollisionObjectMsg>().ToList();
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(CollisionObjectMsg.ADD, sent[0].operation);
        }

        // ---------- the plugin goes away ----------

        [UnityTest]
        public IEnumerator Unregistering_TakesUnitysObstaclesOutOfThePlanningScene()
        {
            MakeObstacle("crate");
            yield return new WaitForSeconds(0.05f);
            bus.Clear();

            ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            var sent = bus.PublishedOn("/collision_object").Cast<CollisionObjectMsg>().ToList();
            Assert.AreEqual(1, sent.Count, "The robots used to be dropped first, which left nowhere to send this.");
            Assert.AreEqual("crate", sent[0].id);
            Assert.AreEqual(CollisionObjectMsg.REMOVE, sent[0].operation);
        }
    }
}
