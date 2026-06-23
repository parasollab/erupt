using UnityEngine;

public class Quest3RobotInteractionController : MonoBehaviour
{
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
    private Quest3ControllerRayInteractor activeDragInteractor;
    private float dragDistance;
    private Vector3 dragOffset;

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

    public bool TryBeginHandleDrag(Quest3ControllerRayInteractor interactor, Ray ray, RaycastHit hit)
    {
        if (ikController == null || handle == null || interactor == null)
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
        return true;
    }

    public void UpdateHandleDrag(Quest3ControllerRayInteractor interactor, Ray ray)
    {
        if (activeDragInteractor != interactor || ikController == null || handle == null)
        {
            return;
        }

        Vector3 target = ray.GetPoint(dragDistance) + dragOffset;
        handle.position = target;
        ikController.SolveToTarget(target);
    }

    public void EndHandleDrag(Quest3ControllerRayInteractor interactor)
    {
        if (activeDragInteractor != interactor)
        {
            return;
        }

        activeDragInteractor = null;
        ikController.EndInteraction();
        if (endEffector != null && handle != null)
        {
            handle.position = endEffector.position;
        }

        SetHandleActive(false);
    }

    public void JogSelectedJoint(float deltaRadians)
    {
        if (selectedJoint == null || ikController == null || Mathf.Approximately(deltaRadians, 0f))
        {
            return;
        }

        ikController.BeginInteraction();
        ikController.NudgeJoint(selectedJoint, deltaRadians);
        ikController.EndInteraction();
    }

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
