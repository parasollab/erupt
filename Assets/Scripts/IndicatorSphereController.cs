using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit;

// Attached to the IndicatorSphere prefab spawned by WristMenuController in Task3 scenes (the
// red sphere the participant drags onto the collision location they identified). Logs its own
// lifecycle/grab events via ObjectMetricsLogger the same way SelectableGrabController +
// CollisionObjectPublisher do for wrist-menu shapes, but standalone: this object is never a
// MoveIt collision object (it's a visual marker, not something to plan around), and it's always
// directly grabbable with no SelectionManager gating, since it's the only interactable of its
// kind in the scene.
[RequireComponent(typeof(XRGrabInteractable))]
public class IndicatorSphereController : MonoBehaviour
{
    // Assigned by WristMenuController right after Instantiate, before Start() runs.
    public string objectId = "indicator_sphere";

    private XRGrabInteractable grabInteractable;

    void Start()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        grabInteractable.selectEntered.AddListener(OnGrabEntered);
        grabInteractable.selectExited.AddListener(OnGrabExited);

        ObjectMetricsLogger.Instance?.LogEvent("object_created", objectId);
    }

    void OnDestroy()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnGrabEntered);
            grabInteractable.selectExited.RemoveListener(OnGrabExited);
        }

        ObjectMetricsLogger.Instance?.LogEvent("object_deleted", objectId);
    }

    void OnGrabEntered(SelectEnterEventArgs args)
    {
        ObjectMetricsLogger.Instance?.LogEvent("grab_start", objectId);
    }

    void OnGrabExited(SelectExitEventArgs args)
    {
        // ObjectMetricsLogger makes this relative to the robot base transform itself.
        ObjectMetricsLogger.Instance?.LogEvent("grab_end", objectId, transform.position, transform.rotation);
    }
}
