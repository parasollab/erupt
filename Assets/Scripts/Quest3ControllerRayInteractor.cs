using UnityEngine;
using UnityEngine.XR;

[RequireComponent(typeof(LineRenderer))]
public class Quest3ControllerRayInteractor : MonoBehaviour
{
    [SerializeField] private XRNode controllerNode = XRNode.RightHand;
    [SerializeField] private RobotInteractionController robotInteraction;
    [SerializeField] private float rayLength = 6f;
    [SerializeField] private float jointJogRadiansPerSecond = 0.8f;
    [SerializeField] private float handlePushPullMetersPerSecond = 1.5f;
    [SerializeField] private float thumbstickDeadzone = 0.18f;
    [SerializeField] private LayerMask raycastLayers = ~0;
    [SerializeField] private Vector3 localRayDirection = Vector3.forward;
    [SerializeField] private Color idleRayColor = new Color(0.05f, 0.75f, 1f, 0.85f);
    [SerializeField] private Color hitRayColor = new Color(1f, 0.75f, 0.05f, 0.95f);

    private InputDevice device;
    private LineRenderer lineRenderer;
    private bool wasTriggerPressed;
    private bool wasGripPressed;
    private bool isDraggingHandle;

    private RaycastHit currentHit;
    private bool hasCurrentHit;

    public bool TryGetCurrentHit(out RaycastHit hit)
    {
        hit = currentHit;
        return hasCurrentHit;
    }

    public void Configure(XRNode node, RobotInteractionController interactionController)
    {
        controllerNode = node;
        robotInteraction = interactionController;
        RefreshDevice();
    }

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        ConfigureLineRenderer();
    }

    private void OnEnable()
    {
        if (!ShouldRunForCurrentPlatform())
        {
            if (lineRenderer != null)
                lineRenderer.enabled = false;

            enabled = false;
            return;
        }

        RefreshDevice();
    }

    private void Update()
    {
        if (!ShouldRunForCurrentPlatform())
            return;

        if (!device.isValid)
        {
            RefreshDevice();
        }

        Ray ray = GetRay();
        bool hasHit = Physics.Raycast(
            ray,
            out RaycastHit hit,
            rayLength,
            raycastLayers,
            QueryTriggerInteraction.Collide);

        hasCurrentHit = hasHit;
        if (hasHit) currentHit = hit;

        UpdateLine(ray, hasHit ? hit.distance : rayLength, hasHit);

        bool triggerPressed = ReadButton(CommonUsages.triggerButton);
        bool gripPressed = ReadButton(CommonUsages.gripButton);

        if (triggerPressed && !wasTriggerPressed && hasHit && robotInteraction != null)
        {
            robotInteraction.SelectFromHit(hit);
        }

        if (gripPressed && !wasGripPressed && hasHit && robotInteraction != null)
        {
            isDraggingHandle = robotInteraction.TryBeginHandleDrag(this, ray, hit);
        }

        if (isDraggingHandle && gripPressed && robotInteraction != null)
        {
            if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 pushPullAxis))
            {
                float pushPullY = Mathf.Abs(pushPullAxis.y) > thumbstickDeadzone ? pushPullAxis.y : 0f;
                if (pushPullY != 0f)
                {
                    robotInteraction.AdjustHandleDragDistance(this, pushPullY * handlePushPullMetersPerSecond * Time.deltaTime, rayLength);
                }
            }

            robotInteraction.UpdateHandleDrag(this, ray);
        }

        if (isDraggingHandle && !gripPressed && wasGripPressed && robotInteraction != null)
        {
            robotInteraction.EndHandleDrag(this);
            isDraggingHandle = false;
        }

        if (!isDraggingHandle && robotInteraction != null && device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 thumbstick))
        {
            float y = Mathf.Abs(thumbstick.y) > thumbstickDeadzone ? thumbstick.y : 0f;
            robotInteraction.JogSelectedJoint(y * jointJogRadiansPerSecond * Time.deltaTime);
        }

        wasTriggerPressed = triggerPressed;
        wasGripPressed = gripPressed;
    }

    private Ray GetRay()
    {
        Vector3 localDirection = localRayDirection.sqrMagnitude > 0.0001f
            ? localRayDirection.normalized
            : Vector3.forward;
        return new Ray(transform.position, transform.TransformDirection(localDirection));
    }

    private bool ReadButton(InputFeatureUsage<bool> usage)
    {
        return device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    private void RefreshDevice()
    {
        device = InputDevices.GetDeviceAtXRNode(controllerNode);
    }

    private void ConfigureLineRenderer()
    {
        if (lineRenderer == null)
        {
            return;
        }

        lineRenderer.useWorldSpace = true;
        lineRenderer.positionCount = 2;
        lineRenderer.startWidth = 0.01f;
        lineRenderer.endWidth = 0.0025f;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            lineRenderer.material = new Material(shader);
        }
        lineRenderer.startColor = idleRayColor;
        lineRenderer.endColor = idleRayColor;
    }

    private static bool ShouldRunForCurrentPlatform()
    {
#if ERUPT_USE_META_XR && UNITY_ANDROID && !UNITY_VISIONOS
        return true;
#else
        return false;
#endif
    }

    private void UpdateLine(Ray ray, float distance, bool hasHit)
    {
        if (lineRenderer == null)
        {
            return;
        }

        Color color = hasHit ? hitRayColor : idleRayColor;
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.SetPosition(0, ray.origin);
        lineRenderer.SetPosition(1, ray.GetPoint(distance));
    }
}
