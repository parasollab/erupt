using System.Collections.Generic;
using Erupt.Interaction;
using UnityEngine;

public class Quest3RobotInteractionController : MonoBehaviour
{
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

    public bool TryBeginHandleDrag(Quest3ControllerRayInteractor interactor, Ray ray, RaycastHit hit)
    {
        return TryBeginHandleDrag((object)interactor, ray, hit);
    }

    /// <summary>Router-driven equivalent of TryBeginHandleDrag. Guidelines Part 3.</summary>
    public bool TryBeginHandleDrag(InteractionIntent intent)
    {
        return TryBeginHandleDrag(
            intent.Sample.SourceId ?? "default", intent.Ray, intent.HasHit ? intent.Hit : default);
    }

    private bool TryBeginHandleDrag(object interactor, Ray ray, RaycastHit hit)
    {
        if (ikController == null || handle == null || interactor == null ||
            activeDrags.ContainsKey(interactor) || activeDrags.Count >= maximumConcurrentDrags)
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
        // The near-miss assist must not grab a handle that is hidden for a solution.
        if (!hitDragTarget && !handleHiddenForSolution &&
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

        if (activeDrags.Count == 0) ikController.BeginInteraction();
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
        return true;
    }

    public void UpdateHandleDrag(Quest3ControllerRayInteractor interactor, Ray ray)
    {
        UpdateHandleDrag((object)interactor, ray);
    }

    /// <summary>Router-driven equivalent of UpdateHandleDrag.</summary>
    public void UpdateHandleDrag(InteractionIntent intent)
    {
        UpdateHandleDrag(intent.Sample.SourceId ?? "default", intent.Ray);
    }

    private void UpdateHandleDrag(object interactor, Ray ray)
    {
        if (interactor == null || ikController == null || handle == null ||
            !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
        {
            return;
        }

        drag.target = ray.GetPoint(drag.distance) + drag.offset;
        if (drag.body == null) handle.position = drag.target;
    }

    public void EndHandleDrag(Quest3ControllerRayInteractor interactor)
    {
        EndHandleDrag((object)interactor);
    }

    /// <summary>Router-driven equivalent of EndHandleDrag.</summary>
    public void EndHandleDrag(InteractionIntent intent)
    {
        EndHandleDrag(intent.Sample.SourceId ?? "default");
    }

    private void EndHandleDrag(object interactor)
    {
        if (interactor == null || !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
        {
            return;
        }

        bool draggedEndEffector = drag.body == null;
        activeDrags.Remove(interactor);
        if (activeDrags.Count == 0) ikController.EndInteraction();

        if (endEffector != null && handle != null && !HasEndEffectorDrag())
            handle.position = endEffector.position;

        if (draggedEndEffector) SetHandleActive(HasEndEffectorDrag());
    }

    public void EndHandleDragBySource(string sourceId)
    {
        EndHandleDrag(sourceId ?? "default");
    }

    public void JogSelectedJoint(float deltaRadians)
    {
        if (selectedJoint == null || ikController == null || Mathf.Approximately(deltaRadians, 0f))
        {
            return;
        }

        ikController.BeginInteraction();
        LastRefusal = ikController.TryNudgeJoint(selectedJoint, deltaRadians);
        ikController.EndInteraction();
    }

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
    }

    private void ClearSelection()
    {
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
            orderedDrags.Add(drag);

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
            if (drag.body == body)
                return true;
        }

        return false;
    }

    private bool HasEndEffectorDrag()
    {
        foreach (ActiveDrag drag in activeDrags.Values)
        {
            if (drag.body == null)
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
