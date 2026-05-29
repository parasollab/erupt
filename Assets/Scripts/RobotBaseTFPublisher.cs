using UnityEngine;

/// <summary>
/// Watches the robot base GameObject (positioned by the ArUco tracker) and forces all
/// CollisionObjectPublishers and the SceneAnchorCollisionBridge to republish their poses
/// whenever the base moves beyond the configured thresholds. This keeps MoveIt's planning
/// scene consistent after the robot is physically repositioned.
/// </summary>
public class RobotBaseTFPublisher : MonoBehaviour
{
    [Tooltip("The GameObject positioned by the ArUco tracker (robot base).")]
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

        Debug.Log($"[RobotBaseMovement] Checking for movement... Current pos={robotBase.transform.position}, rot={robotBase.transform.rotation.eulerAngles}");

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
        Debug.Log($"[RobotBaseMovement] Republishing — {publishers.Length} CollisionObjectPublisher(s), bridge={(bridge != null ? "found" : "null")}");

        foreach (var pub in publishers)
            pub.ForceRepublish();

        bridge?.RepublishAll();
    }
}
