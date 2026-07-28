using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
using UnityEngine.XR.VisionOS;
using UnityEngine.XR.VisionOS.InputDevices;
#endif

/// <summary>
/// Adds scene-wide spatial pointer interaction targets for visionOS without editing every scene.
/// </summary>
[DisallowMultipleComponent]
public sealed class VisionOSInteractionBootstrap : MonoBehaviour
{
    const string BootstrapName = "VisionOS Interaction Bootstrap";
    const string ProxyName = "__VisionOSInputProxy";
    const string PolySpatialLayerName = "PolySpatial";

    static VisionOSInteractionBootstrap s_Instance;

    [SerializeField]
    bool m_EnableMouseFallback = true;

    [SerializeField]
    bool m_CreateProxyColliders = true;

    [SerializeField]
    float m_MinimumProxySize = 0.03f;

    [SerializeField]
    float m_MaximumTargetExtent = 10f;

    readonly Dictionary<GameObject, GameObject> m_ProxyToTarget = new();

#if POLYSPATIAL_1_1_OR_NEWER
    InputAction m_PolySpatialPrimaryPointer;
    InputAction m_PolySpatialSecondaryPointer;
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
    InputAction m_VisionOSPrimaryPointer;
    InputAction m_VisionOSSecondaryPointer;
#endif

    Camera m_Camera;
    int m_PolySpatialLayer = -1;
    int m_InteractionMask = ~0;
    RobotInteractionController m_ActiveRobotInteraction;
    Transform m_SelectedTransform;
    Rigidbody m_SelectedRigidbody;
    Vector3 m_GrabOffset;
    float m_GrabDistance;
    bool m_IsDragging;
    bool m_IsDraggingRobot;
    bool m_IsJoggingJoint;
    float m_LastJogPointerY;
    RobotInteractionController m_JogRobotInteraction;

    [SerializeField]
    float m_JointJogRadiansPerMeter = 2f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (!ShouldInstallForCurrentPlatform() || s_Instance != null)
            return;

        var bootstrap = new GameObject(BootstrapName);
        DontDestroyOnLoad(bootstrap);
        s_Instance = bootstrap.AddComponent<VisionOSInteractionBootstrap>();
    }

    static bool ShouldInstallForCurrentPlatform()
    {
#if UNITY_EDITOR
        return EditorUserBuildSettings.activeBuildTarget.ToString() == "VisionOS";
#elif UNITY_VISIONOS
        return true;
#else
        return false;
#endif
    }

    void Awake()
    {
        if (s_Instance != null && s_Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_Instance = this;
        m_PolySpatialLayer = LayerMask.NameToLayer(PolySpatialLayerName);
        if (m_PolySpatialLayer >= 0)
            m_InteractionMask = 1 << m_PolySpatialLayer;

        CreatePointerActions();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    IEnumerator Start()
    {
        yield return null;
        RefreshSceneTargets();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        DisposePointerActions();

        if (s_Instance == this)
            s_Instance = null;
    }

    void Update()
    {
        if (m_EnableMouseFallback)
            UpdateMouseFallback();
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StopAllCoroutines();
        StartCoroutine(RefreshAfterSceneLoad());
    }

    IEnumerator RefreshAfterSceneLoad()
    {
        yield return null;
        RefreshSceneTargets();
    }

    void CreatePointerActions()
    {
#if POLYSPATIAL_1_1_OR_NEWER
        m_PolySpatialPrimaryPointer = CreatePointerAction(
            "PolySpatial Primary Spatial Pointer",
            "<SpatialPointerDevice>/spatialPointer0",
            OnPolySpatialPointer);
        m_PolySpatialSecondaryPointer = CreatePointerAction(
            "PolySpatial Secondary Spatial Pointer",
            "<SpatialPointerDevice>/spatialPointer1",
            OnPolySpatialPointer);
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
        m_VisionOSPrimaryPointer = CreatePointerAction(
            "visionOS Primary Spatial Pointer",
            "<VisionOSSpatialPointerDevice>/spatialPointer0",
            OnVisionOSPointer);
        m_VisionOSSecondaryPointer = CreatePointerAction(
            "visionOS Secondary Spatial Pointer",
            "<VisionOSSpatialPointerDevice>/spatialPointer1",
            OnVisionOSPointer);
#endif
    }

    static InputAction CreatePointerAction(string actionName, string bindingPath, System.Action<InputAction.CallbackContext> callback)
    {
        var action = new InputAction(actionName, InputActionType.PassThrough);
        action.AddBinding(bindingPath);
        action.performed += callback;
        action.canceled += callback;
        action.Enable();
        return action;
    }

    void DisposePointerActions()
    {
#if POLYSPATIAL_1_1_OR_NEWER
        DisposePointerAction(m_PolySpatialPrimaryPointer, OnPolySpatialPointer);
        DisposePointerAction(m_PolySpatialSecondaryPointer, OnPolySpatialPointer);
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
        DisposePointerAction(m_VisionOSPrimaryPointer, OnVisionOSPointer);
        DisposePointerAction(m_VisionOSSecondaryPointer, OnVisionOSPointer);
#endif
    }

    static void DisposePointerAction(InputAction action, System.Action<InputAction.CallbackContext> callback)
    {
        if (action == null)
            return;

        action.performed -= callback;
        action.canceled -= callback;
        action.Disable();
        action.Dispose();
    }

    void RefreshSceneTargets()
    {
        EnsureMainCamera();
        m_ProxyToTarget.Clear();

        if (!m_CreateProxyColliders)
            return;

        var targetBounds = new Dictionary<GameObject, Bounds>();
        var renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var renderer in renderers)
        {
            if (renderer == null || !renderer.enabled)
                continue;

            var target = ResolveMovableTarget(renderer.gameObject);
            if (target == null || IsInfrastructureObject(target))
                continue;

            if (targetBounds.TryGetValue(target, out var bounds))
            {
                bounds.Encapsulate(renderer.bounds);
                targetBounds[target] = bounds;
            }
            else
            {
                targetBounds[target] = renderer.bounds;
            }
        }

        foreach (var pair in targetBounds)
        {
            if (ShouldCreateProxyFor(pair.Key, pair.Value))
                CreateProxyCollider(pair.Key, pair.Value);
        }
    }

    static GameObject ResolveMovableTarget(GameObject source)
    {
        if (source == null)
            return null;

        var selectable = FindTaggedParent(source.transform, "Selectable");
        if (selectable != null && !IsInfrastructureObject(selectable.gameObject))
            return selectable.gameObject;

        var rigidbody = source.GetComponentInParent<Rigidbody>();
        if (rigidbody != null)
            return rigidbody.gameObject;

        var collider = source.GetComponentInParent<Collider>();
        if (collider != null && !IsInfrastructureObject(collider.gameObject))
            return collider.gameObject;

        return source;
    }

    static Transform FindTaggedParent(Transform source, string tagName)
    {
        var current = source;
        while (current != null)
        {
            if (current.CompareTag(tagName))
                return current;

            current = current.parent;
        }

        return null;
    }

    bool ShouldCreateProxyFor(GameObject target, Bounds worldBounds)
    {
        if (target == null || IsInfrastructureObject(target))
            return false;

        if (target.GetComponentInParent<Canvas>() != null)
            return false;

        if (worldBounds.size.x > m_MaximumTargetExtent ||
            worldBounds.size.y > m_MaximumTargetExtent ||
            worldBounds.size.z > m_MaximumTargetExtent)
            return false;

        return true;
    }

    void CreateProxyCollider(GameObject target, Bounds worldBounds)
    {
        var existingProxy = target.transform.Find(ProxyName);
        if (existingProxy != null)
        {
            if (existingProxy.TryGetComponent<BoxCollider>(out var existingBox))
            {
                existingBox.center = target.transform.InverseTransformPoint(worldBounds.center);
                existingBox.size = CalculateLocalBoundsSize(target.transform, worldBounds);
            }
            m_ProxyToTarget[existingProxy.gameObject] = target;
            return;
        }

        var proxy = new GameObject(ProxyName);
        proxy.hideFlags = HideFlags.DontSave;
        proxy.layer = m_PolySpatialLayer >= 0 ? m_PolySpatialLayer : target.layer;
        proxy.transform.SetParent(target.transform, false);

        var box = proxy.AddComponent<BoxCollider>();
        box.center = target.transform.InverseTransformPoint(worldBounds.center);
        box.size = CalculateLocalBoundsSize(target.transform, worldBounds);
        box.isTrigger = false;

        m_ProxyToTarget[proxy] = target;
    }

    Vector3 CalculateLocalBoundsSize(Transform target, Bounds worldBounds)
    {
        var corners = new[]
        {
            new Vector3(worldBounds.min.x, worldBounds.min.y, worldBounds.min.z),
            new Vector3(worldBounds.min.x, worldBounds.min.y, worldBounds.max.z),
            new Vector3(worldBounds.min.x, worldBounds.max.y, worldBounds.min.z),
            new Vector3(worldBounds.min.x, worldBounds.max.y, worldBounds.max.z),
            new Vector3(worldBounds.max.x, worldBounds.min.y, worldBounds.min.z),
            new Vector3(worldBounds.max.x, worldBounds.min.y, worldBounds.max.z),
            new Vector3(worldBounds.max.x, worldBounds.max.y, worldBounds.min.z),
            new Vector3(worldBounds.max.x, worldBounds.max.y, worldBounds.max.z)
        };

        var localBounds = new Bounds(target.InverseTransformPoint(corners[0]), Vector3.zero);
        for (var i = 1; i < corners.Length; i++)
            localBounds.Encapsulate(target.InverseTransformPoint(corners[i]));

        return Vector3.Max(localBounds.size, Vector3.one * m_MinimumProxySize);
    }

#if POLYSPATIAL_1_1_OR_NEWER
    void OnPolySpatialPointer(InputAction.CallbackContext context)
    {
        var state = context.ReadValue<SpatialPointerState>();
        if (IsPolySpatialSecondaryPointer(context))
        {
            HandleJointJogPointer(state.phase, state.interactionPosition.y);
            return;
        }

        var ray = CreatePolySpatialRay(state);
        switch (state.phase)
        {
            case SpatialPointerPhase.Began:
                if (state.targetObject != null)
                    BeginDragFromTarget(state.targetObject, state.interactionPosition, ray);
                else
                    BeginDragFromRay(ray);
                break;
            case SpatialPointerPhase.Moved:
                if (m_IsDraggingRobot)
                    m_ActiveRobotInteraction?.UpdateHandleDragToWorldPoint(this, state.interactionPosition);
                else
                    ContinueDrag(state.interactionPosition);
                break;
            case SpatialPointerPhase.Ended:
            case SpatialPointerPhase.Cancelled:
            case SpatialPointerPhase.None:
                EndDrag();
                break;
        }
    }
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
    void OnVisionOSPointer(InputAction.CallbackContext context)
    {
        var state = context.ReadValue<VisionOSSpatialPointerState>();
        if (IsVisionOSSecondaryPointer(context))
        {
            HandleJointJogPointer(state.phase, state.inputDevicePosition.y);
            return;
        }

        var ray = new Ray(state.startRayOrigin, state.startRayDirection);

        switch (state.phase)
        {
            case VisionOSSpatialPointerPhase.Began:
                BeginDragFromRay(ray);
                break;
            case VisionOSSpatialPointerPhase.Moved:
                ContinueDrag(ray);
                break;
            case VisionOSSpatialPointerPhase.Ended:
            case VisionOSSpatialPointerPhase.Cancelled:
            case VisionOSSpatialPointerPhase.None:
                EndDrag();
                break;
        }
    }
#endif

#if POLYSPATIAL_1_1_OR_NEWER
    bool IsPolySpatialSecondaryPointer(InputAction.CallbackContext context)
    {
        return context.action == m_PolySpatialSecondaryPointer;
    }

    void HandleJointJogPointer(SpatialPointerPhase phase, float pointerY)
    {
        switch (phase)
        {
            case SpatialPointerPhase.Began:
                BeginJointJog(pointerY);
                break;
            case SpatialPointerPhase.Moved:
                ContinueJointJog(pointerY);
                break;
            case SpatialPointerPhase.Ended:
            case SpatialPointerPhase.Cancelled:
            case SpatialPointerPhase.None:
                EndJointJog();
                break;
        }
    }
#endif

#if UNITY_VISIONOS || UNITY_EDITOR
    bool IsVisionOSSecondaryPointer(InputAction.CallbackContext context)
    {
        return context.action == m_VisionOSSecondaryPointer;
    }

    void HandleJointJogPointer(VisionOSSpatialPointerPhase phase, float pointerY)
    {
        switch (phase)
        {
            case VisionOSSpatialPointerPhase.Began:
                BeginJointJog(pointerY);
                break;
            case VisionOSSpatialPointerPhase.Moved:
                ContinueJointJog(pointerY);
                break;
            case VisionOSSpatialPointerPhase.Ended:
            case VisionOSSpatialPointerPhase.Cancelled:
            case VisionOSSpatialPointerPhase.None:
                EndJointJog();
                break;
        }
    }
#endif

    void BeginJointJog(float pointerY)
    {
        m_JogRobotInteraction = FindAnyRobotInteraction();
        if (m_JogRobotInteraction == null)
            return;

        m_IsJoggingJoint = true;
        m_LastJogPointerY = pointerY;
    }

    void ContinueJointJog(float pointerY)
    {
        if (!m_IsJoggingJoint || m_JogRobotInteraction == null)
            return;

        float delta = pointerY - m_LastJogPointerY;
        m_LastJogPointerY = pointerY;
        m_JogRobotInteraction.JogSelectedJoint(delta * m_JointJogRadiansPerMeter);
    }

    void EndJointJog()
    {
        if (m_JogRobotInteraction != null)
            m_JogRobotInteraction.JogSelectedJoint(0f);

        m_IsJoggingJoint = false;
        m_JogRobotInteraction = null;
        m_LastJogPointerY = 0f;
    }

    void UpdateMouseFallback()
    {
        var mouse = Mouse.current;
        if (mouse == null)
            return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            var camera = GetMainCamera();
            if (camera == null)
                return;

            var ray = camera.ScreenPointToRay(mouse.position.ReadValue());
            BeginDragFromRay(ray);
        }
        else if (mouse.leftButton.isPressed && m_IsDragging)
        {
            var camera = GetMainCamera();
            if (camera == null)
                return;

            var ray = camera.ScreenPointToRay(mouse.position.ReadValue());
            ContinueDrag(ray);
        }
        else if (mouse.leftButton.wasReleasedThisFrame)
        {
            EndDrag();
        }
    }

    void BeginDragFromRay(Ray ray)
    {
        if (!Physics.Raycast(ray, out var hit, 100f, m_InteractionMask, QueryTriggerInteraction.Collide))
            return;

        if (TryBeginRobotInteraction(ray, hit))
            return;

        BeginDrag(ResolveInteractionTarget(hit.collider.gameObject), hit.point, ray);
    }

    void BeginDragFromTarget(GameObject hitObject, Vector3 interactionPoint, Ray ray)
    {
        var target = ResolveInteractionTarget(hitObject);
        if (TryBeginRobotInteraction(ray, target, interactionPoint))
            return;

        BeginDrag(target, interactionPoint, ray);
    }

    void BeginDrag(GameObject target, Vector3 interactionPoint, Ray? interactionRay)
    {
        if (target == null || IsInfrastructureObject(target))
            return;

        if (target.CompareTag("Selectable"))
            SelectionManager.Instance?.SetSelectedObject(target);

        m_SelectedTransform = target.transform;
        m_SelectedRigidbody = target.GetComponent<Rigidbody>();
        m_GrabOffset = m_SelectedTransform.position - interactionPoint;
        m_GrabDistance = interactionRay.HasValue
            ? Mathf.Max(0.1f, Vector3.Distance(interactionRay.Value.origin, interactionPoint))
            : 0f;
        m_IsDragging = true;
    }

    void ContinueDrag(Vector3 interactionPoint)
    {
        if (!m_IsDragging || m_SelectedTransform == null)
            return;

        MoveSelected(interactionPoint + m_GrabOffset);
    }

    void ContinueDrag(Ray ray)
    {
        if (!m_IsDragging || m_SelectedTransform == null)
            return;

        if (m_IsDraggingRobot)
        {
            m_ActiveRobotInteraction?.UpdateHandleDrag(this, ray);
            return;
        }

        MoveSelected(ray.GetPoint(m_GrabDistance) + m_GrabOffset);
    }

    void MoveSelected(Vector3 worldPosition)
    {
        if (m_SelectedRigidbody != null)
        {
            m_SelectedRigidbody.MovePosition(worldPosition);
            return;
        }

        m_SelectedTransform.position = worldPosition;
    }

    void EndDrag()
    {
        if (m_IsDraggingRobot)
            m_ActiveRobotInteraction?.EndHandleDrag(this);

        m_ActiveRobotInteraction = null;
        m_SelectedTransform = null;
        m_SelectedRigidbody = null;
        m_IsDragging = false;
        m_IsDraggingRobot = false;
        m_GrabDistance = 0f;
        m_GrabOffset = Vector3.zero;
    }

    bool TryBeginRobotInteraction(Ray ray, RaycastHit hit)
    {
        var robot = FindRobotInteraction(hit);
        if (robot == null)
            return false;

        if (robot.TryBeginHandleDrag(this, ray, hit))
        {
            m_ActiveRobotInteraction = robot;
            m_IsDragging = true;
            m_IsDraggingRobot = true;
            return true;
        }

        robot.SelectFromHit(hit);
        return true;
    }

    bool TryBeginRobotInteraction(Ray ray, GameObject target, Vector3 interactionPoint)
    {
        var robot = FindRobotInteraction(target);
        if (robot == null)
            return false;

        if (robot.TryBeginHandleDrag(this, ray, target, interactionPoint))
        {
            m_ActiveRobotInteraction = robot;
            m_IsDragging = true;
            m_IsDraggingRobot = true;
            return true;
        }

        robot.SelectFromTarget(target);
        return true;
    }

    static RobotInteractionController FindRobotInteraction(RaycastHit hit)
    {
        var controllers = FindObjectsByType<RobotInteractionController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var controller in controllers)
        {
            if (controller != null && controller.CanRouteHit(hit))
                return controller;
        }

        return null;
    }

    static RobotInteractionController FindRobotInteraction(GameObject target)
    {
        if (target == null)
            return null;

        var controllers = FindObjectsByType<RobotInteractionController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var controller in controllers)
        {
            if (controller != null && controller.CanRouteTarget(target))
                return controller;
        }

        return null;
    }

    static RobotInteractionController FindAnyRobotInteraction()
    {
        var controllers = FindObjectsByType<RobotInteractionController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        return controllers.Length > 0 ? controllers[0] : null;
    }

    GameObject ResolveInteractionTarget(GameObject hitObject)
    {
        if (hitObject == null)
            return null;

        if (m_ProxyToTarget.TryGetValue(hitObject, out var target) && target != null)
            return target;

        var parentProxy = hitObject.GetComponentInParent<Collider>();
        if (parentProxy != null && m_ProxyToTarget.TryGetValue(parentProxy.gameObject, out target) && target != null)
            return target;

        return ResolveMovableTarget(hitObject);
    }

    Camera GetMainCamera()
    {
        if (m_Camera == null)
            EnsureMainCamera();

        return m_Camera;
    }

    void EnsureMainCamera()
    {
        if (m_Camera != null)
            return;

        m_Camera = Camera.main;
        if (m_Camera != null)
            return;

        var cameraObject = new GameObject("Main Camera");
        cameraObject.hideFlags = HideFlags.DontSave;
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -2.2f), Quaternion.Euler(15f, 0f, 0f));

        m_Camera = cameraObject.AddComponent<Camera>();
        m_Camera.nearClipPlane = 0.03f;
        m_Camera.farClipPlane = 100f;
        m_Camera.clearFlags = CameraClearFlags.Skybox;

        if (FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length == 0)
            cameraObject.AddComponent<AudioListener>();
    }

#if POLYSPATIAL_1_1_OR_NEWER
    static Ray CreatePolySpatialRay(SpatialPointerState state)
    {
        var origin = state.inputDevicePosition;
        var direction = state.startInteractionRayDirection;
        if (direction.sqrMagnitude < 0.0001f)
            direction = state.interactionPosition - origin;
        if (direction.sqrMagnitude < 0.0001f)
            direction = Vector3.forward;

        return new Ray(origin, direction.normalized);
    }
#endif

    static bool IsInfrastructureObject(GameObject target)
    {
        if (target == null)
            return true;

        var layer = target.layer;
        if (layer == LayerMask.NameToLayer("UI") ||
            layer == LayerMask.NameToLayer("Overlay UI") ||
            layer == LayerMask.NameToLayer("Ignore Raycast"))
            return true;

        var transform = target.transform;
        while (transform != null)
        {
            var name = transform.name;
            if (name.Contains("XR Origin") ||
                name.Contains("XR Rig") ||
                name.Contains("Interaction Manager") ||
                name.Contains("EventSystem") ||
                name.Contains("Volume Camera") ||
                name.Contains("Study Controller") ||
                name.Contains("Wrist UI") ||
                name.Contains("ROS") ||
                name.Contains("Anchor_") ||
                name.Contains("Plane") ||
                name.Contains("Camera") ||
                name.Contains("Light"))
                return true;

            transform = transform.parent;
        }

        return false;
    }
}
