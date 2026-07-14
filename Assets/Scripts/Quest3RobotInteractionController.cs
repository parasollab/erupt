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
    [SerializeField] private Color selectedJointColor = new Color(1f, 0.35f, 0.08f, 1f);

    private ArticulationBody selectedJoint;
    private Renderer[] selectedRenderers;
    private Color[] originalColors;
    private object activeDragInteractor;
    private float dragDistance;
    private Vector3 dragOffset;
    private GhostControlPanel draggedPanel;

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
    }

    private void LateUpdate()
    {
        if (endEffector != null && handle != null && activeDragInteractor == null)
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

    public bool TryBeginHandleDrag(object interactor, Ray ray, RaycastHit hit)
    {
        if (interactor == null)
        {
            return false;
        }

        // Ghost control panels take priority: dragging one to reposition it shouldn't also
        // try to drag the IK handle underneath it.
        GhostControlPanel panel = GetPanelFromHit(hit);
        if (panel != null)
        {
            activeDragInteractor = interactor;
            draggedPanel = panel;
            dragDistance = hit.distance;
            dragOffset = panel.ShellPosition - ray.GetPoint(dragDistance);
            panel.BeginDrag();
            return true;
        }

        if (ikController == null || handle == null)
        {
            return false;
        }

        bool hitHandle = hit.transform != null && (hit.transform == handle || hit.transform.IsChildOf(handle));
        if (!hitHandle && Vector3.Cross(ray.direction, handle.position - ray.origin).magnitude > 0.08f)
        {
            return false;
        }

        activeDragInteractor = interactor;
        dragDistance = hitHandle ? hit.distance : Mathf.Max(0.1f, Vector3.Dot(handle.position - ray.origin, ray.direction));
        dragOffset = handle.position - ray.GetPoint(dragDistance);
        ikController.BeginInteraction();
        SetHandleActive(true);
        ClearSelection();
        ObjectMetricsLogger.Instance?.LogEvent("grab_start", EndEffectorHandleObjectId);
        return true;
    }

    public void UpdateHandleDrag(object interactor, Ray ray)
    {
        if (activeDragInteractor != interactor)
        {
            return;
        }

        Vector3 target = ray.GetPoint(dragDistance) + dragOffset;

        if (draggedPanel != null)
        {
            draggedPanel.UpdateDrag(target);
            return;
        }

        if (ikController == null || handle == null)
        {
            return;
        }

        handle.position = target;
        ikController.SolveToTarget(target);
    }

    public void EndHandleDrag(object interactor)
    {
        if (activeDragInteractor != interactor)
        {
            return;
        }

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
            // Log before snapping the handle marker back to endEffector -- they should already
            // coincide (IK solves toward the drag target continuously), but endEffector is the
            // real robot pose, so that's the one worth logging.
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", EndEffectorHandleObjectId, endEffector.position, endEffector.rotation);
            handle.position = endEffector.position;
        }

        SetHandleActive(false);
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
        ikController.NudgeJoint(selectedJoint, deltaRadians);
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
            selectedJoint.transform.position, selectedJoint.transform.rotation, $"angle_rad:{angleRad:F4}");
    }

    private static string JointObjectId(ArticulationBody joint) => $"joint_{joint.name}";

    private void SelectJoint(ArticulationBody joint)
    {
        if (selectedJoint == joint)
        {
            return;
        }

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
