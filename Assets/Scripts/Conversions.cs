using UnityEngine;
using RosMessageTypes.Geometry;

public static class RosUnityConversion
{
    public static PointMsg UnityToRosPosition(Vector3 pos)
    {
        return new PointMsg(pos.x, pos.z, pos.y); // Unity Y → ROS Z
    }

    public static QuaternionMsg UnityToRosQuaternion(Quaternion q)
    {
        // Get the Euler angles in degrees
        Vector3 euler = q.eulerAngles;
        var ax = euler.x;
        var ay = euler.z;
        var az = euler.y;

        // Make a rotation matrix from each axis
        Matrix4x4 Rx = RotX(-ax);
        Matrix4x4 Ry = RotY(-ay);
        Matrix4x4 Rz = RotZ(-az);

        // Combine the rotation matrices (note the order of multiplication)
        Matrix4x4 R = Rz * Rx * Ry;

        Quaternion converted = R.rotation;
        return new QuaternionMsg(converted.x, converted.y, converted.z, converted.w);
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
        // First convert back to a Quaternion
        Quaternion q = new Quaternion(
            (float)qMsg.x,
            (float)qMsg.y,
            (float)qMsg.z,
            (float)qMsg.w
        );

        // Get Euler angles from ROS quaternion
        Vector3 euler = q.eulerAngles;

        // Undo the axis swap done in UnityToRosQuaternion
        var ax = euler.x;
        var ay = euler.z;
        var az = euler.y;

        // Build rotation matrices
        Matrix4x4 Rx = RotX(-ax);
        Matrix4x4 Ry = RotY(-ay);
        Matrix4x4 Rz = RotZ(-az);

        Matrix4x4 R = Rz * Rx * Ry;

        Quaternion converted = R.rotation;
        return converted;
    }

    // Rotation helpers
    private static Matrix4x4 RotX(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        return new Matrix4x4(
            new Vector4(1, 0, 0, 0),
            new Vector4(0, c, s, 0),
            new Vector4(0, -s, c, 0),
            new Vector4(0, 0, 0, 1)
        );
    }

    private static Matrix4x4 RotY(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        return new Matrix4x4(
            new Vector4(c, 0, -s, 0),
            new Vector4(0, 1, 0, 0),
            new Vector4(s, 0, c, 0),
            new Vector4(0, 0, 0, 1)
        );
    }

    private static Matrix4x4 RotZ(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        return new Matrix4x4(
            new Vector4(c, s, 0, 0),
            new Vector4(-s, c, 0, 0),
            new Vector4(0, 0, 1, 0),
            new Vector4(0, 0, 0, 1)
        );
    }
}
