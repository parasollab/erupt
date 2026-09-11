using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;

namespace Erupt.Obstacles
{
    /// <summary>
    /// Duplicating an object that is not a primitive (a scene mesh such as a mug), reversibly.
    /// A primitive can be rebuilt from a snapshot; a mesh can only be cloned, so this
    /// instantiates the original and gives the clone a new identity in the mirror.
    /// </summary>
    /// <remarks>
    /// The clone carries copies of whatever the original had (grab controller, planner
    /// publisher); a planner sync recognises an <c>Added</c> object whose publisher still
    /// names the source id and re-keys it. Core only sets the <see cref="EnvironmentObject"/>.
    /// </remarks>
    public class DuplicateMeshObstacleCommand : IUndoableCommand
    {
        private readonly GameObject original;
        private readonly EnvironmentRegistry registry;
        private readonly Vector3 offset;
        private readonly string id;
        private GameObject spawned;

        public DuplicateMeshObstacleCommand(GameObject original, EnvironmentRegistry registry, Vector3 offset)
        {
            this.original = original;
            this.registry = registry;
            this.offset = offset;
            id = ObstacleFactory.NewObjectId("mesh");     // fixed here so redo reuses it
        }

        public string Label => "Duplicate " + (original != null ? original.name : "object");
        public GameObject Spawned => spawned;

        public void Execute()
        {
            if (original == null) return;
            spawned = Object.Instantiate(original, original.transform.position + offset, original.transform.rotation);
            spawned.name = original.name;
            spawned.transform.localScale = original.transform.localScale;
            if (!string.IsNullOrEmpty(original.tag)) spawned.tag = original.tag;

            var source = original.GetComponent<EnvironmentObject>();
            var env = spawned.GetComponent<EnvironmentObject>() ?? spawned.AddComponent<EnvironmentObject>();
            env.Initialize(id, EnvironmentOwner.Unity, source != null ? source.Primitive : null, mesh: true);
            SelectableMarker.Ensure(spawned, SelectionKind.Obstacle);
            registry?.Register(env);
        }

        public void Undo()
        {
            ObstacleFactory.Destroy(spawned, registry);
            spawned = null;
        }
    }

    /// <summary>
    /// Deleting a non-primitive object, reversibly. Execute keeps an inactive clone (its
    /// components never start, so nothing is published) and destroys the original; Undo
    /// activates the clone under the original id so the planner re-adds it.
    /// </summary>
    public class DeleteMeshObstacleCommand : IUndoableCommand
    {
        private readonly EnvironmentRegistry registry;
        private readonly string id;
        private readonly string name;
        private GameObject live;
        private GameObject kept;

        public DeleteMeshObstacleCommand(GameObject target, EnvironmentRegistry registry)
        {
            live = target;
            this.registry = registry;
            var env = target != null ? target.GetComponent<EnvironmentObject>() : null;
            id = env != null ? env.Id : null;
            name = target != null ? target.name : "object";
        }

        public string Label => "Delete " + name;
        public GameObject Restored => live;

        public void Execute()
        {
            if (live == null) return;
            bool wasActive = live.activeSelf;
            live.SetActive(false);                                  // so the clone starts inactive too
            kept = Object.Instantiate(live);
            kept.name = live.name;
            kept.hideFlags = HideFlags.HideInHierarchy;
            live.SetActive(wasActive);

            ObstacleFactory.Destroy(live, registry);                // Local removal: the planner sync propagates it
            live = null;
        }

        public void Undo()
        {
            if (kept == null) return;
            live = kept;
            kept = null;
            live.hideFlags = HideFlags.None;
            var env = live.GetComponent<EnvironmentObject>();
            if (env != null && !string.IsNullOrEmpty(id) && env.Id != id)
                env.Initialize(id, env.Owner, env.Primitive, env.IsMesh);
            live.SetActive(true);
            if (env != null) registry?.Register(env);
        }
    }
}
