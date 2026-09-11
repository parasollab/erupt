using UnityEngine;

namespace Erupt.Environment
{
    /// <summary>Who created the object, which decides who is allowed to remove it.</summary>
    public enum EnvironmentOwner
    {
        /// <summary>Built in Unity (placement tool, duplicate, undo). Synced out to planners.</summary>
        Unity,
        /// <summary>Mirrored from a remote scene (a planner's world). Removed only on its say-so.</summary>
        Remote
    }

    /// <summary>Where a removal was commanded from, so a sync can decide whether to echo it.</summary>
    public enum RemovalOrigin
    {
        /// <summary>The user or an undo removed it here; syncs should propagate the removal.</summary>
        Local,
        /// <summary>The remote scene removed it; syncs must not echo a removal back.</summary>
        Remote
    }

    /// <summary>
    /// Identity of one object in the environment mirror (Guidelines P1: the planner's scene
    /// stays authoritative; core holds the Unity mirror). Replaces the "Selectable" tag plus
    /// mesh-name sniffing that used to stand in for this.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnvironmentObject : MonoBehaviour
    {
        [SerializeField] private string id;
        [SerializeField] private EnvironmentOwner owner = EnvironmentOwner.Unity;
        [SerializeField] private bool hasPrimitive;
        [SerializeField] private PrimitiveType primitive = PrimitiveType.Cube;
        [SerializeField] private bool isMesh;

        /// <summary>Scene-wide id; for planner-synced objects this is the collision object id.</summary>
        public string Id => id;
        public EnvironmentOwner Owner => owner;
        /// <summary>The primitive this was built from, or null for a mesh / unknown shape.</summary>
        public PrimitiveType? Primitive => hasPrimitive ? primitive : null;
        public bool IsMesh => isMesh;

        /// <summary>Robot link this object is attached to, or null.</summary>
        public Transform AttachedTo { get; internal set; }

        public void Initialize(string objectId, EnvironmentOwner objectOwner, PrimitiveType? primitiveType, bool mesh)
        {
            id = objectId;
            owner = objectOwner;
            hasPrimitive = primitiveType.HasValue;
            primitive = primitiveType ?? PrimitiveType.Cube;
            isMesh = mesh;
        }
    }
}
