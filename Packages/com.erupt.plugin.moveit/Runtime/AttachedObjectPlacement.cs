using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosMessageTypes.Geometry;

/// <summary>
/// Puts a mirrored object on a robot link at the link-relative pose an
/// <c>AttachedCollisionObject</c> carries. Shared by the live attach mirror and by previews
/// that show an object riding the gripper (the MTC solution player).
/// </summary>
public static class AttachedObjectPlacement
{
    // Link frames come from the URDF importer and are FLU, but mirrored objects are laid out
    // in this project's world convention (RosUnityConversion: ROS x,y,z -> Unity x,z,y). The
    // two differ by a fixed quarter turn about Unity Y, which has to be folded into the
    // object's link-local rotation or boxes and meshes ride the gripper turned by 90 degrees.
    private static readonly Quaternion WorldConventionFromFlu = Quaternion.Euler(0f, -90f, 0f);

    /// <summary>
    /// Parent <paramref name="obj"/> under <paramref name="link"/> at <paramref name="rosPoseInLink"/>
    /// (null or an all-zero orientation = identity), keeping the object's world size.
    /// </summary>
    public static void Place(Transform obj, Transform link, PoseMsg rosPoseInLink)
    {
        Vector3 worldScale = obj.lossyScale;
        obj.SetParent(link, worldPositionStays: false);

        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;
        if (rosPoseInLink != null)
        {
            position = rosPoseInLink.position.From<FLU>();
            var q = rosPoseInLink.orientation;
            // An all-zero quaternion means "unset"; treat it as identity.
            if (q.x != 0 || q.y != 0 || q.z != 0 || q.w != 0)
                rotation = q.From<FLU>();
        }

        // Cancel any non-unit scale up the link chain so the object keeps its size and its
        // offset stays in metres.
        Vector3 linkScale = link.lossyScale;
        obj.localPosition = Divide(position, linkScale);
        obj.localRotation = rotation * WorldConventionFromFlu;
        obj.localScale = Divide(worldScale, linkScale);
    }

    static Vector3 Divide(Vector3 v, Vector3 by) => new Vector3(
        Mathf.Approximately(by.x, 0f) ? v.x : v.x / by.x,
        Mathf.Approximately(by.y, 0f) ? v.y : v.y / by.y,
        Mathf.Approximately(by.z, 0f) ? v.z : v.z / by.z);
}
