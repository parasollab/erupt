using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Shape;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Previewing a stage's partial solution from its start_scene: objects are shown where
    /// that stage begins (on the table or in the gripper), nothing reaches MoveIt, and the
    /// scene is put back afterwards.
    /// </summary>
    public class MTCStartScenePreviewTests
    {
        private const string SceneTopic = "/collision_objects_ros";

        private FakeRosBus bus;
        private GameObject host;
        private GameObject robot;
        private Transform hand;
        private CollisionObjectsListenerSimple scene;
        private MTCTrajectoryPlayer player;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
        }

        [TearDown]
        public void TearDown()
        {
            var spawned = scene != null
                ? new System.Collections.Generic.List<GameObject>(scene.objectsById.Values)
                : new System.Collections.Generic.List<GameObject>();
            if (host != null) Object.DestroyImmediate(host);
            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            if (robot != null) Object.DestroyImmediate(robot);
            RosBus.Reset();
        }

        [UnityTest]
        public IEnumerator StartScene_PlacesObjects_PublishesNothing_AndIsRestored()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", new Vector3(1f, 0f, 0f)));
            bus.Inbound(SceneTopic, Box("bowl", new Vector3(0f, 0f, 1f)));
            yield return null; // publishers start
            scene.TryGetObject("cube", out var cube);
            scene.TryGetObject("bowl", out var bowl);
            foreach (var go in new[] { cube, bowl })
                foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>())
                    pub.publishRateHz = 100f;
            Transform cubeParent = cube.transform.parent;
            bus.Clear();

            // The stage starts with the cube already in the hand and the bowl moved.
            var solution = new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } };
            solution.start_scene.world.collision_objects = new[] { Box("bowl", new Vector3(0f, 0f, 2f)) };
            solution.start_scene.robot_state.attached_collision_objects = new[]
            {
                new AttachedCollisionObjectMsg { link_name = "panda_hand", @object = Box("cube", Vector3.zero, 0.0, 0.0, 0.1) }
            };

            player.PlaySolution(solution, useStartScene: true);
            yield return null;

            Assert.IsTrue(player.IsPlaying, "The end state is held so a motionless stage can be seen.");
            Assert.AreSame(hand, cube.transform.parent, "An object attached in the start scene rides the gripper.");
            AssertClose(hand.position + hand.up * 0.1f, cube.transform.position);
            AssertClose(new Vector3(0f, 0f, 2f), bowl.transform.position);

            yield return new WaitForSeconds(0.05f);
            player.Stop();
            yield return new WaitForSeconds(0.05f);

            Assert.AreSame(cubeParent, cube.transform.parent);
            AssertClose(new Vector3(1f, 0f, 0f), cube.transform.position);
            AssertClose(new Vector3(0f, 0f, 1f), bowl.transform.position);
            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "Neither the preview nor its restore may reach MoveIt's live scene.");
        }

        [UnityTest]
        public IEnumerator StartScene_LeavesAnObjectTheRealRobotIsCarryingAlone()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", new Vector3(1f, 0f, 0f)));
            scene.TryGetObject("cube", out var cube);
            scene.attachedIds.Add("cube");

            var solution = new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } };
            solution.start_scene.world.collision_objects = new[] { Box("cube", new Vector3(5f, 0f, 0f)) };

            player.PlaySolution(solution, useStartScene: true);
            yield return null;

            AssertClose(new Vector3(1f, 0f, 0f), cube.transform.position);
            player.Stop();
        }

        [UnityTest]
        public IEnumerator EmptyStartScene_WarnsAndPlaysFromTheCurrentState()
        {
            yield return Build();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no start scene"));

            player.PlaySolution(new SolutionMsg { sub_trajectory = new[] { new SubTrajectoryMsg() } }, useStartScene: true);
            yield return null;

            Assert.IsNull(player.LastProblem);
            player.Stop();
        }

        // ---------- helpers ----------

        private IEnumerator Build()
        {
            robot = new GameObject("robot");
            hand = new GameObject("fr3_hand").transform;
            hand.SetParent(robot.transform, false);
            hand.localPosition = new Vector3(0f, 0.5f, 0f);

            host = new GameObject("listeners");
            scene = host.AddComponent<CollisionObjectsListenerSimple>();
            scene.requestInitialPlanningScene = false;

            var ik = host.AddComponent<DirectArticulationIKController>();
            ik.enabled = false;
            SetPrivate(ik, "robotRoot", robot.transform);

            player = host.AddComponent<MTCTrajectoryPlayer>();
            SetPrivate(player, "ikController", ik);
            SetPrivate(player, "sceneListener", scene);

            yield return null;
        }

        private static void SetPrivate(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        // World pose given in Unity terms, converted to the project's ROS convention (x, z, y).
        private static CollisionObjectMsg Box(string id, Vector3 unityPosition) =>
            Box(id, unityPosition, unityPosition.x, unityPosition.z, unityPosition.y);

        private static CollisionObjectMsg Box(string id, Vector3 _, double x, double y, double z) => new CollisionObjectMsg
        {
            id = id,
            operation = CollisionObjectMsg.ADD,
            pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)),
            primitives = new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.1, 0.1, 0.1 } } }
        };

        private static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}");
    }
}
