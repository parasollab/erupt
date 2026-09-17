using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;

// Desktop stand-in for the XR ray + grab: left-click selects a Selectable object through
// SelectionManager and drags it on a camera-facing plane (Ctrl: ground plane, Alt+drag:
// rotate about the vertical axis, scroll: push/pull). Robot links and the end-effector
// handle are left to MouseRobotRayInteractor; UI panels are left to UI Toolkit.
public class MouseSceneObjectDragger : MonoBehaviour
{
    [SerializeField] private Camera rayCamera;
    [SerializeField] private float rayLength = 10f;
    [SerializeField] private LayerMask raycastLayers = ~0;
    [SerializeField] private float pushPullSpeed = 0.5f;
    [SerializeField] private float rotateDegreesPerPixel = 0.5f;
    [Tooltip("Used by the Delete key to remove the selected scene-graph object.")]
    [SerializeField] private FERLObjectPaletteController palette;

    private Transform dragTarget;
    private SceneGraphObject dragNode;
    private Plane dragPlane;
    private Vector3 dragOffset;
    private float dragDistance;

    private void Awake()
    {
        if (rayCamera == null)
            rayCamera = Camera.main;
    }

    private void Update()
    {
        if (rayCamera == null)
        {
            rayCamera = Camera.main;
            if (rayCamera == null)
                return;
        }

        if (Input.GetKeyDown(KeyCode.Delete) && palette != null)
            palette.DeleteSelected();

        if (Input.GetMouseButtonDown(0) && !Input.GetMouseButton(1))
            TryBeginDrag();

        if (dragTarget != null && Input.GetMouseButton(0))
            UpdateDrag();

        if (dragTarget != null && Input.GetMouseButtonUp(0))
            EndDrag();
    }

    private static bool IsUiHit(RaycastHit hit)
    {
        return hit.collider.GetComponentInParent<UIDocument>() != null;
    }

    private static bool IsRobotHit(RaycastHit hit)
    {
        return hit.collider.GetComponentInParent<ArticulationBody>() != null
               || hit.collider.GetComponentInParent<Quest3RobotInteractionController>() != null;
    }

    private static GameObject FindSelectable(Transform hitTransform)
    {
        Transform current = hitTransform;
        while (current != null)
        {
            if (current.CompareTag("Selectable"))
                return current.gameObject;
            current = current.parent;
        }
        return null;
    }

    private void TryBeginDrag()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;
        Ray ray = rayCamera.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, rayLength, raycastLayers, QueryTriggerInteraction.Ignore))
        {
            SelectionManager.Instance?.ClearSelection();
            return;
        }
        if (IsUiHit(hit) || IsRobotHit(hit))
            return;

        GameObject selectable = FindSelectable(hit.transform);
        if (selectable == null)
        {
            SelectionManager.Instance?.ClearSelection();
            return;
        }

        SelectionManager.Instance?.SetSelectedObject(selectable);
        dragTarget = selectable.transform;
        dragNode = selectable.GetComponent<SceneGraphObject>();
        dragDistance = hit.distance;
        dragPlane = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
            ? new Plane(Vector3.up, hit.point)
            : new Plane(-rayCamera.transform.forward, hit.point);
        dragOffset = dragTarget.position - hit.point;
        dragNode?.BeginMove();
    }

    private void UpdateDrag()
    {
        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
        {
            dragTarget.Rotate(Vector3.up, -Input.GetAxis("Mouse X") * rotateDegreesPerPixel * 10f, Space.World);
            return;
        }

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            Vector3 push = rayCamera.transform.forward * scroll * pushPullSpeed * 0.2f;
            dragOffset += push;
            dragPlane = new Plane(dragPlane.normal, dragPlane.ClosestPointOnPlane(dragTarget.position) + push);
        }

        Ray ray = rayCamera.ScreenPointToRay(Input.mousePosition);
        if (dragPlane.Raycast(ray, out float enter))
            dragTarget.position = ray.GetPoint(enter) + dragOffset;
    }

    private void EndDrag()
    {
        dragNode?.EndMove();
        dragTarget = null;
        dragNode = null;
    }
}
