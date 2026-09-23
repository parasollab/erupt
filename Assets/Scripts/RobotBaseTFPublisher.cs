using UnityEngine;

/// <summary>
/// Watches the robot base GameObject (placed from the AprilTag marker) and, whenever the base
/// moves beyond the configured thresholds, makes every room-anchored CollisionObjectPublisher
/// and the SceneAnchorCollisionBridge republish their poses. MoveIt's world frame is the robot
/// base, so a base move changes the base-relative pose of everything that stays put in the
/// room; re-sending those keeps the planning scene consistent after the robot is repositioned.
///
/// Objects that ride with the robot -- anything parented under the base, such as the
/// listener-created objects MoveIt seeded under the world origin -- keep their base-relative
/// pose, so MoveIt already has it. Those are not republished; their publishers are only told
/// the new world pose is known, so the move is not echoed back as a MOVE either.
/// </summary>
public class RobotBaseTFPublisher : MonoBehaviour
{
    [Tooltip("The GameObject placed from the AprilTag marker (robot base).")]
    public GameObject robotBase;

    [Tooltip("Minimum position change (meters) that triggers a republish.")]
    public float positionThreshold = 0.01f;

    [Tooltip("Minimum rotation change (degrees) that triggers a republish.")]
    public float rotationThreshold = 1f;

    [Tooltip("Minimum seconds between republishes. Prevents continuous movement from flooding ROS.")]
    public float cooldownSeconds = 2f;

    private Vector3 _lastPosition;
    private Quaternion _lastRotation;
    private float _lastRepublishTime = float.NegativeInfinity;

    void Start()
    {
        if (robotBase == null) return;
        _lastPosition = robotBase.transform.position;
        _lastRotation = robotBase.transform.rotation;
    }

    void Update()
    {
        if (robotBase == null) return;
        if (Time.time - _lastRepublishTime < cooldownSeconds) return;

        Vector3 pos = robotBase.transform.position;
        Quaternion rot = robotBase.transform.rotation;

        if (Vector3.Distance(pos, _lastPosition) > positionThreshold ||
            Quaternion.Angle(rot, _lastRotation) > rotationThreshold)
        {
            _lastPosition = pos;
            _lastRotation = rot;
            _lastRepublishTime = Time.time;
            TriggerRepublish();
        }
    }

    void TriggerRepublish()
    {
        var publishers = FindObjectsByType<CollisionObjectPublisher>(FindObjectsSortMode.None);
        var bridge = FindFirstObjectByType<SceneAnchorCollisionBridge>();
        Transform baseRoot = robotBase.transform;

        int republished = 0;
        int ridingWithRobot = 0;
        foreach (var pub in publishers)
        {
            if (pub.transform.IsChildOf(baseRoot))
            {
                // Moved with the base: base-relative pose unchanged, MoveIt already has it.
                pub.MarkTransformAsPublished();
                ridingWithRobot++;
                continue;
            }
            pub.ForceRepublish();
            republished++;
        }

        bridge?.RepublishAll();

        Debug.Log($"[RobotBaseMovement] Base moved to pos={baseRoot.position}, rot={baseRoot.rotation.eulerAngles}: " +
                  $"republished {republished} room-anchored object(s), left {ridingWithRobot} riding with the robot, " +
                  $"bridge={(bridge != null ? "republished" : "none")}");
    }
}
