using UnityEngine;
using RosMessageTypes.Geometry;

namespace Erupt.Ros
{
    /// <summary>
    /// The scene's ROS ↔ Unity convention: in a planning frame, ROS (x, y, z) is Unity (x, z, y)
    /// (Unity's Y is up, ROS's z is up; x is shared).
    /// </summary>
    /// <remarks>
    /// Swapping two axes mirrors space, so an orientation is not converted by swapping its
    /// components alone: a rotation by θ about an axis becomes a rotation by −θ about the swapped
    /// axis. For a quaternion (x, y, z, w) that is (−x, −z, −y, w), and the same formula goes
    /// both ways. (This used to go through Euler angles and three rotation matrices, which gave
    /// the same rotation but lost up to a few hundredths of a degree near a 90° pitch.)
    /// Robot links are a different matter: the URDF importer lays them out in the connector's
    /// FLU convention; see <c>AttachedObjectPlacement</c> in the MoveIt plugin for the bridge.
    /// </remarks>
    public static class RosUnityConversion
    {
        public static PointMsg UnityToRosPosition(Vector3 pos)
        {
            return new PointMsg(pos.x, pos.z, pos.y); // Unity Y → ROS Z
        }

        public static QuaternionMsg UnityToRosQuaternion(Quaternion q)
        {
            return new QuaternionMsg(-q.x, -q.z, -q.y, q.w);
        }

        // ROS → Unity Position
        public static Vector3 RosToUnityPosition(PointMsg pos)
        {
            // Undo UnityToRosPosition: (x, z, y) → (x, y, z)
            return new Vector3((float)pos.x, (float)pos.z, (float)pos.y);
        }

        // ROS → Unity Quaternion
        public static Quaternion RosToUnityQuaternion(QuaternionMsg qMsg)
        {
            // An all-zero quaternion is an unset orientation: identity.
            if (qMsg == null || (qMsg.x == 0 && qMsg.y == 0 && qMsg.z == 0 && qMsg.w == 0)) return Quaternion.identity;
            var q = new Quaternion(-(float)qMsg.x, -(float)qMsg.z, -(float)qMsg.y, (float)qMsg.w);
            // ROS senders are not always exactly normalised; Unity wants unit quaternions.
            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return magnitude > 1e-6f ? new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude) : Quaternion.identity;
        }
    }
}
