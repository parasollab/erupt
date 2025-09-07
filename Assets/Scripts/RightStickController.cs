using UnityEngine;
using UnityEngine.InputSystem;

public class RightStickController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Root transform that moves/rotates the rig (usually the XR Origin).")]
    public Transform xrOriginRoot;

    [Header("Input")]
    [Tooltip("Vector2 action bound to RIGHT stick/primary2DAxis.")]
    public InputActionProperty rightStick; // Value(Vector2)
    [Tooltip("Button action bound to RIGHT A (primaryButton).")]
    public InputActionProperty toggleTurnButton; // Button

    [Header("Tuning")]
    [Tooltip("Degrees per second at full stick deflection.")]
    public float turnSpeed = 120f;
    [Range(0f, 1f)] public float deadzone = 0.2f;

    [Header("State")]
    public bool turnEnabled = true;

    public SelectionManager selectionManager;

    void OnEnable()
    {
        if (rightStick.action != null) rightStick.action.Enable();

        if (toggleTurnButton.action != null)
        {
            toggleTurnButton.action.Enable();
            // Make sure the button action uses a Press interaction set to "Press only" in the Input Actions asset.
            toggleTurnButton.action.performed += OnTogglePerformed;
        }
    }

    void OnDisable()
    {
        if (toggleTurnButton.action != null)
        {
            toggleTurnButton.action.performed -= OnTogglePerformed;
            toggleTurnButton.action.Disable();
        }

        // if (rightStick.action != null) rightStick.action.Disable();
    }

    private void OnTogglePerformed(InputAction.CallbackContext ctx)
    {
        // Triggered once per press if the action uses Press(behavior=PressOnly)
        turnEnabled = !turnEnabled;
        // Debug.Log($"Smooth turn {(turnEnabled ? "ENABLED" : "DISABLED")}");
    }

    void Update()
    {
        // if (!turnEnabled || xrOriginRoot == null || rightStick == null) return;

        // Debug.Log($"Turn enabled: {turnEnabled}");
        if (!turnEnabled)
        {
            HandleObjectRotation();
        }
        else
        {
            HandleSmoothTurnAndObjectTranslation();
        }
    }

    void HandleSmoothTurnAndObjectTranslation()
    {
        if (xrOriginRoot == null || rightStick == null) return;
        Vector2 v = rightStick.action.ReadValue<Vector2>();
        float x = Mathf.Abs(v.x) < deadzone ? 0f : v.x;
        if (Mathf.Approximately(x, 0f)) return;

        float yaw = x * turnSpeed * Time.deltaTime;
        xrOriginRoot.Rotate(0f, yaw, 0f, Space.World);

        if (selectionManager == null || selectionManager.SelectedObject == null) return;

        // Get seleected object
        GameObject obj = selectionManager.SelectedObject;
        if (obj == null) return;

        // Apply push and pull based on right stick Y axis
        float z = Mathf.Abs(v.y) < deadzone ? 0f : v.y;
        if (Mathf.Approximately(z, 0f)) return;
        Vector3 forward = xrOriginRoot.forward;
        forward.y = 0f;
        forward.Normalize();
        obj.transform.position += forward * z * turnSpeed * Time.deltaTime;
    }

    void HandleObjectRotation()
    {
        if (xrOriginRoot == null || rightStick == null) return;

        if (selectionManager == null || selectionManager.SelectedObject == null) return;

        // Get right stick input
        Vector2 v = rightStick.action.ReadValue<Vector2>();

        // Get seleected object
        GameObject obj = selectionManager.SelectedObject;
        if (obj == null) return;

        // Apply rotation based on right stick X axis
        float rotationAmount = v.x * turnSpeed * Time.deltaTime;
        obj.transform.Rotate(0f, rotationAmount, 0f, Space.World);

        // Apply rotation base on right stick Y axis
        float verticalRotationAmount = v.y * turnSpeed * Time.deltaTime;
        obj.transform.Rotate(-verticalRotationAmount, 0f, 0f, Space.World);
    }
}
