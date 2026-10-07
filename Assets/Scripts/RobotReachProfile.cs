using UnityEngine;

/// <summary>
/// Robot-specific settings for reachability queries, carried by the robot prefab so that
/// TagReachabilityIndicator only needs a reference to the robot GameObject. Put one on the
/// prefab root; a robot without one falls back to the indicator's own defaults.
/// </summary>
public class RobotReachProfile : MonoBehaviour
{
    [Tooltip("MoveIt planning group that /compute_ik solves for this robot.")]
    public string planningGroupName = "ur_manipulator";

    [Tooltip("IK tip link. Empty = the group's SRDF tip link (tool0 for ur_manipulator).")]
    public string ikLinkName = "";

    [Tooltip("ROS frame the query pose is expressed in. Must be a link of this robot so that no TF is needed.")]
    public string planningFrameId = "base_link";

    [Tooltip("Yaw about Unity Y applied to the robot root to obtain the Unity transform that corresponds to " +
             "planningFrameId under Conversions.cs's (x, z, y) axis swap. Used only when baseAnchor is empty and " +
             "no child named BaseTransform exists. -90 matches the URDF importer's axis mapping (study scenes' " +
             "BaseTransform convention).")]
    public float baseYawOffsetDegrees = -90f;

    [Tooltip("Optional explicit Unity transform that corresponds to planningFrameId.")]
    public Transform baseAnchor;

    [Header("Reachability map")]
    [Tooltip("Baked reachability map (.bytes written by planning_scene_utils reachability_map_generator) for this " +
             "robot's planning group, rendered by ReachabilityVolumeVisualizer. Grid is in planningFrameId axes.")]
    public TextAsset reachabilityMap;

    [Tooltip("Fallback name under Resources/ReachabilityMaps (no extension) used when reachabilityMap is empty.")]
    public string reachabilityMapResource = "";

    [Tooltip("Initial height of the reachability slice above planningFrameId, in metres.")]
    public float defaultSliceHeight = 0.3f;
}
