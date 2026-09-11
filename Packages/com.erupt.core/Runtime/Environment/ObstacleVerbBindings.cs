using UnityEngine;
using Erupt.Interaction;
using Erupt.Environment;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.UiBindings
{
    /// <summary>
    /// Wires tier 2 verbs to ERUPT's existing behaviour.
    /// </summary>
    /// <remarks>
    /// This is the migration half of Phase 2: verbs that already worked from the wrist
    /// menu keep working from the contextual menu. Verbs with no implementation stay
    /// unbound, which renders them disabled rather than silently inert.
    ///
    /// Every mutation goes through the undo stack, because tier 1 exposes undo and redo
    /// and a change that bypasses the stack would make them lie.
    /// </remarks>
    public class ObstacleVerbBindings : MonoBehaviour
    {
        [SerializeField] private TierUiRig rig;
        [SerializeField] private SelectionService selection;
        [SerializeField] private EnvironmentRegistry registry;

        [Tooltip("Offset applied to a duplicated obstacle, matching the wrist menu's behaviour.")]
        [SerializeField] private Vector3 duplicateOffset = new(0.2f, 0f, 0f);

        private void Start()
        {
            if (rig == null) rig = FindFirstObjectByType<TierUiRig>();
            if (rig == null || rig.TierTwo == null)
            {
                Debug.LogError("ObstacleVerbBindings: no TierUiRig found — tier 2 verbs will all render disabled.");
                return;
            }

            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();

            BindObstacleVerbs();
        }

        private void BindObstacleVerbs()
        {
            var menu = rig.TierTwo;
            UndoStack undo = rig.UndoStack;

            menu.Bind("delete", selectable =>
            {
                GameObject target = selectable.GameObject;
                if (target == null) return;

                selection?.ClearSelection();
                if (TryPrimitive(target, out var primitive))
                    undo.Do(new DeleteObstacleCommand(target, primitive, registry));
                else
                    undo.Do(new DeleteMeshObstacleCommand(target, registry));   // a mug is cloned back, not rebuilt as a cube
            });

            menu.Bind("duplicate", selectable =>
            {
                GameObject original = selectable.GameObject;
                if (original == null) return;

                // Deselect first: the highlighter has swapped the original's material, and a
                // copy taken now would be born wearing the highlight as its real material.
                selection?.ClearSelection();

                GameObject spawned;
                if (TryPrimitive(original, out var primitive))
                {
                    var snapshot = ObstacleSnapshot.Capture(original, primitive);
                    snapshot.ObjectId = null;                // a duplicate is a new planning-scene object
                    snapshot.Position += duplicateOffset;
                    var create = new CreateObstacleCommand(snapshot, registry);
                    undo.Do(create);
                    spawned = create.Spawned;
                }
                else
                {
                    var clone = new DuplicateMeshObstacleCommand(original, registry, duplicateOffset);
                    undo.Do(clone);
                    spawned = clone.Spawned;
                }
                if (selection != null && spawned != null) selection.Select(SelectableMarker.Ensure(spawned, SelectionKind.Obstacle));
            });

            menu.Bind("snap", selectable =>
            {
                GameObject target = selectable.GameObject;
                if (target == null) return;

                Vector3 position = target.transform.position;
                Quaternion rotation = target.transform.rotation;
                Vector3 scale = target.transform.localScale;

                if (!ObstacleSnapping.TrySnapToSurface(target)) return;

                undo.Record(new TransformObstacleCommand(target, "Snap to Surface", position, rotation, scale));
            });

            // "resize" is bound to nothing on purpose: scaling is a spatial control and
            // Part 1 P3 puts it in the world as a widget, not on a menu. The verb stays
            // visible and disabled until that widget exists.
        }

        // Objects built through ObstacleFactory record their primitive; legacy shapes are
        // recognised by Unity's built-in mesh names. Anything else is a mesh and is cloned
        // rather than rebuilt — the old "default to Cube" turned a duplicated mug into a cube.
        private static bool TryPrimitive(GameObject obstacle, out PrimitiveType primitive)
        {
            var env = obstacle.GetComponent<EnvironmentObject>();
            if (env != null && env.Primitive.HasValue) { primitive = env.Primitive.Value; return true; }

            var filter = obstacle.GetComponent<MeshFilter>();
            string mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "";
            switch (mesh)
            {
                case "Cube":     primitive = PrimitiveType.Cube;     return true;
                case "Sphere":   primitive = PrimitiveType.Sphere;   return true;
                case "Cylinder": primitive = PrimitiveType.Cylinder; return true;
                case "Capsule":  primitive = PrimitiveType.Capsule;  return true;
                case "Plane":    primitive = PrimitiveType.Plane;    return true;
            }
            primitive = default;
            return false;
        }
    }
}
