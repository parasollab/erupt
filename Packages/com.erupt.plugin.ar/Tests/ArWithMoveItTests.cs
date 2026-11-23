#if ERUPT_PLUGIN_MOVEIT
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins.MoveIt;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Moveit;

namespace Erupt.Plugins.Ar.Tests
{
    /// <summary>
    /// The AR plugin next to the MoveIt plugin, three robots, one fake bus: confirming a
    /// placement moves that robot, and the MoveIt plugin sends the environment again to that
    /// robot's planning scene and to no other. The two plugins do not know each other; what
    /// connects them is core's <see cref="RobotInstance.PlaceBase(Pose, out string)"/>.
    /// </summary>
    public class ArWithMoveItTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private FakeUiHost ui;
        private UndoStack undo;
        private GameObject arGo, moveItGo, sceneHost;
        private ArPlugin ar;
        private MoveItPlugin moveIt;
        private AprilTagTracker tracker;
        private readonly List<Object> made = new();

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
            int tag = 20;
            foreach (RobotInstance robot in robots.All)
            {
                // As on a robot prefab: the planning frame is part of the robot.
                var frame = new GameObject("PlanningFrame");
                frame.transform.SetParent(robot.transform, false);
                robot.SetPlanningFrame(frame.transform);
                // One tag per robot (20, 21, 22), the base on the tag.
                var anchorGo = new GameObject("MarkerAnchor");
                anchorGo.transform.SetParent(robot.transform, false);
                anchorGo.AddComponent<MarkerAnchor>().Configure(tag++, 0.10f, Vector3.zero, Vector3.zero);
            }

            sceneHost = new GameObject("planning scene");
            sceneHost.AddComponent<CollisionObjectsListenerSimple>().requestInitialPlanningScene = false;
            yield return null;

            ui = new FakeUiHost();
            undo = new UndoStack();
            var context = new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = undo };
            moveItGo = new GameObject("moveit");
            moveIt = moveItGo.AddComponent<MoveItPlugin>();
            ((IEruptPlugin)moveIt).OnRegister(context);
            arGo = new GameObject("ar");
            tracker = arGo.AddComponent<AprilTagTracker>();
            tracker.PoseFilterCoefficient = 0f;
            ar = arGo.AddComponent<ArPlugin>();
            ((IEruptPlugin)ar).OnRegister(context);
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (ar != null && ar.IsRegistered) ((IEruptPlugin)ar).OnUnregister(ar.Context);
            if (moveIt != null && moveIt.IsRegistered) ((IEruptPlugin)moveIt).OnUnregister(moveIt.Context);
            Object.DestroyImmediate(arGo);
            Object.DestroyImmediate(moveItGo);
            Object.DestroyImmediate(sceneHost);
            FakeRobots.DestroyAll(robots);
            foreach (Object o in made)
                if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        private static string Topic(string ns) => $"/{ns}/collision_object";

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

        [UnityTest]
        public IEnumerator ConfirmingAPlacement_MovesTheRobot_AndOnlyItsPlanningSceneGetsTheEnvironmentAgain()
        {
            MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            MakeObstacle("post", new Vector3(-1f, 0.25f, 0f));
            yield return new WaitForSeconds(0.05f);
            foreach (string ns in FakeRobots.ThreeIds) Assert.AreEqual(2, bus.CountOn(Topic(ns)), ns);
            bus.Clear();

            // The right robot's tag (21) is seen on the floor at (1, 0, 1), its top edge along world x.
            Assert.IsTrue(tracker.InjectObservation(21, MarkerMathTests.FlatTag(new Vector3(1f, 0f, 1f), 90f)));
            yield return null;   // the plugin's own update picks the proposal up
            Assert.IsTrue(ar.SessionFor(right).HasProposal);
            Assert.IsEmpty(bus.Publishes, "A proposal is only shown: nothing is sent and nothing moves.");
            Assert.AreEqual(Vector3.zero, right.transform.position);

            int moved = 0;
            right.BaseMoved += (_, _) => moved++;
            ui.Widgets.Single(w => w.Id == $"ar.place.{right.Id}").GameObject.GetComponentInChildren<UnityEngine.UI.Button>().onClick.Invoke();

            MarkerMathTests.AssertPose(new Pose(new Vector3(1f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f)), right.BasePose);
            Assert.AreEqual(1, moved);
            Assert.AreEqual(2, bus.CountOn(Topic("right")), "Every object, once, in the frame that moved with the robot.");
            Assert.AreEqual(0, bus.CountOn(Topic("left")));
            Assert.AreEqual(0, bus.CountOn(Topic("third")));
            var crate = bus.PublishedOn(Topic("right")).Cast<CollisionObjectMsg>().Single(m => m.id == "crate");
            Assert.AreEqual(CollisionObjectMsg.MOVE, crate.operation);
            Assert.AreEqual(-2.0, crate.pose.position.x, 1e-4);
            Assert.AreEqual(1.0, crate.pose.position.y, 1e-4);
            Assert.AreEqual(0.5, crate.pose.position.z, 1e-4);

            // Undo puts the robot back, and its planning scene is told again; still no one else's.
            bus.Clear();
            Assert.IsTrue(undo.Undo());
            Assert.AreEqual(Vector3.zero, right.transform.position);
            Assert.AreEqual(2, bus.CountOn(Topic("right")));
            Assert.AreEqual(0, bus.CountOn(Topic("left")) + bus.CountOn(Topic("third")));
            crate = bus.PublishedOn(Topic("right")).Cast<CollisionObjectMsg>().Single(m => m.id == "crate");
            Assert.AreEqual(2.0, crate.pose.position.x, 1e-4);
            Assert.AreEqual(3.0, crate.pose.position.y, 1e-4);
        }

        [UnityTest]
        public IEnumerator ConfirmingAPlacement_DropsThatRobotsGoalAndPlans()
        {
            foreach (string ns in FakeRobots.ThreeIds)
                bus.SetServiceHandler($"/{ns}/plan_kinematic_path", _ => new GetMotionPlanResponse
                {
                    motion_plan_response = new MotionPlanResponseMsg
                    {
                        error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                        trajectory = new RobotTrajectoryMsg
                        {
                            joint_trajectory = new RosMessageTypes.Trajectory.JointTrajectoryMsg
                            {
                                joint_names = new[] { ns + "_joint1", ns + "_joint2" },
                                points = new[] { new RosMessageTypes.Trajectory.JointTrajectoryPointMsg { positions = new[] { 0.1, 0.2 } } }
                            }
                        }
                    }
                });
            foreach (RobotInstance robot in new[] { left, right })
            {
                SelectableMarker tool = FakeRobots.EndEffectorOf(robot);
                ui.Bound["set-start"](tool);
                ui.Bound["set-goal"](tool);
                ui.Bound["plan"](tool);
            }
            Assert.AreEqual(2, moveIt.Results.Count);

            // The plan is being executed: the base has to stay.
            ui.Bound["execute"](moveIt.Results.Single(r => r.RobotId == "right"));
            tracker.InjectObservation(21, MarkerMathTests.FlatTag(new Vector3(1f, 0f, 1f)));
            yield return null;
            InteractionRefusal refused = ar.PlaceAtMarker(right);
            Assert.AreEqual("Robot 'right' is executing a trajectory. It cannot be moved now.", refused.Reason);
            Assert.AreEqual(Vector3.zero, right.transform.position);

            // The execution is over.
            moveIt.SessionFor(right).Client.SetMirroring(false);
            Assert.IsFalse(ar.PlaceAtMarker(right).IsRefused);

            Assert.IsFalse(moveIt.SessionFor(right).HasGoal || moveIt.SessionFor(right).HasStart);
            Assert.IsNull(moveIt.LastResultFor(right));
            Assert.IsTrue(moveIt.SessionFor(left).HasGoal, "The other robots keep theirs.");
            CollectionAssert.AreEqual(new[] { "left" }, moveIt.Results.Select(r => r.RobotId).ToList());
        }
    }
}
#endif
