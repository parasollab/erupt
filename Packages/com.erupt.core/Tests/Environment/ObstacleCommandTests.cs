using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Obstacles;

namespace Erupt.Environment.Tests
{
    /// <summary>
    /// Undo of obstacle mutations as seen by the environment mirror. No planner is
    /// involved: what MoveIt sees is asserted in the MoveIt plugin's own tests.
    /// </summary>
    public class ObstacleCommandTests
    {
        private GameObject registryHost;
        private EnvironmentRegistry registry;
        private UndoStack stack;
        private GameObject spawned;

        [SetUp]
        public void SetUp()
        {
            registryHost = new GameObject("environment");
            registry = registryHost.AddComponent<EnvironmentRegistry>();
            stack = new UndoStack();
        }

        [TearDown]
        public void TearDown()
        {
            if (spawned != null) Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(registryHost);
        }

        [Test]
        public void Create_RegistersAUnityOwnedObjectUnderItsId()
        {
            var command = new CreateObstacleCommand(Snapshot("unity_cube_register"), registry);
            stack.Do(command);
            spawned = command.Spawned;

            Assert.IsTrue(registry.TryGetObject("unity_cube_register", out var env));
            Assert.AreSame(spawned, env.gameObject);
            Assert.AreEqual(EnvironmentOwner.Unity, env.Owner);
            Assert.AreEqual(PrimitiveType.Cube, env.Primitive);
        }

        [UnityTest]
        public IEnumerator Create_ThenUndo_UnregistersWithLocalOrigin()
        {
            RemovalOrigin? seen = null;
            registry.Removed += (obj, origin) => seen = origin;

            var command = new CreateObstacleCommand(Snapshot("unity_cube_undo"), registry);
            stack.Do(command);
            spawned = command.Spawned;

            stack.Undo();
            spawned = null;
            yield return null;

            Assert.AreEqual(RemovalOrigin.Local, seen, "An undone creation is a local removal; syncs must propagate it.");
            Assert.IsFalse(registry.TryGet("unity_cube_undo", out _));
        }

        // The point of the whole snapshot design: an undone delete has to come back under
        // the same id, or a planner's world diverges from what the user sees.
        [UnityTest]
        public IEnumerator Delete_ThenUndo_RestoresTheSameObjectId()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_restore"), registry);
            create.Execute();
            spawned = create.Spawned;

            var delete = new DeleteObstacleCommand(spawned, PrimitiveType.Cube, registry);
            stack.Do(delete);
            spawned = null;
            yield return null;

            Assert.IsFalse(registry.TryGet("unity_cube_restore", out _), "Delete must leave the mirror.");

            stack.Undo();
            spawned = delete.Restored;

            Assert.IsNotNull(spawned, "Undo must recreate the obstacle.");
            Assert.AreEqual("unity_cube_restore", spawned.GetComponent<EnvironmentObject>().Id,
                "The restored obstacle must reuse its original id.");
            Assert.IsTrue(registry.TryGet("unity_cube_restore", out var go));
            Assert.AreSame(spawned, go);
        }

        [UnityTest]
        public IEnumerator Delete_ThenUndo_RestoresPoseAndScale()
        {
            var snapshot = Snapshot("unity_cube_pose");
            snapshot.Position = new Vector3(1f, 2f, 3f);
            snapshot.Scale = new Vector3(0.25f, 0.5f, 0.75f);

            var create = new CreateObstacleCommand(snapshot, registry);
            create.Execute();
            spawned = create.Spawned;

            var delete = new DeleteObstacleCommand(spawned, PrimitiveType.Cube, registry);
            stack.Do(delete);
            spawned = null;
            yield return null;

            stack.Undo();
            spawned = delete.Restored;

            Assert.AreEqual(new Vector3(1f, 2f, 3f), spawned.transform.position);
            Assert.AreEqual(new Vector3(0.25f, 0.5f, 0.75f), spawned.transform.localScale);
        }

        [Test]
        public void Transform_UndoAndRedo_RoundTrip()
        {
            var create = new CreateObstacleCommand(Snapshot("unity_cube_move"), registry);
            create.Execute();
            spawned = create.Spawned;

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
            var command = new CreateObstacleCommand(Snapshot(null), registry);
            stack.Do(command);
            spawned = command.Spawned;
            string firstId = spawned.GetComponent<EnvironmentObject>().Id;
            Assert.IsFalse(string.IsNullOrEmpty(firstId));

            stack.Undo();
            yield return null;
            stack.Redo();
            spawned = command.Spawned;

            Assert.AreEqual(firstId, spawned.GetComponent<EnvironmentObject>().Id,
                "Redo must not leak a new id on every cycle.");
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
    }
}
