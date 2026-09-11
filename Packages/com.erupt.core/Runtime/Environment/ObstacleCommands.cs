using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using Erupt.Interaction;
using Erupt.Environment;

namespace Erupt.Obstacles
{
    /// <summary>
    /// Creating an obstacle, reversibly. Undo unregisters and destroys it so a planner
    /// sync can remove it from its scene; redo rebuilds it under the same id.
    /// </summary>
    public class CreateObstacleCommand : IUndoableCommand
    {
        private readonly ObstacleSnapshot snapshot;
        private readonly EnvironmentRegistry registry;
        private GameObject spawned;

        public CreateObstacleCommand(ObstacleSnapshot snapshot, EnvironmentRegistry registry = null)
        {
            // Fix the id at construction, so redo recreates the same planning-scene object
            // rather than leaking a new one on every cycle.
            if (string.IsNullOrEmpty(snapshot.ObjectId))
                snapshot.ObjectId = ObstacleFactory.NewObjectId(snapshot.PrimitiveType);

            this.snapshot = snapshot;
            this.registry = registry;
        }

        public string Label => "Create " + snapshot.PrimitiveType;

        public GameObject Spawned => spawned;

        public void Execute() => spawned = ObstacleFactory.Create(snapshot, registry);

        public void Undo()
        {
            ObstacleFactory.Destroy(spawned, registry);
            spawned = null;
        }
    }

    /// <summary>
    /// Deleting an obstacle, reversibly.
    /// </summary>
    /// <remarks>
    /// Undo recreates rather than resurrects: the object is gone from Unity and from
    /// the planner's scene, so putting it back means rebuilding it under its original id
    /// and letting the sync re-add it.
    /// </remarks>
    public class DeleteObstacleCommand : IUndoableCommand
    {
        private readonly ObstacleSnapshot snapshot;
        private readonly EnvironmentRegistry registry;
        private GameObject target;

        public DeleteObstacleCommand(GameObject obstacle, PrimitiveType primitiveType,
                                     EnvironmentRegistry registry = null)
        {
            target = obstacle;
            snapshot = ObstacleSnapshot.Capture(obstacle, primitiveType);
            this.registry = registry;
        }

        public string Label => "Delete " + snapshot.Name;

        public GameObject Restored => target;

        public void Execute()
        {
            ObstacleFactory.Destroy(target, registry);
            target = null;
        }

        public void Undo() => target = ObstacleFactory.Create(snapshot, registry);
    }

    /// <summary>
    /// A pose or scale change — move, scale or snap-to-surface.
    /// </summary>
    /// <remarks>
    /// Recorded after the fact, because a drag has already moved the object by the time
    /// it ends. Undo writes the transform back; a planner sync notices the moved
    /// transform on its own schedule, so no explicit republish is needed here.
    /// </remarks>
    public class TransformObstacleCommand : IUndoableCommand
    {
        private readonly GameObject target;
        private readonly Vector3 beforePosition, beforeScale, afterPosition, afterScale;
        private readonly Quaternion beforeRotation, afterRotation;

        public TransformObstacleCommand(GameObject obstacle, string label,
                                        Vector3 beforePosition, Quaternion beforeRotation, Vector3 beforeScale)
        {
            target = obstacle;
            Label = label;

            this.beforePosition = beforePosition;
            this.beforeRotation = beforeRotation;
            this.beforeScale = beforeScale;

            afterPosition = obstacle.transform.position;
            afterRotation = obstacle.transform.rotation;
            afterScale = obstacle.transform.localScale;
        }

        public string Label { get; }

        public void Execute() => Apply(afterPosition, afterRotation, afterScale);

        public void Undo() => Apply(beforePosition, beforeRotation, beforeScale);

        private void Apply(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (target == null) return;

            target.transform.SetPositionAndRotation(position, rotation);
            target.transform.localScale = scale;

            // Snap-to-surface caches a rotation for the grab pipeline; without this an
            // undone snap reverts visually and then springs back on the next grab.
            var lockPose = target.GetComponent<XRGrabTransformerLockPose>();
            if (lockPose != null) lockPose.SyncInitialRotation();
        }
    }
}
