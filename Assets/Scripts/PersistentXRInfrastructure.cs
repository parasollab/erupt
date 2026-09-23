using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Attachment;

/// <summary>
/// Owns the XR/platform objects that must survive study-content changes. The generated
/// bootstrap scene contains one copy of each root. Duplicate copies in legacy scenes are
/// disabled from sceneLoaded before the next rendered frame, then destroyed.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class PersistentXRInfrastructure : MonoBehaviour
{
    private static readonly HashSet<string> PersistentRootNames = new HashSet<string>
    {
        "XR Origin (XR Rig)",
        "XR Interaction Manager",
        "EventSystem",
        "PanelInputConfiguration",
        "XR UI Toolkit Manager",
        "OVRManager",
    };

    /// <summary>Root of the Meta building-block camera rig the AR demo scenes track through.</summary>
    public const string MetaCameraRigName = "[BuildingBlock] Camera Rig";

    /// <summary>Name of the passive XROrigin created under the Meta rig by <see cref="CreateMetaRigXROrigin"/>.</summary>
    public const string MetaRigXROriginName = "XR Origin (Meta Rig)";

    private static PersistentXRInfrastructure s_Instance;
    private readonly Dictionary<string, GameObject> _roots = new Dictionary<string, GameObject>();
    private XROrigin _xrOrigin;
    private WristMenuController _wristMenu;
    private Quest3ControllerRayInteractor _leftRobotRay;
    private Quest3ControllerRayInteractor _rightRobotRay;

    public static XROrigin XROrigin => s_Instance != null ? s_Instance._xrOrigin : null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (s_Instance != null)
        {
            return;
        }

        GameObject host = new GameObject(nameof(PersistentXRInfrastructure));
        DontDestroyOnLoad(host);
        s_Instance = host.AddComponent<PersistentXRInfrastructure>();
    }

    public static bool IsPersistentRootName(string rootName)
    {
        return !string.IsNullOrEmpty(rootName) && PersistentRootNames.Contains(rootName);
    }

    public static XROrigin ResolveXROrigin(XROrigin fallback = null)
    {
        if (s_Instance != null && s_Instance._xrOrigin != null)
        {
            return s_Instance._xrOrigin;
        }

        if (fallback != null)
        {
            return fallback;
        }

        return FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
    }

    public static Quest3ControllerRayInteractor ResolveRobotRay(XRNode node)
    {
        if (s_Instance == null)
            return null;

        s_Instance.CachePersistentComponents();
        s_Instance.EnsureControllerRobotRays();
        return node == XRNode.LeftHand ? s_Instance._leftRobotRay : s_Instance._rightRobotRay;
    }

    private void Awake()
    {
        if (s_Instance != null && s_Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Update()
    {
        // Drains the paced /collision_object outbox even in scenes with no publishers of
        // their own (interludes), so teardown REMOVEs queued by a retired scene still flow.
        CollisionObjectPublisher.PumpOutbox();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null || !IsPersistentRootName(root.name))
            {
                continue;
            }

            if (!_roots.TryGetValue(root.name, out GameObject persistentRoot) || persistentRoot == null)
            {
                _roots[root.name] = root;
                DontDestroyOnLoad(root);

                XROrigin origin = root.GetComponentInChildren<XROrigin>(true);
                if (origin != null)
                {
                    _xrOrigin = origin;
                }

                continue;
            }

            if (root != persistentRoot)
            {
                // Deactivation is immediate, so duplicate cameras, event systems, and input
                // managers never participate in the next rendered frame.
                root.SetActive(false);
                Destroy(root);
            }
        }

        if (_xrOrigin == null)
        {
            _xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        }

        EnsureActiveXROrigin(scene);
        CachePersistentComponents();
        EnsureControllerRobotRays();
        BindSceneDependencies(scene);
    }

    /// <summary>
    /// Gives a scene that tracks through the Meta building-block rig an active XROrigin. XRI's
    /// InteractionAttachController (the Near-Far Interactor's far-grab anchor) skips its whole
    /// per-frame update when it cannot find one, so a far grab selects the object but never
    /// moves it. The AR demo scenes keep the XRI rig, and with it the only XROrigin, inactive.
    /// </summary>
    private void EnsureActiveXROrigin(Scene scene)
    {
        // FindAnyObjectByType skips inactive objects, so the disabled XRI rig does not count.
        if (FindAnyObjectByType<XROrigin>() != null)
            return;

        GameObject rig = FindGameObjectInScene(scene, MetaCameraRigName);
        if (rig == null || !rig.activeInHierarchy)
            return;

        XROrigin origin = CreateMetaRigXROrigin(rig);
        if (origin == null)
            return;

        Debug.Log($"PersistentXRInfrastructure: scene '{scene.name}' had no active XROrigin; added " +
                  $"'{MetaRigXROriginName}' under '{rig.name}' so XRI far grabs can move objects.");

        // Attach controllers looked the origin up in their OnEnable during scene load and cached
        // the miss; OnEnable is the only place they look again, so cycle them once.
        List<InteractionAttachController> attachControllers = FindAllInScene<InteractionAttachController>(scene);
        for (int i = 0; i < attachControllers.Count; i++)
        {
            InteractionAttachController attach = attachControllers[i];
            if (!attach.isActiveAndEnabled)
                continue;

            attach.enabled = false;
            attach.enabled = true;
        }
    }

    /// <summary>
    /// Creates a passive XROrigin under the Meta camera rig: origin = the rig root, camera = its
    /// CenterEyeAnchor, tracking mode left as-is, and the floor-offset object is the new empty
    /// child so nothing XROrigin might shift is part of the rig. Also used by the ARMTC scene
    /// builder at edit time; nothing here needs play mode.
    /// </summary>
    public static XROrigin CreateMetaRigXROrigin(GameObject rig)
    {
        if (rig == null)
            return null;

        Transform centerEye = FindDescendantByName(rig.transform, "CenterEyeAnchor");
        Camera camera = centerEye != null ? centerEye.GetComponent<Camera>() : null;
        if (camera == null)
            camera = rig.GetComponentInChildren<Camera>(true);

        // Inactive while configuring so XROrigin.Awake (play mode) sees the final fields.
        GameObject go = new GameObject(MetaRigXROriginName);
        go.SetActive(false);
        go.transform.SetParent(rig.transform, false);

        XROrigin origin = go.AddComponent<XROrigin>();
        origin.Origin = rig;
        origin.CameraFloorOffsetObject = go;
        origin.Camera = camera;
        origin.CameraYOffset = 0f;
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.NotSpecified;

        go.SetActive(true);
        return origin;
    }

    private void CachePersistentComponents()
    {
        if (_xrOrigin == null)
            return;

        if (_wristMenu == null)
            _wristMenu = _xrOrigin.GetComponentInChildren<WristMenuController>(true);

        Quest3ControllerRayInteractor[] rays =
            _xrOrigin.GetComponentsInChildren<Quest3ControllerRayInteractor>(true);
        for (int i = 0; i < rays.Length; i++)
        {
            Quest3ControllerRayInteractor ray = rays[i];
            if (ray.ControllerNode == XRNode.LeftHand)
                _leftRobotRay = ray;
            else if (ray.ControllerNode == XRNode.RightHand)
                _rightRobotRay = ray;
        }
    }

    private void EnsureControllerRobotRays()
    {
        _leftRobotRay = EnsureRobotRay(_leftRobotRay, XRNode.LeftHand, "Left Controller", "LeftHandAnchor");
        _rightRobotRay = EnsureRobotRay(_rightRobotRay, XRNode.RightHand, "Right Controller", "RightHandAnchor");
    }

    /// <summary>
    /// Keeps one robot ray per hand under a controller transform that is actually tracked. Study
    /// scenes track through the XRI rig's "Left/Right Controller"; the AR demo scenes keep that rig
    /// inactive and track through the Meta building-block camera rig's "Left/RightHandAnchor"
    /// instead, so a ray parented under the inactive rig never updates. A ray that already exists
    /// under an active parent is left alone; one under an inactive parent is rebuilt.
    /// </summary>
    private Quest3ControllerRayInteractor EnsureRobotRay(
        Quest3ControllerRayInteractor existing,
        XRNode node,
        string xriControllerName,
        string ovrAnchorName)
    {
        if (existing != null && existing.transform.parent != null
            && existing.transform.parent.gameObject.activeInHierarchy)
        {
            return existing;
        }

        Transform controller = FindTrackedControllerTransform(xriControllerName, ovrAnchorName);
        if (controller == null)
        {
            // Same as before: silent when there is no XR origin at all, an error when the rig
            // exists but has no controller of that name.
            if (existing == null && _xrOrigin != null)
                Debug.LogError($"PersistentXRInfrastructure: could not find {node} controller transform.");
            return existing;
        }

        if (existing != null)
        {
            if (existing.transform.parent == controller)
                return existing;
            Destroy(existing.gameObject);
        }

        return CreateRobotRay(controller, node);
    }

    /// <summary>
    /// Prefers the XRI rig's controller when it is active, then any active hand anchor in the
    /// loaded scenes (OVR camera rig, or an XRI controller outside the persistent rig), and finally
    /// falls back to the inactive XRI controller so behaviour matches the previous lookup.
    /// </summary>
    private Transform FindTrackedControllerTransform(string xriControllerName, string ovrAnchorName)
    {
        Transform xriController = _xrOrigin != null
            ? FindDescendantByName(_xrOrigin.transform, xriControllerName)
            : null;
        if (xriController != null && xriController.gameObject.activeInHierarchy)
            return xriController;

        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
                continue;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (!roots[i].activeInHierarchy)
                    continue;

                Transform anchor = FindDescendantByName(roots[i].transform, ovrAnchorName);
                if (anchor == null)
                    anchor = FindDescendantByName(roots[i].transform, xriControllerName);
                if (anchor != null && anchor.gameObject.activeInHierarchy)
                {
                    Debug.Log($"PersistentXRInfrastructure: robot ray for '{xriControllerName}' " +
                              $"attached to active '{anchor.name}' in scene '{scene.name}'.");
                    return anchor;
                }
            }
        }

        return xriController;
    }

    private static Quest3ControllerRayInteractor CreateRobotRay(Transform controller, XRNode node)
    {
        if (controller == null)
        {
            Debug.LogError($"PersistentXRInfrastructure: could not find {node} controller transform.");
            return null;
        }

        GameObject rayObject = new GameObject("Robot Ray");
        rayObject.layer = controller.gameObject.layer;
        rayObject.transform.SetParent(controller, false);
        rayObject.AddComponent<LineRenderer>();
        Quest3ControllerRayInteractor ray = rayObject.AddComponent<Quest3ControllerRayInteractor>();
        ray.Configure(node, null);
        return ray;
    }

    private void BindSceneDependencies(Scene scene)
    {
        SelectionManager selectionManager = FindFirstInScene<SelectionManager>(scene);
        Quest3RobotInteractionController robotInteraction =
            FindFirstInScene<Quest3RobotInteractionController>(scene);
        CollisionObjectsListenerSimple collisionObjectsListener =
            FindComponentInSceneByGameObjectName<CollisionObjectsListenerSimple>(
                scene,
                "CollisionObjectListener");
        if (collisionObjectsListener == null)
            collisionObjectsListener = FindFirstInScene<CollisionObjectsListenerSimple>(scene);

        GameObject worldOrigin = collisionObjectsListener != null && collisionObjectsListener.worldOrigin != null
            ? collisionObjectsListener.worldOrigin
            : FindGameObjectInScene(scene, "BaseTransform");

        if (selectionManager != null)
            selectionManager.BindRayInteractor(_rightRobotRay);

        BindRobotRay(_leftRobotRay, robotInteraction);
        BindRobotRay(_rightRobotRay, robotInteraction);

        bool showRobotRays = selectionManager != null || robotInteraction != null;
        SetRayActive(_leftRobotRay, showRobotRays);
        SetRayActive(_rightRobotRay, showRobotRays);

        if (_wristMenu != null)
            _wristMenu.BindSceneDependencies(selectionManager, collisionObjectsListener, worldOrigin);

        // The AR demo scenes keep the persistent XRI rig inactive and run a second wrist menu
        // under the Meta camera rig's LeftHandAnchor, which the binding above never reaches.
        // Bind every wrist menu the scene brought with it too, keeping whatever the scene had
        // serialized wherever this lookup came up empty.
        List<WristMenuController> sceneWristMenus = FindAllInScene<WristMenuController>(scene);
        for (int i = 0; i < sceneWristMenus.Count; i++)
        {
            WristMenuController wrist = sceneWristMenus[i];
            if (wrist == _wristMenu)
                continue;

            wrist.BindSceneDependencies(
                selectionManager != null ? selectionManager : wrist.selectionManager,
                collisionObjectsListener != null ? collisionObjectsListener : wrist.collisionObjectsListener,
                worldOrigin != null ? worldOrigin : wrist.worldOrigin);
        }

        if (robotInteraction != null && selectionManager == null)
        {
            Debug.LogWarning(
                $"PersistentXRInfrastructure: scene '{scene.name}' has robot interaction but no SelectionManager.");
        }
    }

    private static void BindRobotRay(
        Quest3ControllerRayInteractor ray,
        Quest3RobotInteractionController robotInteraction)
    {
        if (ray != null)
            ray.BindRobotInteraction(robotInteraction);
    }

    private static void SetRayActive(Quest3ControllerRayInteractor ray, bool active)
    {
        if (ray != null && ray.gameObject.activeSelf != active)
            ray.gameObject.SetActive(active);
    }

    private static T FindFirstInScene<T>(Scene scene) where T : Component
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            T component = roots[i].GetComponentInChildren<T>(true);
            if (component != null)
                return component;
        }

        return null;
    }

    private static List<T> FindAllInScene<T>(Scene scene) where T : Component
    {
        var results = new List<T>();
        if (!scene.IsValid() || !scene.isLoaded)
            return results;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
            results.AddRange(roots[i].GetComponentsInChildren<T>(true));

        return results;
    }

    private static GameObject FindGameObjectInScene(Scene scene, string objectName)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            Transform match = FindDescendantByName(roots[i].transform, objectName);
            if (match != null)
                return match.gameObject;
        }

        return null;
    }

    private static T FindComponentInSceneByGameObjectName<T>(Scene scene, string objectName)
        where T : Component
    {
        GameObject gameObject = FindGameObjectInScene(scene, objectName);
        return gameObject != null ? gameObject.GetComponent<T>() : null;
    }

    private static Transform FindDescendantByName(Transform root, string objectName)
    {
        if (root.name == objectName)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform match = FindDescendantByName(root.GetChild(i), objectName);
            if (match != null)
                return match;
        }

        return null;
    }

    private void OnDestroy()
    {
        if (s_Instance == this)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            s_Instance = null;
        }
    }
}
