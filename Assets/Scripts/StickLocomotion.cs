using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Thumbstick locomotion for scenes that run on the Meta OVRCameraRig (where the XRI rig and its
/// locomotion providers are inactive). Put it on the rig root: the left stick moves the rig on the
/// ground plane relative to where the head is looking, the right stick snap-turns around the head.
/// Reads the same XRI Default Input Actions asset the wrist menu uses, so no extra bindings.
/// Not for passthrough scenes: moving the rig there would detach the virtual robot from the room.
/// </summary>
public class StickLocomotion : MonoBehaviour
{
    [Header("Input")]
    [SerializeField, Tooltip("XRI Default Input Actions asset (same one WristMenuController reads).")]
    private InputActionAsset inputActions;

    [SerializeField] private string moveActionMap = "XRI Left Locomotion";
    [SerializeField] private string moveActionName = "Move";
    [SerializeField] private string turnActionMap = "XRI Right Locomotion";
    [SerializeField] private string turnActionName = "Turn";

    [SerializeField, Tooltip("Ignore a hand's stick while that hand's Select is held: the robot ray uses the stick to " +
                             "push/pull a grabbed handle, and the slider/menu grabs should not walk the rig.")]
    private bool suppressWhileSelecting = true;

    [SerializeField] private string leftSelectMap = "XRI Left Interaction";
    [SerializeField] private string rightSelectMap = "XRI Right Interaction";
    [SerializeField] private string selectActionName = "Select";

    [Header("Rig")]
    [SerializeField, Tooltip("Headset transform that defines 'forward' and the turn pivot. Empty = Camera.main.")]
    private Transform head;

    [Header("Tuning")]
    [SerializeField, Tooltip("Master switch; also settable at runtime via SetLocomotionEnabled.")]
    private bool locomotionEnabled = true;

    [SerializeField, Tooltip("Metres per second at full stick deflection.")]
    private float moveSpeed = 1.5f;

    [SerializeField, Range(0f, 0.9f)] private float moveDeadzone = 0.15f;

    [SerializeField, Tooltip("Degrees per snap turn. 0 = smooth turning at turnSpeed.")]
    private float snapAngle = 45f;

    [SerializeField, Tooltip("Degrees per second when snapAngle is 0.")]
    private float turnSpeed = 90f;

    [SerializeField, Range(0.3f, 0.95f), Tooltip("Stick deflection that triggers a snap; the stick must return below half of this before the next snap.")]
    private float snapThreshold = 0.6f;

    private InputAction _move;
    private InputAction _turn;
    private InputAction _leftSelect;
    private InputAction _rightSelect;
    private bool _snapArmed = true;

    public bool LocomotionEnabled => locomotionEnabled;

    public void SetLocomotionEnabled(bool on) => locomotionEnabled = on;

    private void Awake()
    {
        if (head == null && Camera.main != null)
            head = Camera.main.transform;
        if (inputActions == null)
        {
            Debug.LogError("StickLocomotion: inputActions is not assigned; locomotion disabled.");
            return;
        }
        _move = inputActions.FindActionMap(moveActionMap)?.FindAction(moveActionName);
        _turn = inputActions.FindActionMap(turnActionMap)?.FindAction(turnActionName);
        _leftSelect = inputActions.FindActionMap(leftSelectMap)?.FindAction(selectActionName);
        _rightSelect = inputActions.FindActionMap(rightSelectMap)?.FindAction(selectActionName);
        if (_move == null)
            Debug.LogError($"StickLocomotion: action '{moveActionMap}/{moveActionName}' not found.");
        if (_turn == null)
            Debug.LogError($"StickLocomotion: action '{turnActionMap}/{turnActionName}' not found.");
    }

    private void OnEnable()
    {
        _move?.Enable();
        _turn?.Enable();
        _leftSelect?.Enable();
        _rightSelect?.Enable();
    }

    private void OnDisable()
    {
        _move?.Disable();
        _turn?.Disable();
        // Select actions are shared with the XRI interactors; leave their enabled state alone.
    }

    private static bool Held(InputAction action) => action != null && action.IsPressed();

    private void Update()
    {
        if (!locomotionEnabled || head == null)
            return;

        if (_move != null && !(suppressWhileSelecting && Held(_leftSelect)))
        {
            Vector2 stick = _move.ReadValue<Vector2>();
            if (stick.magnitude > moveDeadzone)
            {
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 1e-4f)
                    forward = Vector3.ProjectOnPlane(head.up, Vector3.up).normalized;   // looking straight down/up
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 delta = (forward * stick.y + right * stick.x) * (moveSpeed * Time.deltaTime);
                transform.position += delta;
            }
        }

        if (_turn != null && !(suppressWhileSelecting && Held(_rightSelect)))
        {
            float x = _turn.ReadValue<Vector2>().x;
            if (snapAngle > 0f)
            {
                if (_snapArmed && Mathf.Abs(x) >= snapThreshold)
                {
                    _snapArmed = false;
                    TurnAroundHead(Mathf.Sign(x) * snapAngle);
                }
                else if (Mathf.Abs(x) < snapThreshold * 0.5f)
                {
                    _snapArmed = true;
                }
            }
            else if (Mathf.Abs(x) > moveDeadzone)
            {
                TurnAroundHead(x * turnSpeed * Time.deltaTime);
            }
        }
    }

    /// <summary>Yaw the rig about the vertical axis through the head so the user stays in place.</summary>
    private void TurnAroundHead(float degrees)
    {
        Vector3 pivot = head.position;
        transform.RotateAround(pivot, Vector3.up, degrees);
    }
}
