using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Sensor;
using RosMessageTypes.Shape;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// The solution player on a fake robot and the environment registry: stage ranges,
    /// previews from a start_scene (objects shown where that stage begins, on the table or in
    /// the gripper, nothing reaching MoveIt, everything put back), and ROS → Unity name mapping.
    /// </summary>
    public class MtcSolutionPlayerTests
    {
        private FakeRosBus bus;
        private GameObject host, robotGo;
        private Transform hand;
        private FakeRobotModel robot;
        private EnvironmentRegistry registry;
        private MtcSolutionPlayer player;
        private readonly List<GameObject> spawned = new();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);

            robotGo = new GameObject("robot");
            hand = new GameObject("fr3_hand").transform;
            hand.SetParent(robotGo.transform, false);
            hand.localPosition = new Vector3(0f, 0.5f, 0f);
            robot = new FakeRobotModel("fr3_joint1", "fr3_joint2") { Root = robotGo.transform, EndEffector = hand };

            host = new GameObject("mtc");
            registry = host.AddComponent<EnvironmentRegistry>();      // world origin = host, identity
            player = host.AddComponent<MtcSolutionPlayer>();
            typeof(MtcSolutionPlayer).GetField("registry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(player, registry);
            player.SetRobot(robot);
        }

        [TearDown]
        public void TearDown()
        {
            player.Stop();
            foreach (var go in spawned) if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(robotGo);
            RosBus.Reset();
        }

        // --- start scene ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator StartScene_PlacesObjects_AttachesToTheExactLink_AndIsRestored()
        {
            var cube = Spawn("cube", new Vector3(1f, 0f, 0f));
            var bowl = Spawn("bowl", new Vector3(0f, 0f, 1f));
            yield return null;                                   // publishers start
            yield return null;                                   // and publish their ADD
            Transform cubeParent = cube.transform.parent;
            var cubePublisher = cube.GetComponent<CollisionObjectPublisher>();
            bus.Clear();

            // The stage starts with the cube already in the hand and the bowl moved.
            var solution = new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } };
            solution.start_scene.world.collision_objects = new[] { Box("bowl", 0.0, 2.0, 0.0) };
            solution.start_scene.robot_state.attached_collision_objects = new[]
            {
                new AttachedCollisionObjectMsg { link_name = "panda_hand", @object = Box("cube", 0.0, 0.0, 0.1) }
            };

            player.PlaySolution(solution, useStartScene: true);
            yield return null;

            Assert.IsTrue(player.IsPlaying, "The end state is held so a motionless stage can be seen.");
            Assert.AreSame(hand, cube.transform.parent, "An object attached in the start scene rides the gripper (panda_hand → fr3_hand).");
            AssertClose(hand.position + hand.up * 0.1f, cube.transform.position);
            AssertClose(new Vector3(0f, 0f, 2f), bowl.transform.position, "ROS (0, 2, 0) is Unity (0, 0, 2)");
            Assert.IsTrue(cubePublisher.pausePublishing, "The preview motion must not stream into MoveIt.");

            yield return new WaitForSeconds(0.05f);
            player.Stop();
            yield return new WaitForSeconds(0.05f);

            Assert.AreSame(cubeParent, cube.transform.parent);
            AssertClose(new Vector3(1f, 0f, 0f), cube.transform.position);
            AssertClose(new Vector3(0f, 0f, 1f), bowl.transform.position);
            Assert.AreEqual(Vector3.one, cube.transform.localScale);
            Assert.IsFalse(cubePublisher.pausePublishing);
            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "Neither the preview nor its restore may reach MoveIt's live scene.");
        }

        [UnityTest]
        public IEnumerator StartScene_LeavesAnObjectTheRealRobotIsCarryingAlone()
        {
            var cube = Spawn("cube", new Vector3(1f, 0f, 0f));
            registry.Attach("cube", hand);                       // the live attach mirror owns it

            var solution = new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } };
            solution.start_scene.world.collision_objects = new[] { Box("cube", 5.0, 0.0, 0.0) };

            player.PlaySolution(solution, useStartScene: true);
            yield return null;

            AssertClose(new Vector3(1f, 0f, 0f), cube.transform.position);
            player.Stop();
        }

        [UnityTest]
        public IEnumerator EmptyStartScene_WarnsAndPlaysFromTheCurrentState()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no start scene"));

            player.PlaySolution(new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } }, useStartScene: true);
            yield return null;

            Assert.IsNull(player.LastProblem);
            Assert.IsTrue(player.IsPlaying);
            player.Stop();
        }

        [UnityTest]
        public IEnumerator StartScene_AppliesTheJointsTheUnityRobotHas()
        {
            var solution = new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } };
            solution.start_scene.robot_state.joint_state = new JointStateMsg
            {
                name = new[] { "panda_joint1", "panda_joint2", "panda_finger_joint1" },
                position = new[] { 0.5, -0.5, 0.04 }
            };

            player.PlaySolution(solution, useStartScene: true);
            yield return null;

            var applied = robot.Applied[0];
            CollectionAssert.AreEqual(new[] { "fr3_joint1", "fr3_joint2" }, applied.names, "mapped, fingers dropped");
            CollectionAssert.AreEqual(new[] { 0.5, -0.5 }, applied.positions);
            player.Stop();
        }

        // --- stage ranges -----------------------------------------------------------------

        [UnityTest]
        public IEnumerator StageRange_FastForwardsEarlierSteps_ThenPlaysOnlyTheRange()
        {
            var solution = new SolutionMsg
            {
                sub_trajectory = new[] { Step("fr3_joint1", 0, 5), Step("fr3_joint1", 10, 15), Step("fr3_joint1", 20, 25) }
            };

            player.PlaySolution(solution, 1, 1);
            yield return new WaitForSeconds(0.1f);

            var positions = robot.Applied.Select(a => a.positions[0]).ToList();
            Assert.AreEqual(5.0, positions[0], "step 0 is applied instantly at its end state");
            Assert.That(positions, Has.Member(10.0).And.Member(15.0), "step 1 plays");
            Assert.That(positions, Has.No.Member(20.0).And.No.Member(25.0), "step 2 is after the range");
            Assert.IsTrue(player.IsPlaying, "a partial preview holds its end state");

            player.Stop();
            Assert.AreEqual(0.0, robot.Applied[^1].positions[0], "the pose from before the preview is restored");
            Assert.IsFalse(player.IsPlaying);
        }

        // --- name mapping -----------------------------------------------------------------

        [UnityTest]
        public IEnumerator RosJointNames_AreMappedToTheUnityRobot_AndGripperOnlySegmentsAreSkipped()
        {
            var solution = new SolutionMsg
            {
                sub_trajectory = new[] { Step("panda_finger_joint1", 0.0, 0.04), Step("panda_joint1", 1, 2) }
            };

            player.PlaySolution(solution);
            yield return new WaitForSeconds(0.1f);

            Assert.IsNull(player.LastProblem, "a gripper-only segment is skipped, not an error");
            Assert.IsTrue(robot.Applied.All(a => a.names.All(n => n.StartsWith("fr3_"))), "every applied name is a Unity name");
            Assert.That(robot.Applied.Select(a => a.positions[0]).ToList(), Has.Member(2.0));
            player.Stop();
        }

        [UnityTest]
        public IEnumerator NoMatchingJoint_ReportsAProblem()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("cannot preview"));
            string reported = null;
            player.OnProblem += p => reported = p;

            player.PlaySolution(new SolutionMsg { sub_trajectory = new[] { Step("panda_finger_joint1", 0.0, 0.04) } });
            yield return null;

            Assert.That(player.LastProblem, Does.Contain("panda_finger_joint1").And.Contain("fr3_joint1"));
            Assert.AreEqual(player.LastProblem, reported);
            Assert.IsFalse(player.IsPlaying);
        }

        // ---------- helpers ----------

        private GameObject Spawn(string id, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.SetParent(host.transform, false);
            go.transform.position = position;
            var publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = id;
            publisher.publishRateHz = 100f;
            registry.Adopt(go, id, EnvironmentOwner.Remote, PrimitiveType.Cube, false);
            spawned.Add(go);
            return go;
        }

        private static SubTrajectoryMsg Step(string joint, double from, double to) => new SubTrajectoryMsg
        {
            trajectory = new RobotTrajectoryMsg
            {
                joint_trajectory = new JointTrajectoryMsg
                {
                    joint_names = new[] { joint },
                    points = new[]
                    {
                        new JointTrajectoryPointMsg { positions = new[] { from } },
                        new JointTrajectoryPointMsg { positions = new[] { to }, time_from_start = new DurationMsg(0, 10_000_000) },
                    }
                }
            }
        };

        // Pose in ROS terms (world frame, or the link frame for an attachment).
        private static CollisionObjectMsg Box(string id, double x, double y, double z) => new CollisionObjectMsg
        {
            id = id,
            operation = CollisionObjectMsg.ADD,
            pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)),
            primitives = new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.1, 0.1, 0.1 } } }
        };

        private static void AssertClose(Vector3 expected, Vector3 actual, string why = null) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}{(why == null ? "" : ": " + why)}");
    }
}
