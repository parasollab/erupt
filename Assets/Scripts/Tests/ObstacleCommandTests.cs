using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Moveit;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Undo of obstacle mutations, checked against the ROS traffic it produces. These run
    /// over the FakeRosBus seam, so they assert what MoveIt would actually see.
    /// </summary>
    public class ObstacleCommandTests
    {
        private FakeRosBus bus;
        private UndoStack stack;
        private GameObject spawned;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            stack = new UndoStack();
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            if (spawned != null) Object.DestroyImmediate(spawned);
        }

        [UnityTest]
        public IEnumerator Create_ThenUndo_RemovesTheObjectFromThePlanningScene()
        {
            var command = new CreateObstacleCommand(Snapshot("unity_cube_undo"));
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

        // The point of the whole snapshot design: an undone delete has to come back under
        // the same planning-scene id, or MoveIt's world diverges from what the user sees.
        [UnityTest]
        public IEnumerator Delete_ThenUndo_RestoresTheSameObjectId()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_restore"));
            create.Execute();
            spawned = create.Spawned;
            yield return Settle();

            var delete = new DeleteObstacleCommand(spawned, PrimitiveType.Cube);
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
        public IEnumerator Delete_ThenUndo_RestoresPoseAndScale()
        {
            var snapshot = Snapshot("unity_cube_pose");
            snapshot.Position = new Vector3(1f, 2f, 3f);
            snapshot.Scale = new Vector3(0.25f, 0.5f, 0.75f);

            var create = new CreateObstacleCommand(snapshot);
            create.Execute();
            spawned = create.Spawned;
            yield return Settle();

            var delete = new DeleteObstacleCommand(spawned, PrimitiveType.Cube);
            stack.Do(delete);
            spawned = null;
            yield return null;

            stack.Undo();
            spawned = delete.Restored;

            Assert.AreEqual(new Vector3(1f, 2f, 3f), spawned.transform.position);
            Assert.AreEqual(new Vector3(0.25f, 0.5f, 0.75f), spawned.transform.localScale);
        }

        [UnityTest]
        public IEnumerator Transform_UndoAndRedo_RoundTrip()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_move"));
            create.Execute();
            spawned = create.Spawned;
            yield return Settle();

            Vector3 before = spawned.transform.position;
            Quaternion beforeRot = spawned.transform.rotation;
            Vector3 beforeScale = spawned.transform.localScale;

            spawned.transform.position = before + new Vector3(0f, 1f, 0f);
            stack.Record(new TransformObstacleCommand(spawned, "Move Cube", before, beforeRot, beforeScale));

            stack.Undo();
            Assert.AreEqual(before, spawned.transform.position, "Undo must restore the original pose.");

            stack.Redo();
            Assert.AreEqual(before + new Vector3(0f, 1f, 0f), spawned.transform.position);
        }

        [UnityTest]
        public IEnumerator Create_Undo_Redo_ReusesTheSameObjectId()
        {
            var command = new CreateObstacleCommand(Snapshot(null));
            stack.Do(command);
            spawned = command.Spawned;
            string firstId = spawned.GetComponent<CollisionObjectPublisher>().objectId;
            yield return Settle();

            stack.Undo();
            yield return null;
            stack.Redo();
            spawned = command.Spawned;
            yield return Settle();

            Assert.AreEqual(firstId, spawned.GetComponent<CollisionObjectPublisher>().objectId,
                "Redo must not leak a new planning-scene object on every cycle.");
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
