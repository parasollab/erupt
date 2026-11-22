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
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>
    /// A robot's base is put somewhere else (<see cref="RobotInstance.PlaceBase(Pose, out string)"/>)
    /// while the MoveIt plugin serves three robots, each with its own planning frame that moves
    /// with it: that robot's planning scene, and only that one, is sent the environment again;
    /// its goal and plans go; and the move is refused while the robot executes or carries.
    /// </summary>
    public class MoveItBaseMovedTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private readonly List<Object> made = new();
        private readonly Dictionary<RobotInstance, Transform> hands = new();
        private GameObject pluginGo, sceneHost;
        private CollisionObjectsListenerSimple mirror;
        private MoveItPlugin plugin;
        private FakeUiHost ui;

        // Where the right robot is put: a metre along x and z, turned a quarter turn.
        private static readonly Pose Elsewhere = new Pose(new Vector3(1f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            robots = FakeRobots.Three(bus, profile: id =>
            {
                var profile = ScriptableObject.CreateInstance<RobotProfile>();
                profile.armGroup = id + "_arm";
                profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
                made.Add(profile);
                return profile;
            });
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];
            hands.Clear();
            foreach (RobotInstance robot in robots.All)
            {
                robot.gameObject.AddComponent<JointTrajectoryPlayer>();
                // As on a robot prefab: the planning frame is part of the robot and moves with it.
                var frame = new GameObject("PlanningFrame");
                frame.transform.SetParent(robot.transform, false);
                robot.SetPlanningFrame(frame.transform);
                var hand = new GameObject("hand").transform;
                hand.SetParent(robot.transform, false);
                ((FakeRobotModel)robot.Model).Links["hand"] = hand;
                hands[robot] = hand;
            }

            sceneHost = new GameObject("planning scene");
            mirror = sceneHost.AddComponent<CollisionObjectsListenerSimple>();
            mirror.requestInitialPlanningScene = false;
            var attach = sceneHost.AddComponent<AttachedCollisionObjectListener>();
            typeof(AttachedCollisionObjectListener)
                .GetField("maxAttachJump", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(attach, 100f);
            yield return null;

            foreach (string ns in FakeRobots.ThreeIds)
                bus.SetServiceHandler($"/{ns}/plan_kinematic_path", request => Plan(ns + "_joint1", ns + "_joint2"));

            ui = new FakeUiHost();
            pluginGo = new GameObject("moveit");
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack() });
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            var spawned = mirror != null ? mirror.objectsById.Values.ToList() : new List<GameObject>();
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(sceneHost);
            foreach (string ghost in new[] { "StartGhost", "GoalGhost" })
            {
                GameObject leftOver = GameObject.Find(ghost);
                if (leftOver != null) Object.DestroyImmediate(leftOver);
            }
            FakeRobots.DestroyAll(robots);
            foreach (Object o in made.Concat(spawned))
                if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        [UnityTest]
        public IEnumerator PlacingARobotsBase_SendsEveryObjectAgain_ToThatRobotsPlanningSceneOnly_InItsMovedFrame()
        {
            GameObject crateObject = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", crateObject);
            MakeObstacle("post", new Vector3(-1f, 0.25f, 0f));
            yield return WaitForPublish();
            foreach (string ns in FakeRobots.ThreeIds) Assert.AreEqual(2, bus.CountOn(Topic(ns)), ns);
            // Every robot stands at the origin, so each planning scene has the crate where Unity has it.
            AssertPose(Last("right", "crate"), 2.0, 3.0, 0.5);
            bus.Clear();

            Assert.IsTrue(right.PlaceBase(Elsewhere, out string reason), reason);

            Assert.AreEqual(2, bus.CountOn(Topic("right")), "Both objects, once each.");
            Assert.AreEqual(0, bus.CountOn(Topic("left")), "The other robots' planning frames did not move.");
            Assert.AreEqual(0, bus.CountOn(Topic("third")));
            // The crate did not move in Unity; in the frame that now stands at (1, 0, 1) and looks
            // along Unity +x it is at Unity-local (-2, 0.5, 1), which is ROS (-2, 1, 0.5).
            CollisionObjectMsg crate = Last("right", "crate");
            Assert.AreEqual(CollisionObjectMsg.MOVE, crate.operation, "The planning scene has the shapes; only the pose changed.");
            Assert.AreEqual("world", crate.header.frame_id);
            AssertPose(crate, -2.0, 1.0, 0.5);
            // The post at Unity (-1, 0.25, 0): Unity-local (1, 0.25, -2), ROS (1, -2, 0.25).
            AssertPose(Last("right", "post"), 1.0, -2.0, 0.25);

            // The publishers have nothing more to say: the objects did not move in Unity.
            bus.Clear();
            yield return WaitForPublish();
            Assert.IsEmpty(bus.Publishes.Where(p => p.Topic.EndsWith("/collision_object")).ToList());

            // The planning scene answers with what it now holds; that is an echo, not a change.
            bus.Inbound(Scene("right"), Box("crate", -2, 1, 0.5, CollisionObjectMsg.MOVE));
            Assert.AreEqual(new Vector3(2f, 0.5f, 3f), crateObject.transform.position);
            Assert.IsEmpty(bus.Publishes.Where(p => p.Topic.EndsWith("/collision_object")).ToList());
        }

        [UnityTest]
        public IEnumerator AnObjectTheMovedRobotsRosSideOwns_GoesWithTheRobot_AndTheOthersAreTold()
        {
            // The right robot's planning scene reports a pallet of its own, half a metre ahead (ROS x).
            bus.Inbound(Scene("right"), Box("pallet", 0.5, 0, 0));
            yield return WaitForPublish();
            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject pallet));
            Assert.Less(Vector3.Distance(new Vector3(0.5f, 0f, 0f), pallet.transform.position), 1e-4f);
            bus.Clear();

            Assert.IsTrue(right.PlaceBase(Elsewhere));

            // It is fixed in the right robot's frame: half a metre along that frame's x, which now points along Unity -z.
            Assert.Less(Vector3.Distance(new Vector3(1f, 0f, 0.5f), pallet.transform.position), 1e-4f, pallet.transform.position.ToString());
            Assert.AreEqual(0, bus.CountOn(Topic("right")), "Its owner already has it there.");
            AssertPose(Last("left", "pallet"), 1.0, 0.5, 0.0);
            AssertPose(Last("third", "pallet"), 1.0, 0.5, 0.0);
            Assert.AreEqual(CollisionObjectMsg.MOVE, Last("left", "pallet").operation);
        }

        [UnityTest]
        public IEnumerator AnObjectThatCannotBeSentNow_IsSentInFull_WhenItCan()
        {
            GameObject crate = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            yield return WaitForPublish();
            crate.GetComponent<CollisionObjectPublisher>().pausePublishing = true;
            bus.Clear();

            Assert.IsTrue(right.PlaceBase(Elsewhere));
            yield return WaitForPublish();
            Assert.AreEqual(0, bus.CountOn(Topic("right")), "A paused object is not sent.");

            crate.GetComponent<CollisionObjectPublisher>().pausePublishing = false;
            yield return WaitForPublish();

            CollisionObjectMsg sent = Last("right", "crate");
            Assert.AreEqual(CollisionObjectMsg.ADD, sent.operation);
            AssertPose(sent, -2.0, 1.0, 0.5);
            Assert.AreEqual(0, bus.CountOn(Topic("left")));
        }

        [Test]
        public void PlacingARobotsBase_DropsItsStartGoalAndPlans_AndKeepsTheOthers()
        {
            SelectableMarker rightTool = FakeRobots.EndEffectorOf(right);
            SelectableMarker leftTool = FakeRobots.EndEffectorOf(left);
            foreach (SelectableMarker tool in new[] { rightTool, leftTool })
            {
                ui.Bound["set-start"](tool);
                ui.Bound["set-goal"](tool);
                ui.Bound["plan"](tool);
            }
            Assert.IsTrue(plugin.SessionFor(right).IsPreviewing);
            int goalChanges = 0;
            plugin.GoalChanged += () => goalChanges++;

            Assert.IsTrue(right.PlaceBase(Elsewhere));

            MoveItRobotSession session = plugin.SessionFor(right);
            Assert.IsFalse(session.HasStart || session.HasGoal, "They were captured for the base where it was.");
            Assert.IsFalse(session.IsPreviewing);
            Assert.IsNull(plugin.LastResultFor(right));
            Assert.AreEqual(1, goalChanges);
            CollectionAssert.AreEqual(new[] { "left" }, plugin.Results.Select(r => r.RobotId).ToList());
            Assert.IsTrue(plugin.SessionFor(left).HasGoal && plugin.SessionFor(left).IsPreviewing);
        }

        [Test]
        public void PlacingIsRefused_WhileTheRobotExecutes_AndOnlyForThatRobot()
        {
            SelectableMarker rightTool = FakeRobots.EndEffectorOf(right);
            ui.Bound["set-start"](rightTool);
            ui.Bound["set-goal"](rightTool);
            ui.Bound["plan"](rightTool);
            ui.Bound["execute"](plugin.Results.Single());
            Assert.IsTrue(plugin.SessionFor(right).Client.IsFollowingExecution);

            Assert.IsFalse(right.PlaceBase(Elsewhere, out string reason));
            Assert.AreEqual("Robot 'right' is executing a trajectory.", reason);
            Assert.AreEqual(Vector3.zero, right.transform.position);
            Assert.IsNotNull(plugin.LastResultFor(right), "A refused move changes nothing.");

            Assert.IsTrue(left.PlaceBase(Elsewhere), "The other robots are free.");

            // The execution is over (here: the mirror is switched off): the base may move.
            plugin.SessionFor(right).Client.SetMirroring(false);
            Assert.IsTrue(right.PlaceBase(Elsewhere, out reason), reason);
        }

        [UnityTest]
        public IEnumerator PlacingIsRefused_WhileTheRobotCarriesAnObject()
        {
            GameObject part = MakeObstacle("part", Vector3.zero);
            mirror.RegisterUnityOwnedObject("part", part);
            yield return WaitForPublish();

            bus.Inbound($"/right/attached_collision_objects_ros", Attached("part", "hand", CollisionObjectMsg.ADD));
            Assert.AreSame(hands[right], part.transform.parent);
            Assert.IsTrue(plugin.SessionFor(right).AttachListener.IsCarrying);

            Assert.IsFalse(right.PlaceBase(Elsewhere, out string reason));
            Assert.AreEqual("Robot 'right' is carrying an object.", reason);
            Assert.IsTrue(third.PlaceBase(Elsewhere), "A robot that carries nothing is free.");

            // Put down; the place pose arrives from the robot's planning scene, and then it may move.
            bus.Inbound($"/right/attached_collision_objects_ros", Attached("part", "hand", CollisionObjectMsg.REMOVE));
            Assert.IsFalse(right.PlaceBase(Elsewhere), "The pose it was put down at is still to come, in the robot's frame.");
            bus.Inbound(Scene("right"), Box("part", 0.3, 0, 0));
            Assert.IsFalse(plugin.SessionFor(right).AttachListener.IsCarrying);
            Assert.IsTrue(right.PlaceBase(Elsewhere, out reason), reason);
        }

        // ---- helpers ----

        private static string Topic(string ns) => $"/{ns}/collision_object";
        private static string Scene(string ns) => $"/{ns}/collision_objects_ros";

        private CollisionObjectMsg Last(string ns, string id)
        {
            CollisionObjectMsg last = null;
            foreach (var message in bus.PublishedOn(Topic(ns)))
                if (((CollisionObjectMsg)message).id == id) last = (CollisionObjectMsg)message;
            return last;
        }

        private static IEnumerator WaitForPublish()
        {
            yield return new WaitForSeconds(0.05f);
        }

        private GameObject MakeObstacle(string id, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.position = position;
            var publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = id;
            publisher.publishRateHz = 100f;
            made.Add(go);
            return go;
        }

        private static CollisionObjectMsg Box(string id, double x, double y, double z, sbyte operation = CollisionObjectMsg.ADD) =>
            new CollisionObjectMsg
            {
                id = id,
                operation = operation,
                pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)),
                primitives = operation == CollisionObjectMsg.MOVE
                    ? new SolidPrimitiveMsg[0]
                    : new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.2, 0.2, 0.2 } } }
            };

        private static AttachedCollisionObjectMsg Attached(string id, string link, sbyte operation)
        {
            var obj = Box(id, 0, 0, 0, operation == CollisionObjectMsg.REMOVE ? CollisionObjectMsg.MOVE : CollisionObjectMsg.ADD);
            obj.operation = operation;
            obj.header.frame_id = link;
            return new AttachedCollisionObjectMsg { link_name = link, @object = obj };
        }

        private static void AssertPose(CollisionObjectMsg msg, double x, double y, double z)
        {
            Assert.IsNotNull(msg, "No message.");
            Assert.AreEqual(x, msg.pose.position.x, 1e-4, "x");
            Assert.AreEqual(y, msg.pose.position.y, 1e-4, "y");
            Assert.AreEqual(z, msg.pose.position.z, 1e-4, "z");
        }

        private static GetMotionPlanResponse Plan(params string[] joints) => new GetMotionPlanResponse
        {
            motion_plan_response = new MotionPlanResponseMsg
            {
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
    }
}
