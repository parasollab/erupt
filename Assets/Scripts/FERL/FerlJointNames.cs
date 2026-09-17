using System.Collections.Generic;

// Maps between the Unity FR3 prefab's joint names (fr3_joint1..7, read from its UrdfJoint
// components by DirectArticulationIKController) and preference_rl's Franka joint names
// (joint1..7, FRANKA_ARM_JOINT_NAMES), which every /ferl/* trajectory message uses.
public static class FerlJointNames
{
    public const string UnityPrefix = "fr3_";

    public static readonly string[] RosArmNames =
    {
        "joint1", "joint2", "joint3", "joint4", "joint5", "joint6", "joint7"
    };

    public static string ToRos(string unityName)
    {
        return unityName != null && unityName.StartsWith(UnityPrefix)
            ? unityName.Substring(UnityPrefix.Length)
            : unityName;
    }

    public static string ToUnity(string rosName)
    {
        return rosName == null || rosName.StartsWith(UnityPrefix) ? rosName : UnityPrefix + rosName;
    }

    public static string[] UnityNamesForRos(IList<string> rosNames)
    {
        var result = new string[rosNames.Count];
        for (int i = 0; i < rosNames.Count; i++)
            result[i] = ToUnity(rosNames[i]);
        return result;
    }

    public static string[] UnityArmNames => UnityNamesForRos(RosArmNames);

    // The seven arm joints in ROS order as ROS-convention radians, or false when the
    // controller cannot report one of them (wrong robot prefab, chain not built yet).
    public static bool TryReadArmPositions(DirectArticulationIKController controller, out double[] positions)
    {
        positions = new double[RosArmNames.Length];
        if (controller == null)
            return false;

        for (int i = 0; i < RosArmNames.Length; i++)
        {
            if (!controller.TryGetJointAngle(ToUnity(RosArmNames[i]), out float radians))
                return false;
            positions[i] = radians;
        }
        return true;
    }
}
