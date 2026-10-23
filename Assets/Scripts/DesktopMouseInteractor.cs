using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Mouse/keyboard stand-in for the VR controllers when a scene runs in the editor without a
/// headset (e.g. ReachabilityVR in Play mode on a Mac). Flies the camera rig and drags any object
/// the VR user could grab, so CollisionObjectPublisher sees the same transform changes and sends
/// MOVE/REMOVE to MoveIt exactly as it would for a hand grab.
///
/// Controls: right-drag look, W/A/S/D + Q/E move, scroll zoom, left-drag slides a shape on the
/// horizontal plane through the grab point, Shift + left-drag (or scroll while dragging) raises
/// or lowers it, Delete removes the selected shape. Disabled automatically in device builds and
/// whenever an XR device is active, so it is harmless to leave in the scene.
/// </summary>
[DisallowMultipleComponent]
public class DesktopMouseInteractor : MonoBehaviour
{
    [Header("Activation")]
    [Tooltip("Only run in the editor (never in a headset build).")]
    public bool editorOnly = true;
    [Tooltip("Stay inactive while an XR device is tracking (Link, simulator, headset).")]
    public bool onlyWithoutHeadset = true;

    [Header("Camera")]
    [Tooltip("Camera to fly; empty = Camera.main.")]
    public Camera targetCamera;
    [Tooltip("Move the camera's root (the OVR/XR rig) instead of the camera itself, so tracking " +
             "scripts that reset the camera's local pose do not fight the mouse.")]
    public bool moveCameraRoot = true;
    public Vector3 startPosition = new Vector3(0f, 1.45f, -0.5f);
    public Vector3 lookAt = new Vector3(0f, 0.95f, 1.0f);
    public float moveSpeed = 1.2f;
    public float lookSensitivity = 0.2f;
    public float scrollSpeed = 0.25f;

    [Header("Dragging")]
    [Tooltip("Only drag objects a VR user could grab (XRGrabInteractable). Off = any object with " +
             "a CollisionObjectPublisher, including fixed furniture.")]
    public bool requireGrabInteractable = true;
    public float maxRayDistance = 20f;
    public float verticalScrollStep = 0.05f;
    public bool selectOnClick = true;
    [Tooltip("Degrees per second the [ and ] keys turn the dragged or selected shape about the vertical axis.")]
    public float rotateDegreesPerSecond = 45f;
    [Tooltip("Told when a mouse drag puts a shape down, so Stop Recording sends the pose (found in the scene when empty).")]
    public PickPlaceTaskRecorder recorder;
    public bool showHelp = true;

    Transform _rig;
    float _yaw, _pitch;
    Vector3 _lastMouse;
    Transform _dragged;
    Plane _dragPlane;
    Vector3 _dragOffset;
    bool _dragVertical;
    CollisionObjectPublisher _draggedPublisher;

    void Start()
    {
        if (editorOnly && !Application.isEditor) { enabled = false; return; }
        if (onlyWithoutHeadset && XRSettings.isDeviceActive) { enabled = false; return; }
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null)
        {
            Debug.LogWarning("DesktopMouseInteractor: no camera found; disabled.", this);
            enabled = false;
            return;
        }
        _rig = moveCameraRoot ? targetCamera.transform.root : targetCamera.transform;
        if (recorder == null) recorder = FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);

        JumpTo(startPosition, lookAt);
        _lastMouse = Input.mousePosition;
    }

    /// <summary>Put the camera at <paramref name="cameraPosition"/> looking at <paramref name="target"/> (editor views).</summary>
    public void JumpTo(Vector3 cameraPosition, Vector3 target)
    {
        if (_rig == null) return;
        // Put the *camera* there, compensating for its offset and rotation inside the rig.
        Vector3 dir = (target - cameraPosition).normalized;
        _yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        _pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        ApplyCameraPose(cameraPosition);
    }

    /// <summary>The shape a left-drag is moving right now, or null.</summary>
    public Transform Dragged => _dragged;

    /// <summary>A left-drag started moving a collision-object shape (the desktop's "grab").</summary>
    public event System.Action<GameObject> DragStarted;
    /// <summary>A left-drag put a collision-object shape down (the desktop's "release").</summary>
    public event System.Action<GameObject> DragEnded;

    void ApplyCameraPose(Vector3 cameraWorldPosition)
    {
        Quaternion camRelRot = Quaternion.Inverse(_rig.rotation) * targetCamera.transform.rotation;
        Quaternion wanted = Quaternion.Euler(_pitch, _yaw, 0f);
        _rig.rotation = wanted * Quaternion.Inverse(camRelRot);
        Vector3 camOffset = targetCamera.transform.position - _rig.position;
        _rig.position = cameraWorldPosition - camOffset;
    }

    void Update()
    {
        Vector3 mouse = Input.mousePosition;
        Vector3 mouseDelta = mouse - _lastMouse;
        _lastMouse = mouse;
        Transform cam = targetCamera.transform;

        // ---- camera -------------------------------------------------------------------------
        if (Input.GetMouseButton(1))
        {
            _yaw += mouseDelta.x * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - mouseDelta.y * lookSensitivity, -89f, 89f);
        }
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += cam.forward;
        if (Input.GetKey(KeyCode.S)) move -= cam.forward;
        if (Input.GetKey(KeyCode.D)) move += cam.right;
        if (Input.GetKey(KeyCode.A)) move -= cam.right;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
        float scroll = Input.mouseScrollDelta.y;
        if (_dragged == null && Mathf.Abs(scroll) > 0f)
            move += cam.forward * scroll * scrollSpeed / Mathf.Max(Time.deltaTime, 1e-4f);
        Vector3 camPos = cam.position + move * moveSpeed * Time.deltaTime;
        ApplyCameraPose(camPos);

        // ---- dragging -----------------------------------------------------------------------
        if (Input.GetMouseButtonDown(0))
            BeginDrag();
        if (_dragged != null)
        {
            if (Input.GetMouseButton(0))
                UpdateDrag(scroll);
            else
                EndDrag();
        }
        if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
        {
            if (_dragged != null) EndDrag();
            SelectionManager.Instance?.DeleteSelectedObject();
        }

        // ---- turning: [ and ] yaw the dragged (else the selected) shape ---------------------
        float turn = (Input.GetKey(KeyCode.RightBracket) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftBracket) ? 1f : 0f);
        if (turn != 0f)
        {
            Transform shape = _dragged != null ? _dragged : SelectionManager.Instance?.SelectedObject?.transform;
            var publisher = shape != null ? shape.GetComponentInParent<CollisionObjectPublisher>() : null;
            if (shape != null && publisher != null && !publisher.attachedToRobot && IsKinematic(shape))
            {
                shape.Rotate(Vector3.up, turn * rotateDegreesPerSecond * Time.deltaTime, Space.World);
                _turned = publisher;
            }
        }
        else if (_turned != null)
        {
            if (_dragged == null) _turned.ForceRepublish();
            if (recorder != null && _dragged == null) recorder.NotifyObjectReleased(_turned.gameObject);
            _turned = null;
        }
    }

    CollisionObjectPublisher _turned;

    // A part the robot has just dropped is a live physics body until it settles; leave it alone.
    static bool IsKinematic(Transform t)
    {
        var rb = t.GetComponentInParent<Rigidbody>();
        return rb == null || rb.isKinematic;
    }

    void BeginDrag()
    {
        Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            if (selectOnClick) SelectionManager.Instance?.ClearSelection();
            return;
        }
        Transform target = null;
        // Floor handles (robot placement) have no publisher; they slide on the floor only.
        var handle = hit.collider.GetComponentInParent<DesktopDragHandle>();
        if (handle != null)
        {
            _dragged = handle.transform;
            _draggedPublisher = null;
            _dragVertical = false;
            _dragPlane = new Plane(Vector3.up, hit.point);
            _dragOffset = _dragged.position - hit.point;
            return;
        }
        var publisher = hit.collider.GetComponentInParent<CollisionObjectPublisher>();
        if (publisher != null && !publisher.attachedToRobot && IsKinematic(publisher.transform))
        {
            if (!requireGrabInteractable || hit.collider.GetComponentInParent<XRGrabInteractable>() != null)
                target = publisher.transform;
        }
        if (target == null)
        {
            if (selectOnClick) SelectionManager.Instance?.ClearSelection();
            return;
        }
        _dragged = target;
        _draggedPublisher = publisher;
        _dragVertical = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        Vector3 planeNormal = _dragVertical
            ? -Vector3.ProjectOnPlane(targetCamera.transform.forward, Vector3.up).normalized
            : Vector3.up;
        if (planeNormal.sqrMagnitude < 1e-6f) planeNormal = Vector3.up;
        _dragPlane = new Plane(planeNormal, hit.point);
        _dragOffset = target.position - hit.point;
        if (selectOnClick) SelectionManager.Instance?.SetSelectedObject(target.gameObject);
        DragStarted?.Invoke(publisher.gameObject);
    }

    void UpdateDrag(float scroll)
    {
        Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);
        if (!_dragPlane.Raycast(ray, out float enter)) return;
        Vector3 p = ray.GetPoint(enter) + _dragOffset;
        Vector3 pos = _dragged.position;
        if (_dragVertical)
            pos.y = p.y;
        else
        {
            pos.x = p.x;
            pos.z = p.z;
        }
        pos.y += scroll * verticalScrollStep;
        _dragged.position = pos;
    }

    void EndDrag()
    {
        // Publish the final pose right away instead of waiting for the next rate-limited tick.
        if (_draggedPublisher != null) _draggedPublisher.ForceRepublish();
        // A mouse drag ending is the desktop's "hand let go": the recorder takes the pose from here.
        if (_draggedPublisher != null && recorder != null) recorder.NotifyObjectReleased(_draggedPublisher.gameObject);
        GameObject released = _draggedPublisher != null ? _draggedPublisher.gameObject : null;
        _dragged = null;
        _draggedPublisher = null;
        if (released != null) DragEnded?.Invoke(released);
    }

    void OnGUI()
    {
        if (!showHelp) return;
        const string help = "Mouse: right-drag look | WASD/QE move | scroll zoom | left-drag move shape | " +
                            "Shift+drag or scroll-while-dragging raise/lower | [ ] turn shape | Delete removes selected";
        GUI.Label(new Rect(10, Screen.height - 26, Screen.width - 20, 22), help);
    }
}
