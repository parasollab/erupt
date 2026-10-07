using System.Collections.Generic;
using Erupt.Interaction;
using UnityEngine;

public class Quest3RobotInteractionController : MonoBehaviour
{
    // Fixed id -- EndEffectorHandle is a single static part of the Robot IK Manager prefab
    // present in every scene, not a spawned/duplicated object, same convention as
    // IndicatorSphereController's default "indicator_sphere" id.
    private const string EndEffectorHandleObjectId = "end_effector_handle";

    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private Transform endEffector;
    [SerializeField] private Transform handle;
    [SerializeField] private Renderer handleRenderer;
    [SerializeField] private Color handleIdleColor = new Color(0.05f, 0.75f, 1f, 1f);
    [SerializeField] private Color handleActiveColor = new Color(1f, 0.75f, 0.05f, 1f);

    [Header("Task solutions")]
    [Tooltip("The end-effector handle is hidden while a solution executes or is previewed, so it " +
             "does not block the view of the grasp. Found in the scene when left empty.")]
    [SerializeField] private PickPlaceClient pickPlaceClient;
    [SerializeField] private MTCTrajectoryPlayer trajectoryPlayer;
    private bool handleHiddenForSolution;
    [SerializeField] private Color selectedJointColor = new Color(1f, 0.35f, 0.08f, 1f);
    [Header("Joint IK Handles")]
    [SerializeField] private bool createJointHandles = true;
    [SerializeField, Min(0.01f)] private float jointHandleDiameter = 0.075f;
    [SerializeField] private Color jointHandleColor = new Color(0.1f, 0.85f, 1f, 0.8f);
    [SerializeField, Range(1, 2)] private int maximumConcurrentDrags = 2;
    [Header("Link Grabbing")]
    [SerializeField] private bool allowLinkGrabbing = true;

    private ArticulationBody selectedJoint;
    private Renderer[] selectedRenderers;
    private Color[] originalColors;
    private readonly Dictionary<Transform, ArticulationBody> jointByHandle =
        new Dictionary<Transform, ArticulationBody>();
    private readonly List<GameObject> jointHandles = new List<GameObject>();
    private sealed class ActiveDrag
    {
        public ArticulationBody body;
        public Vector3 localGrabPoint;
        public bool isJointPivot;
        public float distance;
        public Vector3 offset;
        public Vector3 target;
        // Set when the interactor grabbed a ghost control panel instead of the robot. Such a
        // drag occupies the interactor like any other but never touches the IK controller.
        public GhostControlPanel panel;
    }

    private readonly Dictionary<object, ActiveDrag> activeDrags =
        new Dictionary<object, ActiveDrag>();
    private readonly List<ActiveDrag> orderedDrags = new List<ActiveDrag>(2);

    public Transform Handle => handle;

    private void Awake()
    {
        if (handle != null && handleRenderer == null)
            handleRenderer = handle.GetComponentInChildren<Renderer>();
        SetHandleActive(false);
    }

    public void Configure(DirectArticulationIKController controller, Transform toolTransform, Transform handleTransform)
    {
        ikController = controller;
        endEffector = toolTransform;
        handle = handleTransform;
        handleRenderer = handle != null ? handle.GetComponentInChildren<Renderer>() : null;
        SetHandleActive(false);
        EnsureJointHandles();
    }

    private void Start()
    {
        if (pickPlaceClient == null) pickPlaceClient = FindFirstObjectByType<PickPlaceClient>();
        if (trajectoryPlayer == null) trajectoryPlayer = FindFirstObjectByType<MTCTrajectoryPlayer>();
    }

    private void UpdateHandleVisibilityForSolution()
    {
        if (handle == null) return;

        bool hide = (pickPlaceClient != null && pickPlaceClient.Phase == PickPlacePhase.Executing)
                 || (trajectoryPlayer != null && trajectoryPlayer.IsPlaying);
        if (hide == handleHiddenForSolution) return;

        handleHiddenForSolution = hide;
        handle.gameObject.SetActive(!hide);
    }

    private void LateUpdate()
    {
        UpdateHandleVisibilityForSolution();
        EnsureJointHandles();
        SolveActiveDrags();
        UpdateJointHandleWorldPositions();

        // A joint-handle drag moves the end effector as a consequence, so its original
        // handle should continue following it rather than being left behind in space.
        if (endEffector != null && handle != null && !HasEndEffectorDrag())
        {
            handle.position = endEffector.position;
        }
    }

    public void SelectFromHit(RaycastHit hit)
    {
        // The shared control panel is parented under whichever ghost currently owns it, so a hit
        // on the panel would otherwise also resolve to that ghost's GhostSelectable via the parent
        // lookup below. Exclude it so poking or grabbing any part of the panel cannot close it.
        var ghostSelectable = hit.transform.GetComponentInParent<GhostSelectable>();
        if (ghostSelectable != null && GetPanelFromHit(hit) != null)
            ghostSelectable = null;

        if (ghostSelectable != null)
        {
            ghostSelectable.OnSelected();
            return;
        }

        if (handle != null && (hit.transform == handle || hit.transform.IsChildOf(handle)))
        {
            ClearSelection();
            SetHandleActive(true);
            return;
        }

        if (TryGetJointHandle(hit.transform, out ArticulationBody handleJoint))
        {
            SetHandleActive(false);
            SelectJoint(handleJoint, hit.transform.GetComponent<Renderer>());
            return;
        }

        SetHandleActive(false);
        ArticulationBody hitJoint = hit.transform.GetComponentInParent<ArticulationBody>();
        if (ikController != null && ikController.CanControlJoint(hitJoint))
        {
            SelectJoint(hitJoint);
        }
        else
        {
            ClearSelection();
        }
    }

    /// <summary>Router-driven equivalent of TryBeginHandleDrag. Guidelines Part 3.</summary>
    public bool TryBeginHandleDrag(InteractionIntent intent)
    {
        return TryBeginHandleDrag(
            intent.Sample.SourceId ?? "default", intent.Ray, intent.HasHit ? intent.Hit : default);
    }

    // Direct entry for ray interactors (Quest3 controller, mouse); keyed by interactor identity.
    public bool TryBeginHandleDrag(object interactor, Ray ray, RaycastHit hit)
    {
        if (interactor == null || activeDrags.ContainsKey(interactor) ||
            activeDrags.Count >= maximumConcurrentDrags)
        {
            return false;
        }

        // Ghost control panels take priority: dragging one to reposition it shouldn't also
        // try to drag the IK handle underneath it.
        GhostControlPanel panel = GetPanelFromHit(hit);
        if (panel != null)
        {
            activeDrags.Add(interactor, new ActiveDrag
            {
                panel = panel,
                distance = hit.distance,
                offset = panel.ShellPosition - ray.GetPoint(hit.distance),
                target = panel.ShellPosition
            });
            panel.BeginDrag();
            return true;
        }

        if (ikController == null || handle == null)
        {
            return false;
        }

        bool hitHandle = hit.transform != null && (hit.transform == handle || hit.transform.IsChildOf(handle));
        bool hitJointHandle = TryGetJointHandle(hit.transform, out ArticulationBody joint);
        ArticulationBody linkBody = null;
        bool hitLink = false;
        if (allowLinkGrabbing && !hitHandle && !hitJointHandle && hit.transform != null)
        {
            linkBody = hit.transform.GetComponentInParent<ArticulationBody>();
            hitLink = ikController.CanControlBodyPoint(linkBody);
        }

        bool hitDragTarget = hitHandle || hitJointHandle || hitLink;
        // The near-miss assist must not grab a handle that is hidden for a solution, and it
        // must not fire when the grip is aimed at a grabbable object: the same grip press
        // starts the XRI grab on that object, and stealing it for the handle would drag the
        // robot along with the object and clear the selection mid-grab.
        if (!hitDragTarget && !handleHiddenForSolution && !HitsGrabbableObject(hit) &&
            Vector3.Cross(ray.direction, handle.position - ray.origin).magnitude <= 0.08f)
        {
            hitHandle = true;
        }

        if (!hitHandle && !hitJointHandle && !hitLink)
        {
            return false;
        }

        ArticulationBody grabbedBody = hitJointHandle ? joint : linkBody;
        if (IsTargetAlreadyGrabbed(grabbedBody))
            return false;

        Transform jointHandle = hitJointHandle ? hit.transform : null;
        Vector3 draggedPoint = hitJointHandle
            ? jointHandle.position
            : hitLink ? hit.point : handle.position;
        float distance = hitDragTarget
            ? hit.distance
            : Mathf.Max(0.1f, Vector3.Dot(draggedPoint - ray.origin, ray.direction));
        var drag = new ActiveDrag
        {
            body = grabbedBody,
            localGrabPoint = hitJointHandle
                ? joint.anchorPosition
                : hitLink ? linkBody.transform.InverseTransformPoint(hit.point) : Vector3.zero,
            isJointPivot = hitJointHandle,
            distance = distance,
            offset = draggedPoint - ray.GetPoint(distance),
            target = draggedPoint
        };

        if (!HasIkDrag()) ikController.BeginInteraction();
        activeDrags.Add(interactor, drag);
        if (hitHandle) SetHandleActive(true);
        if (hitJointHandle) SelectJoint(joint, jointHandle.GetComponent<Renderer>());
        else if (hitLink)
        {
            int jointIndex = ikController.GetBodyPointEffectorIndex(linkBody) - 1;
            Renderer hitRenderer = hit.transform.GetComponent<Renderer>() ??
                hit.transform.GetComponentInChildren<Renderer>();
            SelectJoint(ikController.Joints[jointIndex], hitRenderer);
        }
        else ClearSelection();
        // Grabbing the EE handle also drops the current shape selection, matching the
        // trigger-click behavior in SelectionManager.IsDeselectSurface.
        SelectionManager.Instance?.ClearSelection();
        // Joint and link drags already logged their own grab_start via SelectJoint.
        if (grabbedBody == null)
            ObjectMetricsLogger.Instance?.LogEvent("grab_start", EndEffectorHandleObjectId);
        return true;
    }

    // Lets the thumbstick push/pull the handle's drag distance along the ray while it's held,
    // mirroring the InteractionAttachController push/pull used for XRI far-grabbed objects.
    public void AdjustHandleDragDistance(object interactor, float delta, float maxDistance)
    {
        if (interactor == null || !activeDrags.TryGetValue(interactor, out ActiveDrag drag) ||
            drag.panel != null)
        {
            return;
        }

        drag.distance = Mathf.Clamp(drag.distance + delta, 0.1f, maxDistance);
    }

    /// <summary>Router-driven equivalent of UpdateHandleDrag.</summary>
    public void UpdateHandleDrag(InteractionIntent intent)
    {
        UpdateHandleDrag(intent.Sample.SourceId ?? "default", intent.Ray);
    }

    public void UpdateHandleDrag(object interactor, Ray ray)
    {
        if (interactor == null || !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
        {
            return;
        }

        drag.target = ray.GetPoint(drag.distance) + drag.offset;

        if (drag.panel != null)
        {
            drag.panel.UpdateDrag(drag.target);
            return;
        }

        if (ikController == null || handle == null)
        {
            return;
        }

        if (drag.body == null) handle.position = drag.target;
    }

    /// <summary>Router-driven equivalent of EndHandleDrag.</summary>
    public void EndHandleDrag(InteractionIntent intent)
    {
        EndHandleDrag(intent.Sample.SourceId ?? "default");
    }

    public void EndHandleDrag(object interactor)
    {
        if (interactor == null || !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
        {
            return;
        }

        activeDrags.Remove(interactor);

        if (drag.panel != null)
        {
            drag.panel.EndDrag();
            return;
        }

        bool draggedEndEffector = drag.body == null;
        if (!HasIkDrag()) ikController.EndInteraction();

        // Log before snapping the handle marker back to endEffector -- they should already
        // coincide (IK solves toward the drag target continuously), but endEffector is the
        // real robot pose, so that's the one worth logging.
        if (draggedEndEffector && endEffector != null)
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", EndEffectorHandleObjectId, endEffector.position, endEffector.rotation);

        if (endEffector != null && handle != null && !HasEndEffectorDrag())
            handle.position = endEffector.position;

        if (draggedEndEffector) SetHandleActive(HasEndEffectorDrag());
    }

    public void EndHandleDragBySource(string sourceId)
    {
        EndHandleDrag(sourceId ?? "default");
    }

    // True when the ray landed on an object the user can grab through XRI (spawned shapes,
    // planning-scene props): anything under a "Selectable"-tagged or XRGrabInteractable parent.
    private static bool HitsGrabbableObject(RaycastHit hit)
    {
        if (hit.transform == null) return false;
        if (hit.transform.GetComponentInParent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>() != null)
            return true;
        for (Transform t = hit.transform; t != null; t = t.parent)
        {
            if (t.CompareTag("Selectable")) return true;
        }
        return false;
    }

    // The panel's grabbable BoxCollider lives on the shell root while the GhostControlPanel
    // component lives on its child UIDocument object, so a raycast hit can land on either.
    private static GhostControlPanel GetPanelFromHit(RaycastHit hit)
    {
        if (hit.transform == null) return null;
        return hit.transform.GetComponentInChildren<GhostControlPanel>()
            ?? hit.transform.GetComponentInParent<GhostControlPanel>();
    }

    // JogSelectedJoint is called every frame regardless of thumbstick position (with
    // deltaRadians == 0 while centered/idle -- see Quest3ControllerRayInteractor.Update), so the
    // moving/idle transition can be detected entirely in here: log "grab_end" the frame jogging
    // stops rather than needing every-frame logging while the joystick is held.
    private bool isJoggingSelectedJoint = false;

    public void JogSelectedJoint(float deltaRadians)
    {
        if (selectedJoint == null || ikController == null)
        {
            return;
        }

        if (Mathf.Approximately(deltaRadians, 0f))
        {
            if (isJoggingSelectedJoint)
            {
                LogJointJogEnd();
            }
            return;
        }

        isJoggingSelectedJoint = true;

        ikController.BeginInteraction();
        LastRefusal = ikController.TryNudgeJoint(selectedJoint, deltaRadians);
        ikController.EndInteraction();
    }

    private void LogJointJogEnd()
    {
        isJoggingSelectedJoint = false;
        if (selectedJoint == null)
        {
            return;
        }

        float angleRad = selectedJoint.jointPosition[0];
        ObjectMetricsLogger.Instance?.LogEvent("grab_end", JointObjectId(selectedJoint),
            selectedJoint.transform.position, selectedJoint.transform.rotation, details: $"angle_rad:{angleRad:F4}");
    }

    private static string JointObjectId(ArticulationBody joint) => $"joint_{joint.name}";

    /// <summary>Router-driven selection. Target resolution already happened upstream.</summary>
    public void SelectFromIntent(InteractionIntent intent)
    {
        if (intent.HasHit) SelectFromHit(intent.Hit);
        else ClearSelection();
    }

    /// <summary>
    /// Why the most recent manipulation could not fully proceed, or
    /// <see cref="InteractionRefusal.None"/>. Guidelines Part 3.
    /// </summary>
    public InteractionRefusal LastRefusal { get; private set; } = InteractionRefusal.None;

    private void SelectJoint(ArticulationBody joint, Renderer preferredRenderer = null)
    {
        if (selectedJoint == joint)
        {
            return;
        }

        ClearSelection();
        selectedJoint = joint;
        selectedRenderers = preferredRenderer != null
            ? new[] { preferredRenderer }
            : selectedJoint.GetComponentsInChildren<Renderer>();
        originalColors = new Color[selectedRenderers.Length];

        for (int i = 0; i < selectedRenderers.Length; i++)
        {
            Material mat = selectedRenderers[i].material;
            originalColors[i] = GetColor(mat);
            SetColor(mat, selectedJointColor);
        }

        ObjectMetricsLogger.Instance?.LogEvent("grab_start", JointObjectId(joint));
    }

    private void ClearSelection()
    {
        // Flush a jog session that was still in progress when selection changed (e.g. the
        // participant let go of the trigger or grabbed something else mid-jog), so grab_start
        // never goes without a matching grab_end.
        if (isJoggingSelectedJoint)
        {
            LogJointJogEnd();
        }

        if (selectedRenderers != null && originalColors != null)
        {
            int count = Mathf.Min(selectedRenderers.Length, originalColors.Length);
            for (int i = 0; i < count; i++)
            {
                if (selectedRenderers[i] != null)
                {
                    SetColor(selectedRenderers[i].material, originalColors[i]);
                }
            }
        }

        selectedJoint = null;
        selectedRenderers = null;
        originalColors = null;
    }

    private void SetHandleActive(bool active)
    {
        if (handleRenderer != null)
        {
            SetColor(handleRenderer.material, active ? handleActiveColor : handleIdleColor);
        }
    }

    private void SolveActiveDrags()
    {
        if (ikController == null || activeDrags.Count == 0)
            return;

        orderedDrags.Clear();
        foreach (ActiveDrag drag in activeDrags.Values)
        {
            if (drag.panel == null) orderedDrags.Add(drag);
        }
        if (orderedDrags.Count == 0)
            return;

        orderedDrags.Sort((a, b) => GetEffectorIndex(a).CompareTo(GetEffectorIndex(b)));

        int firstJointIndex = 0;
        LastRefusal = InteractionRefusal.None;
        foreach (ActiveDrag drag in orderedDrags)
        {
            InteractionRefusal refusal;
            if (drag.body != null)
            {
                int effectorIndex = GetEffectorIndex(drag);
                refusal = drag.isJointPivot
                    ? ikController.TrySolveJointToTarget(
                        drag.body, drag.target, firstJointIndex)
                    : ikController.TrySolveBodyPointToTarget(
                        drag.body, drag.localGrabPoint, drag.target, firstJointIndex);
                if (effectorIndex >= 0) firstJointIndex = effectorIndex;
            }
            else
            {
                refusal = ikController.TrySolveToTarget(drag.target, firstJointIndex);
                firstJointIndex = ikController.Joints.Count;
                if (handle != null) handle.position = drag.target;
            }

            if (!LastRefusal.IsRefused && refusal.IsRefused)
                LastRefusal = refusal;
        }
    }

    private int GetEffectorIndex(ActiveDrag drag)
    {
        if (drag.body == null)
            return ikController.Joints.Count;

        return drag.isJointPivot
            ? ikController.GetJointIndex(drag.body)
            : ikController.GetBodyPointEffectorIndex(drag.body);
    }

    private bool IsTargetAlreadyGrabbed(ArticulationBody body)
    {
        foreach (ActiveDrag drag in activeDrags.Values)
        {
            if (drag.panel == null && drag.body == body)
                return true;
        }

        return false;
    }

    private bool HasIkDrag()
    {
        foreach (ActiveDrag drag in activeDrags.Values)
        {
            if (drag.panel == null)
                return true;
        }

        return false;
    }

    private bool HasEndEffectorDrag()
    {
        foreach (ActiveDrag drag in activeDrags.Values)
        {
            if (drag.panel == null && drag.body == null)
                return true;
        }

        return false;
    }

    private bool TryGetJointHandle(Transform candidate, out ArticulationBody joint)
    {
        joint = null;
        return candidate != null && jointByHandle.TryGetValue(candidate, out joint);
    }

    private void EnsureJointHandles()
    {
        if (!createJointHandles || jointByHandle.Count > 0 || ikController == null ||
            ikController.Joints.Count == 0)
            return;

        foreach (ArticulationBody joint in ikController.Joints)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"Joint IK Handle ({joint.name})";
            sphere.transform.SetParent(null, false);
            sphere.transform.SetPositionAndRotation(GetJointPivot(joint), Quaternion.identity);
            sphere.transform.localScale = Vector3.one * jointHandleDiameter;
            if (handle != null) sphere.layer = handle.gameObject.layer;

            SphereCollider sphereCollider = sphere.GetComponent<SphereCollider>();
            sphereCollider.isTrigger = true;

            Renderer sphereRenderer = sphere.GetComponent<Renderer>();
            if (handleRenderer != null && handleRenderer.sharedMaterial != null)
                sphereRenderer.material = new Material(handleRenderer.sharedMaterial);
            SetColor(sphereRenderer.material, jointHandleColor);
            jointByHandle.Add(sphere.transform, joint);
            jointHandles.Add(sphere);
        }
    }

    private void UpdateJointHandleWorldPositions()
    {
        foreach (KeyValuePair<Transform, ArticulationBody> pair in jointByHandle)
        {
            if (pair.Key != null && pair.Value != null)
                pair.Key.position = GetJointPivot(pair.Value);
        }
    }

    private void OnDestroy()
    {
        foreach (GameObject sphere in jointHandles)
        {
            if (sphere != null) Destroy(sphere);
        }
    }

    private static Vector3 GetJointPivot(ArticulationBody joint)
    {
        return joint.transform.TransformPoint(joint.anchorPosition);
    }

    private static Color GetColor(Material mat)
    {
        if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
        return mat.color;
    }

    private static void SetColor(Material mat, Color color)
    {
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
    }
}
