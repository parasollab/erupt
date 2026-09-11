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
        [SerializeField] private SelectionManager selectionManager;
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

            if (selectionManager == null) selectionManager = FindFirstObjectByType<SelectionManager>();
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

                undo.Do(new DeleteObstacleCommand(target, PrimitiveTypeOf(target), registry));
                selectionManager?.ClearSelection();
            });

            menu.Bind("duplicate", selectable =>
            {
                GameObject original = selectable.GameObject;
                if (original == null) return;

                var snapshot = ObstacleSnapshot.Capture(original, PrimitiveTypeOf(original));
                snapshot.ObjectId = null;                    // a duplicate is a new planning-scene object
                snapshot.Position += duplicateOffset;

                var create = new CreateObstacleCommand(snapshot, registry);
                undo.Do(create);
                selectionManager?.SetSelectedObject(create.Spawned);
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

        // Objects built through ObstacleFactory record their primitive; anything else
        // (wrist-menu shapes, remote objects) falls back to the mesh-name sniffing the
        // wrist menu always used for duplicate.
        private static PrimitiveType PrimitiveTypeOf(GameObject obstacle)
        {
            var env = obstacle.GetComponent<EnvironmentObject>();
            if (env != null && env.Primitive.HasValue) return env.Primitive.Value;

            var filter = obstacle.GetComponent<MeshFilter>();
            string mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "";

            if (mesh.Contains("Sphere")) return PrimitiveType.Sphere;
            if (mesh.Contains("Cylinder")) return PrimitiveType.Cylinder;
            if (mesh.Contains("Capsule")) return PrimitiveType.Capsule;
            if (mesh.Contains("Plane")) return PrimitiveType.Plane;

            return PrimitiveType.Cube;
        }
    }
}
