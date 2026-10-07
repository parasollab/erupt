using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Core-flow tests over the ROS seam. These are the checks that could previously only
    /// be made by hand in a headset with a live ROS stack — see backlog B16.
    /// </summary>
    public class CollisionObjectFlowTests
    {
        private FakeRosBus bus;
        private GameObject obstacle;
        private GameObject listenerHost;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
        }

        [TearDown]
        public void TearDown()
        {
            if (listenerHost != null) Object.DestroyImmediate(listenerHost);
            if (obstacle != null) Object.DestroyImmediate(obstacle);
            RosBus.Reset();
        }

        [Test]
        public void Seam_DefaultsToLiveConnection()
        {
            RosBus.Reset();
            Assert.IsFalse(RosBus.IsOverridden,
                "Unmodified scenes must reach the real ROSConnection.");
        }

        // Creating an obstacle must ADD it on /collision_object, or it never reaches
        // MoveIt's planning scene.
        [UnityTest]
        public IEnumerator CreatingAnObstacle_PublishesAddOnCollisionObject()
        {
            obstacle = MakeObstacle("unity_cube_test");

            yield return WaitForPublish();

            Assert.Contains("/collision_object", bus.RegisteredPublishers);
            Assert.GreaterOrEqual(bus.CountOn("/collision_object"), 1,
                "No CollisionObject was published for a newly created obstacle.");

            var msg = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.ADD, msg.operation, "First publish must be ADD (0).");
            Assert.AreEqual("unity_cube_test", msg.id);
        }

        // The publisher watches its own transform and emits MOVE on change. Undo in
        // Phase 2 has to cooperate with this rather than fight it.
        [UnityTest]
        public IEnumerator MovingAnObstacle_PublishesMove()
        {
            obstacle = MakeObstacle("unity_cube_move");

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            obstacle.transform.position += new Vector3(0.5f, 0f, 0f);
            yield return WaitForPublish();

            Assert.Greater(bus.CountOn("/collision_object"), afterAdd,
                "Moving an obstacle must publish an update.");
        }

        [UnityTest]
        public IEnumerator ScalingAnObstacle_PublishesUpdatedGeometry()
        {
            obstacle = MakeObstacle("unity_cube_scale");

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            obstacle.transform.localScale = new Vector3(2f, 3f, 4f);
            yield return WaitForPublish();

            Assert.Greater(bus.CountOn("/collision_object"), afterAdd,
                "Scale-only edits must republish geometry to MoveIt.");
            var msg = (CollisionObjectMsg)LastOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.ADD, msg.operation,
                "A scale change must replace geometry, not publish a pose-only MOVE.");
        }

        [UnityTest]
        public IEnumerator RuntimeUnityOwnedObject_WatcherEchoDoesNotBuildCoincidentGeometry()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            listener.requestInitialPlanningScene = false;
            yield return null; // Start subscribes to the fake bus.

            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = "unity_runtime_owned";
            listener.RegisterUnityOwnedObject(publisher.objectId, obstacle);

            bus.Inbound(listener.topic, MakeBoxCollisionObject(publisher.objectId));

            Assert.AreSame(obstacle, listener.objectsById[publisher.objectId]);
            Assert.AreEqual(0, obstacle.transform.childCount,
                "The watcher echo must not add a second renderer on top of the Unity-owned shape.");
        }

        [UnityTest]
        public IEnumerator Startup_RequestsAndAppliesExistingPlanningSceneObjects()
        {
            bus.SetServiceHandler("/get_planning_scene", _ =>
                new GetPlanningSceneResponse(new PlanningSceneMsg
                {
                    world = new PlanningSceneWorldMsg
                    {
                        collision_objects = new[] { MakeBoxCollisionObject("moveit_existing_box") }
                    }
                }));

            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            yield return null; // Start subscribes, registers the service, and requests the snapshot.

            Assert.Contains("/get_planning_scene", bus.RegisteredServices);
            Assert.IsTrue(listener.TryGetObject("moveit_existing_box", out GameObject spawned));
            Assert.AreEqual(1, spawned.transform.childCount,
                "The full planning-scene snapshot must be rendered even when no live diff arrives.");
        }

        // Guards the destroy -> REMOVE feedback loop that suppressRemoveOnDestroy exists
        // to prevent: an inbound REMOVE must not bounce a REMOVE back into MoveIt.
        [UnityTest]
        public IEnumerator SuppressRemoveOnDestroy_StopsTheRemoveEchoingBack()
        {
            obstacle = MakeObstacle("unity_cube_suppress");
            yield return WaitForPublish();

            var publisher = obstacle.GetComponent<CollisionObjectPublisher>();
            publisher.suppressRemoveOnDestroy = true;

            bus.Clear();
            Object.DestroyImmediate(obstacle);
            obstacle = null;
            yield return null;

            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "A ROS-commanded removal must not publish REMOVE back to MoveIt.");
        }

        [UnityTest]
        public IEnumerator DeletingAnObstacle_PublishesRemove()
        {
            obstacle = MakeObstacle("unity_cube_remove");
            yield return WaitForPublish();

            bus.Clear();
            Object.DestroyImmediate(obstacle);
            obstacle = null;
            yield return null;

            Assert.AreEqual(1, bus.CountOn("/collision_object"),
                "Deleting an obstacle must publish exactly one CollisionObject.");
            var msg = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.REMOVE, msg.operation, "Delete must publish REMOVE (1).");
        }

        // Obstacles publish at 1 Hz by default, so a moved obstacle can take up to a
        // second to reach MoveIt. Phase 2's undo has to account for that latency.
        [UnityTest]
        public IEnumerator PublishingIsRateLimited()
        {
            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = false;
            publisher.objectId = "unity_cube_rate";
            publisher.publishRateHz = 1f;

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            for (int i = 0; i < 5; i++)
            {
                obstacle.transform.position += new Vector3(0.1f, 0f, 0f);
                yield return null;
            }

            Assert.AreEqual(afterAdd, bus.CountOn("/collision_object"),
                "Moves within the rate-limit window must coalesce rather than flood /collision_object.");
        }

        private static IEnumerator WaitForPublish()
        {
            // Comfortably past the 100 Hz window used in these tests.
            yield return new WaitForSeconds(0.05f);
        }

        private static GameObject MakeObstacle(string id)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            var publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = false;
            publisher.objectId = id;

            // Production default is 1 Hz (CollisionObjectPublisher.publishRateHz), which
            // would make every test wait a full second. Raised here only to keep the
            // suite fast — the rate-limiting behavior itself is asserted below.
            publisher.publishRateHz = 100f;
            return go;
        }

        private static CollisionObjectMsg MakeBoxCollisionObject(string id)
        {
            return new CollisionObjectMsg
            {
                id = id,
                operation = CollisionObjectMsg.ADD,
                primitives = new[]
                {
                    new SolidPrimitiveMsg
                    {
                        type = SolidPrimitiveMsg.BOX,
                        dimensions = new[] { 1.0, 1.0, 1.0 }
                    }
                }
            };
        }

        private Unity.Robotics.ROSTCPConnector.MessageGeneration.Message FirstOn(string topic)
        {
            foreach (var m in bus.PublishedOn(topic)) return m;
            return null;
        }

        private Unity.Robotics.ROSTCPConnector.MessageGeneration.Message LastOn(string topic)
        {
            Unity.Robotics.ROSTCPConnector.MessageGeneration.Message last = null;
            foreach (var message in bus.PublishedOn(topic)) last = message;
            return last;
        }
    }
}
