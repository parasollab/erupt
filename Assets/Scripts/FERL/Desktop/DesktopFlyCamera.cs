using Unity.XR.CoreUtils;
using UnityEngine;

// Fly the XR rig with the mouse and keyboard when there is no headset. Moves the XR Origin
// root rather than the camera, because the camera's TrackedPoseDriver owns the camera's
// local pose.
public class DesktopFlyCamera : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 1.5f;
    [SerializeField] private float fastMultiplier = 3f;
    [SerializeField] private float lookSensitivity = 0.15f;
    [Tooltip("Where the rig starts in desktop mode (world metres); the robot is at the FR3 root.")]
    [SerializeField] private Vector3 startPosition = new Vector3(0f, 0.2f, -0.9f);
    [SerializeField] private float startYawDegrees = 0f;
    [SerializeField] private float startPitchDegrees = 15f;

    private Transform rig;
    private float yaw;
    private float pitch;

    private void OnEnable()
    {
        rig = ResolveRig();
        if (rig == null)
            return;
        rig.position = startPosition;
        yaw = startYawDegrees;
        pitch = startPitchDegrees;
        rig.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private static Transform ResolveRig()
    {
        XROrigin origin = PersistentXRInfrastructure.ResolveXROrigin();
        if (origin != null)
            return origin.transform;
        Camera camera = Camera.main;
        return camera != null ? camera.transform.root : null;
    }

    private void Update()
    {
        if (rig == null)
        {
            rig = ResolveRig();
            if (rig == null)
                return;
        }

        if (Input.GetMouseButton(1))
        {
            yaw += Input.GetAxis("Mouse X") * lookSensitivity * 10f;
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * lookSensitivity * 10f, -80f, 80f);
            rig.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        Vector3 direction = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) direction += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) direction += Vector3.back;
        if (Input.GetKey(KeyCode.A)) direction += Vector3.left;
        if (Input.GetKey(KeyCode.D)) direction += Vector3.right;
        if (Input.GetKey(KeyCode.E)) direction += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) direction += Vector3.down;
        if (direction == Vector3.zero)
            return;
        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);
        rig.position += rig.rotation * direction.normalized * speed * Time.deltaTime;
    }
}
