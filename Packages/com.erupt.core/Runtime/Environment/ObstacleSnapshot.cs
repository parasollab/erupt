using UnityEngine;
using Erupt.Environment;

namespace Erupt.Obstacles
{
    /// <summary>
    /// Everything needed to rebuild an obstacle exactly, including its planning-scene id.
    /// </summary>
    /// <remarks>
    /// The id matters more than it looks: a planner's scene is keyed by object id, so
    /// undoing a deletion has to recreate the object under its original id or the scene
    /// the planner works against diverges from what the user sees. Reversing a Destroy
    /// is not enough.
    /// </remarks>
    public struct ObstacleSnapshot
    {
        public PrimitiveType PrimitiveType;
        public string ObjectId;
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Material Material;

        public static ObstacleSnapshot Capture(GameObject obstacle, PrimitiveType primitiveType)
        {
            var env = obstacle.GetComponent<EnvironmentObject>();
            var renderer = obstacle.GetComponent<MeshRenderer>();

            return new ObstacleSnapshot
            {
                PrimitiveType = primitiveType,
                ObjectId = env != null ? env.Id : null,
                Name = obstacle.name,
                Position = obstacle.transform.position,
                Rotation = obstacle.transform.rotation,
                Scale = obstacle.transform.localScale,
                Material = renderer != null ? renderer.sharedMaterial : null
            };
        }
    }
}
