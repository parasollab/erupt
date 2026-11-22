using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Interaction.Tests;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.Environment.Tests
{
    /// <summary>
    /// A drag intent moves the selected obstacle (what the mouse does, having no XR
    /// interactor): only the selection, only while its grab interactable is enabled, and the
    /// move can be undone.
    /// </summary>
    public class ObstacleDragBindingTests
    {
        private GameObject eruptGo, routerGo, sourceGo, registryHost, cube, other;
        private InteractionRouter router;
        private TestInteractionSource source;
        private SelectionService selection;
        private TierUiRig rig;
        private ObstacleDragBinding binding;
        private EnvironmentRegistry registry;
        private double time;

        // Far from whatever other tests left behind.
        private static readonly Vector3 Origin = new Vector3(80f, 1f, 0f);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            sourceGo = new GameObject("source");
            source = sourceGo.AddComponent<TestInteractionSource>();
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();

            registryHost = new GameObject("environment");
            registry = registryHost.AddComponent<EnvironmentRegistry>();

            eruptGo = new GameObject("ERUPT");
            selection = eruptGo.AddComponent<SelectionService>();
            rig = eruptGo.AddComponent<TierUiRig>();
            binding = eruptGo.AddComponent<ObstacleDragBinding>();

            cube = Make("drag_cube", Origin + Vector3.forward * 2f);
            other = Make("other_cube", Origin + Vector3.forward * 2f + Vector3.right * 1.5f);
            yield return null;
            yield return null;   // the grab controllers have looked at the selection
            Physics.SyncTransforms();
            time = 1.0;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(cube);
            Object.DestroyImmediate(other);
            Object.DestroyImmediate(eruptGo);
            Object.DestroyImmediate(registryHost);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(sourceGo);
        }

        private GameObject Make(string id, Vector3 position) => ObstacleFactory.Create(new ObstacleSnapshot
        {
            PrimitiveType = PrimitiveType.Cube,
            Name = id,
            ObjectId = id,
            Position = position,
            Rotation = Quaternion.identity,
            Scale = Vector3.one * 0.3f
        }, registry);

        // The test source's ray points along +Z from where it is put.
        private void Emit(IntentKind kind, Vector3 from)
        {
            time += 0.02;
            source.EmitAt(kind, from, Modality.Mouse, time);
        }

        private IEnumerator Select(GameObject go)
        {
            selection.Select(go.GetComponent<ISelectable>());
            yield return null;
            Assert.IsTrue(go.GetComponent<XRGrabInteractable>().enabled, "The selected obstacle can be grabbed.");
        }

        [UnityTest]
        public IEnumerator ADrag_MovesTheSelectedObstacle_AndCanBeUndone()
        {
            yield return Select(cube);
            Vector3 before = cube.transform.position;

            Emit(IntentKind.BeginDrag, Origin);
            Assert.IsTrue(binding.IsDragging(source.id));
            Vector3 to = Origin + new Vector3(0.4f, 0.25f, 0f);
            for (int i = 0; i < 60; i++) Emit(IntentKind.Drag, to);   // the router smooths a drag
            Emit(IntentKind.EndDrag, to);
            Assert.IsFalse(binding.IsDragging(source.id));

            Vector3 moved = cube.transform.position - before;
            Assert.AreEqual(0.4f, moved.x, 0.01f);
            Assert.AreEqual(0.25f, moved.y, 0.01f);
            Assert.AreEqual(0f, moved.z, 1e-3f, "The obstacle keeps its distance along the ray.");

            Assert.IsTrue(rig.UndoStack.CanUndo, "The move is on the undo stack.");
            rig.UndoStack.Undo();
            Assert.Less(Vector3.Distance(before, cube.transform.position), 1e-4f);
            rig.UndoStack.Redo();
            Assert.AreEqual(0.4f, (cube.transform.position - before).x, 0.01f);
        }

        [UnityTest]
        public IEnumerator AnObstacleThatIsNotTheSelection_StaysWhereItIs()
        {
            yield return Select(other);
            Vector3 before = cube.transform.position;

            Emit(IntentKind.BeginDrag, Origin);
            Assert.IsFalse(binding.IsDragging(source.id), "Like a grab: only the selected obstacle moves.");
            for (int i = 0; i < 10; i++) Emit(IntentKind.Drag, Origin + Vector3.right * 0.4f);
            Emit(IntentKind.EndDrag, Origin + Vector3.right * 0.4f);

            Assert.AreEqual(before, cube.transform.position);
            Assert.IsFalse(rig.UndoStack.CanUndo);
        }

        [UnityTest]
        public IEnumerator AnObstacleARobotCarries_IsNotDragged_AndADragEndsWhenItIsPickedUp()
        {
            yield return Select(cube);
            var grab = cube.GetComponent<SelectableGrabController>();
            Vector3 before = cube.transform.position;

            // Locked before the drag: nothing starts.
            grab.SetLocked(true);
            Emit(IntentKind.BeginDrag, Origin);
            Assert.IsFalse(binding.IsDragging(source.id));
            Emit(IntentKind.EndDrag, Origin);

            // Locked during the drag: the binding lets go.
            grab.SetLocked(false);
            Emit(IntentKind.BeginDrag, Origin);
            Assert.IsTrue(binding.IsDragging(source.id));
            grab.SetLocked(true);
            for (int i = 0; i < 10; i++) Emit(IntentKind.Drag, Origin + Vector3.right * 0.4f);
            Assert.IsFalse(binding.IsDragging(source.id));
            Assert.AreEqual(before, cube.transform.position);
        }

        [UnityTest]
        public IEnumerator AMissedDrag_AndADragOnSomethingElse_DoNothing()
        {
            yield return Select(cube);
            Emit(IntentKind.BeginDrag, Origin + Vector3.up * 5f);   // past everything
            Assert.IsFalse(binding.IsDragging(source.id));

            // Something that is not an obstacle.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = Origin + Vector3.forward * 1f;
            Physics.SyncTransforms();
            Emit(IntentKind.BeginDrag, Origin);
            Assert.IsFalse(binding.IsDragging(source.id));
            Object.DestroyImmediate(wall);
            yield return null;
        }
    }
}
