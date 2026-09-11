using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Moveit;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Undo of obstacle mutations, checked against the ROS traffic it produces through
    /// <see cref="MoveItPlanningSceneSync"/>. These run over the FakeRosBus seam, so they
    /// assert what MoveIt would actually see. The mirror-side assertions live in
    /// the core Environment tests.
    /// </summary>
    public class ObstacleCommandTests
    {
        private FakeRosBus bus;
        private UndoStack stack;
        private GameObject registryHost;
        private GameObject syncHost;
        private EnvironmentRegistry registry;
        private GameObject spawned;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            stack = new UndoStack();

            registryHost = new GameObject("environment");
            registry = registryHost.AddComponent<EnvironmentRegistry>();

            syncHost = new GameObject("collision-listener");
            var listener = syncHost.AddComponent<CollisionObjectsListenerSimple>();
            listener.requestInitialPlanningScene = false;
            syncHost.AddComponent<MoveItPlanningSceneSync>();
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            if (spawned != null) Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(syncHost);
            Object.DestroyImmediate(registryHost);
        }

        [UnityTest]
        public IEnumerator Create_GivesTheObstacleAPublisherThroughTheSync()
        {
            var command = new CreateObstacleCommand(Snapshot("unity_cube_sync"), registry);
            stack.Do(command);
            spawned = command.Spawned;

            var publisher = spawned.GetComponent<CollisionObjectPublisher>();
            Assert.IsNotNull(publisher, "The sync must attach a publisher to a Unity-owned obstacle.");
            Assert.AreEqual("unity_cube_sync", publisher.objectId);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Create_ThenUndo_RemovesTheObjectFromThePlanningScene()
        {
            var command = new CreateObstacleCommand(Snapshot("unity_cube_undo"), registry);
            stack.Do(command);
            spawned = command.Spawned;

            yield return Settle();
            Assert.AreEqual(CollisionObjectMsg.ADD, FirstOperation(), "Creation must ADD.");

            bus.Clear();
            stack.Undo();
            spawned = null;
            yield return null;

            Assert.AreEqual(CollisionObjectMsg.REMOVE, FirstOperation(),
                "Undoing a creation must REMOVE it from MoveIt, not just destroy the GameObject.");
        }

        [UnityTest]
        public IEnumerator Delete_ThenUndo_ReAddsUnderTheSameObjectId()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_restore"), registry);
            create.Execute();
            spawned = create.Spawned;
            yield return Settle();

            var delete = new DeleteObstacleCommand(spawned, PrimitiveType.Cube, registry);
            stack.Do(delete);
            spawned = null;
            yield return null;

            bus.Clear();
            stack.Undo();
            spawned = delete.Restored;
            yield return Settle();

            Assert.IsNotNull(spawned, "Undo must recreate the obstacle.");
            var publisher = spawned.GetComponent<CollisionObjectPublisher>();
            Assert.AreEqual("unity_cube_restore", publisher.objectId,
                "The restored obstacle must reuse its original planning-scene id.");
            Assert.AreEqual(CollisionObjectMsg.ADD, FirstOperation(), "Restoring must re-ADD.");
        }

        [UnityTest]
        public IEnumerator RemoteRemoval_DoesNotEchoARemove()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_remote"), registry);
            create.Execute();
            spawned = create.Spawned;
            yield return Settle();

            bus.Clear();
            ObstacleFactory.Destroy(spawned, registry, publishRemoval: false);
            spawned = null;
            yield return null;

            Assert.AreEqual(-1, FirstOperation(),
                "A removal commanded by ROS must not be echoed back into MoveIt's live scene.");
        }

        private static ObstacleSnapshot Snapshot(string id) => new ObstacleSnapshot
        {
            PrimitiveType = PrimitiveType.Cube,
            ObjectId = id,
            Name = id ?? "cube",
            Position = Vector3.zero,
            Rotation = Quaternion.identity,
            Scale = Vector3.one
        };

        // CollisionObjectPublisher rate-limits to 1 Hz, so a publish needs a real wait.
        private static IEnumerator Settle()
        {
            yield return new WaitForSeconds(1.1f);
        }

        private int FirstOperation()
        {
            foreach (var m in bus.PublishedOn("/collision_object"))
                return ((CollisionObjectMsg)m).operation;
            return -1;
        }
    }
}
