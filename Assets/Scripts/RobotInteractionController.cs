using UnityEngine;

public class RobotInteractionController : MonoBehaviour
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
    [SerializeField] private Color selectedJointColor = new Color(1f, 0.35f, 0.08f, 1f);

    private ArticulationBody selectedJoint;
    private Renderer[] selectedRenderers;
    private Color[] originalColors;
    private object activeDragInteractor;
    private float dragDistance;
    private Vector3 dragOffset;
    private GhostControlPanel draggedPanel;
    private bool isJoggingSelectedJoint;

    public Transform Handle => handle;

    protected virtual void Awake()
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
    }

    protected virtual void LateUpdate()
    {
        if (endEffector != null && handle != null && activeDragInteractor == null)
        {
            handle.position = endEffector.position;
        }
    }

    public void SelectFromHit(RaycastHit hit)
    {
        SelectFromTransform(hit.transform);
    }

    public void SelectFromTarget(GameObject target)
    {
        SelectFromTransform(target != null ? target.transform : null);
    }

    void SelectFromTransform(Transform hitTransform)
    {
        if (hitTransform == null)
            return;

        var ghostSelectable = hitTransform.GetComponentInParent<GhostSelectable>();
        if (ghostSelectable != null && GetPanelFromTransform(hitTransform) != null)
            ghostSelectable = null;

        if (ghostSelectable != null)
        {
            ghostSelectable.OnSelected();
            return;
        }

        if (IsHandleHit(hitTransform))
        {
            ClearSelection();
            SetHandleActive(true);
            return;
        }

        SetHandleActive(false);
        ArticulationBody hitJoint = hitTransform.GetComponentInParent<ArticulationBody>();
        if (ikController != null && ikController.CanControlJoint(hitJoint))
        {
            SelectJoint(hitJoint);
        }
        else
        {
            ClearSelection();
        }
    }

    public bool TryBeginHandleDrag(object interactor, Ray ray, RaycastHit hit)
    {
        return TryBeginHandleDrag(interactor, ray, hit.transform, hit.distance);
    }

    public bool TryBeginHandleDrag(object interactor, Ray ray, GameObject target, Vector3 interactionPoint)
    {
        float distance = Mathf.Max(0.1f, Vector3.Dot(interactionPoint - ray.origin, ray.direction));
        return TryBeginHandleDrag(interactor, ray, target != null ? target.transform : null, distance);
    }

    public bool TryBeginHandleDrag(object interactor, Ray ray, Transform hitTransform, float hitDistance)
    {
        if (interactor == null)
            return false;

        GhostControlPanel panel = GetPanelFromTransform(hitTransform);
        if (panel != null)
        {
            activeDragInteractor = interactor;
            draggedPanel = panel;
            dragDistance = Mathf.Max(0.1f, hitDistance);
            dragOffset = panel.ShellPosition - ray.GetPoint(dragDistance);
            panel.BeginDrag();
            return true;
        }

        if (ikController == null || handle == null)
            return false;

        bool hitHandle = IsHandleHit(hitTransform);
        if (!hitHandle && Vector3.Cross(ray.direction, handle.position - ray.origin).magnitude > 0.08f)
            return false;

        activeDragInteractor = interactor;
        dragDistance = hitHandle ? Mathf.Max(0.1f, hitDistance) : Mathf.Max(0.1f, Vector3.Dot(handle.position - ray.origin, ray.direction));
        dragOffset = handle.position - ray.GetPoint(dragDistance);
        ikController.BeginInteraction();
        SetHandleActive(true);
        ClearSelection();
        ObjectMetricsLogger.Instance?.LogEvent("grab_start", EndEffectorHandleObjectId);
        return true;
    }

    public void AdjustHandleDragDistance(object interactor, float delta, float maxDistance)
    {
        if (activeDragInteractor != interactor || draggedPanel != null)
            return;

        dragDistance = Mathf.Clamp(dragDistance + delta, 0.1f, maxDistance);
    }

    public void UpdateHandleDrag(object interactor, Ray ray)
    {
        if (activeDragInteractor != interactor)
            return;

        Vector3 target = ray.GetPoint(dragDistance) + dragOffset;

        if (draggedPanel != null)
        {
            draggedPanel.UpdateDrag(target);
            return;
        }

        if (ikController == null || handle == null)
            return;

        handle.position = target;
        ikController.SolveToTarget(target);
    }

    public void UpdateHandleDragToWorldPoint(object interactor, Vector3 target)
    {
        if (activeDragInteractor != interactor)
            return;

        if (draggedPanel != null)
        {
            draggedPanel.UpdateDrag(target);
            return;
        }

        if (ikController == null || handle == null)
            return;

        handle.position = target;
        ikController.SolveToTarget(target);
    }

    public void EndHandleDrag(object interactor)
    {
        if (activeDragInteractor != interactor)
            return;

        activeDragInteractor = null;

        if (draggedPanel != null)
        {
            draggedPanel.EndDrag();
            draggedPanel = null;
            return;
        }

        ikController.EndInteraction();
        if (endEffector != null && handle != null)
        {
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", EndEffectorHandleObjectId, endEffector.position, endEffector.rotation);
            handle.position = endEffector.position;
        }

        SetHandleActive(false);
    }

    public bool CanRouteHit(RaycastHit hit)
    {
        return CanRouteTransform(hit.transform);
    }

    public bool CanRouteTarget(GameObject target)
    {
        return CanRouteTransform(target != null ? target.transform : null);
    }

    bool CanRouteTransform(Transform hitTransform)
    {
        if (hitTransform == null)
            return false;

        if (GetPanelFromTransform(hitTransform) != null || IsHandleHit(hitTransform))
            return true;

        var ghostSelectable = hitTransform.GetComponentInParent<GhostSelectable>();
        if (ghostSelectable != null)
            return true;

        var hitJoint = hitTransform.GetComponentInParent<ArticulationBody>();
        return ikController != null && ikController.CanControlJoint(hitJoint);
    }

    public void JogSelectedJoint(float deltaRadians)
    {
        if (selectedJoint == null || ikController == null)
            return;

        if (Mathf.Approximately(deltaRadians, 0f))
        {
            if (isJoggingSelectedJoint)
                LogJointJogEnd();
            return;
        }

        isJoggingSelectedJoint = true;

        ikController.BeginInteraction();
        ikController.NudgeJoint(selectedJoint, deltaRadians);
        ikController.EndInteraction();
    }

    private bool IsHandleHit(Transform hitTransform)
    {
        return handle != null && hitTransform != null && (hitTransform == handle || hitTransform.IsChildOf(handle));
    }

    private static GhostControlPanel GetPanelFromTransform(Transform hitTransform)
    {
        if (hitTransform == null) return null;
        return hitTransform.GetComponentInChildren<GhostControlPanel>()
            ?? hitTransform.GetComponentInParent<GhostControlPanel>();
    }

    private void LogJointJogEnd()
    {
        isJoggingSelectedJoint = false;
        if (selectedJoint == null)
            return;

        float angleRad = selectedJoint.jointPosition[0];
        ObjectMetricsLogger.Instance?.LogEvent("grab_end", JointObjectId(selectedJoint),
            selectedJoint.transform.position, selectedJoint.transform.rotation, details: $"angle_rad:{angleRad:F4}");
    }

    private static string JointObjectId(ArticulationBody joint) => $"joint_{joint.name}";

    private void SelectJoint(ArticulationBody joint)
    {
        if (selectedJoint == joint)
            return;

        ClearSelection();
        selectedJoint = joint;
        selectedRenderers = selectedJoint.GetComponentsInChildren<Renderer>();
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
        if (isJoggingSelectedJoint)
            LogJointJogEnd();

        if (selectedRenderers != null && originalColors != null)
        {
            int count = Mathf.Min(selectedRenderers.Length, originalColors.Length);
            for (int i = 0; i < count; i++)
            {
                if (selectedRenderers[i] != null)
                    SetColor(selectedRenderers[i].material, originalColors[i]);
            }
        }

        selectedJoint = null;
        selectedRenderers = null;
        originalColors = null;
    }

    private void SetHandleActive(bool active)
    {
        if (handleRenderer != null)
            SetColor(handleRenderer.material, active ? handleActiveColor : handleIdleColor);
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
