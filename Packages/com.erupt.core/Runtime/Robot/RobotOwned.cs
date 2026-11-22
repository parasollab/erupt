using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// Names the robot an object belongs to when it is not parented under that robot: joint
    /// handles, ghosts, trajectory handles. <see cref="IRobotRegistry.OwnerOf"/> reads it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RobotOwned : MonoBehaviour
    {
        [SerializeField] private RobotInstance owner;

        public RobotInstance Owner
        {
            get => owner;
            set => owner = value;
        }

        /// <summary>Mark <paramref name="go"/> as belonging to <paramref name="owner"/> (replacing an earlier mark).</summary>
        public static RobotOwned Mark(GameObject go, RobotInstance owner)
        {
            if (go == null) return null;
            if (!go.TryGetComponent(out RobotOwned marker)) marker = go.AddComponent<RobotOwned>();
            marker.owner = owner;
            return marker;
        }
    }
}
