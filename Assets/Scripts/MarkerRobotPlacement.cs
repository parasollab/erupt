using UnityEngine;
using UnityEngine.InputSystem;

public class MarkerRobotPlacement : MonoBehaviour
{
    [SerializeField] private InputActionReference placeRobotAction;
    [SerializeField] private GameObject markerIndicator;
    [SerializeField] private SceneAnchorCollisionBridge anchorBridge;

    [SerializeField] private GameObject leftRobot;
    [SerializeField] private GameObject rightRobot;

    [SerializeField, Tooltip("Keep the AprilTag tracker running after the robot is placed. Required when the tracker " +
                             "also tracks object tags (TagReachabilityIndicator). Off restores the old behaviour of " +
                             "stopping passthrough reads once the robot is placed.")]
    private bool keepTrackerRunningAfterPlacement = true;

    private AprilTagTracker _tracker;

    void Start()
    {
        _tracker = GetComponent<AprilTagTracker>();
        placeRobotAction.action.performed += OnPlaceRobot;
    }

    void OnPlaceRobot(InputAction.CallbackContext context)
    {
        if (markerIndicator == null) return;

        Vector3 markerPosition = markerIndicator.transform.position;
        Vector3 surfaceNormal;
        Vector3 placementPosition;

        // Raycast downward to find the placement surface (table, furniture, or any collider).
        // Origin is raised slightly above the marker so the ray always travels through the surface.
        if (Physics.Raycast(markerPosition + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 3f))
        {
            placementPosition = hit.point;
            // A hit normal with a small Y component means the ray clipped a side face of a
            // box collider (e.g. anchor bridge furniture). Snap to up so the robot doesn't
            // end up on its side.
            surfaceNormal = hit.normal.y >= 0.5f ? hit.normal : Vector3.up;
        }
        else if (anchorBridge != null && anchorBridge.HasFloor)
        {
            // No collider found below — fall back to the MRUK floor plane.
            surfaceNormal = anchorBridge.FloorNormal;
            placementPosition = markerPosition - Vector3.Dot(markerPosition - anchorBridge.FloorPosition, surfaceNormal) * surfaceNormal;
        }
        else
        {
            return;
        }

        Vector3 projectedRight = Vector3.ProjectOnPlane(markerIndicator.transform.right, surfaceNormal).normalized;
        // Guard against the marker's right axis being parallel to the surface normal.
        if (projectedRight.sqrMagnitude < 0.001f)
            projectedRight = Vector3.ProjectOnPlane(markerIndicator.transform.forward, surfaceNormal).normalized;

        Quaternion newRotation = Quaternion.LookRotation(projectedRight, surfaceNormal);

        bool hasLeft = leftRobot != null;
        bool hasRight = rightRobot != null;
        int robotCount = (hasLeft ? 1 : 0) + (hasRight ? 1 : 0);

        if (robotCount == 1)
        {
            GameObject robot = hasLeft ? leftRobot : rightRobot;
            MoveRobot(robot, placementPosition, newRotation);
        }
        else if (robotCount == 2)
        {
            float halfDist = Vector3.Distance(leftRobot.transform.position, rightRobot.transform.position) / 2f;
            MoveRobot(leftRobot, placementPosition - projectedRight * halfDist, newRotation);
            MoveRobot(rightRobot, placementPosition + projectedRight * halfDist, newRotation);
        }

        // Placement is done; stop reading passthrough frames unless other consumers still need tags.
        if (!keepTrackerRunningAfterPlacement && _tracker != null)
            _tracker.enabled = false;
    }

    /// <summary>
    /// Moves a robot root. URDF-imported robots are ArticulationBody chains, and PhysX ignores
    /// Transform writes on an articulation root: the pose shows for one frame and the next physics
    /// step restores the old one. TeleportRoot moves the articulation itself; the Transform is set
    /// first so the root body's world pose (and any non-articulated children) reflect the target.
    /// </summary>
    static void MoveRobot(GameObject robot, Vector3 position, Quaternion rotation)
    {
        robot.transform.SetPositionAndRotation(position, rotation);

        foreach (var body in robot.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!body.isRoot) continue;
            body.TeleportRoot(body.transform.position, body.transform.rotation);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    void OnDestroy()
    {
        if (placeRobotAction != null)
            placeRobotAction.action.performed -= OnPlaceRobot;
    }
}
