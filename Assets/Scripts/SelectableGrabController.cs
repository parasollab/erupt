using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using System.Collections;

/// <summary>
/// Controls whether an object can be grabbed based on its selection state.
/// Objects can only be grabbed when they are selected through the SelectionManager.
/// Once actively grabbed by an XRI interactor, the interactable stays enabled until
/// the grab is released, even if SelectionManager clears the selection.
/// </summary>
public class SelectableGrabController : MonoBehaviour
{
    private XRGrabInteractable grabInteractable;
    private bool isSelected = false;
    private bool isGrabbed = false;
    private SelectionManager subscribedSelectionManager;

    void Start()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        if (grabInteractable == null)
        {
            Debug.LogError("SelectableGrabController requires XRGrabInteractable component");
            return;
        }

        grabInteractable.selectEntered.AddListener(OnGrabEntered);
        grabInteractable.selectExited.AddListener(OnGrabExited);

        // Subscribe to selection events
        if (SelectionManager.Instance != null)
        {
            subscribedSelectionManager = SelectionManager.Instance;
            subscribedSelectionManager.OnObjectSelected += OnObjectSelected;
            subscribedSelectionManager.OnSelectionCleared += OnSelectionCleared;
        }

        // Check if this object is already selected (important for newly created objects)
        // Use coroutine to ensure all components are initialized
        StartCoroutine(CheckInitialSelectionStateNextFrame());
    }

    IEnumerator CheckInitialSelectionStateNextFrame()
    {
        // Wait one frame to ensure all components are fully initialized
        yield return null;
        CheckInitialSelectionState();
    }

    void CheckInitialSelectionState()
    {
        if (SelectionManager.Instance != null && SelectionManager.Instance.SelectedObject == gameObject)
        {
            isSelected = true;
        }
        else
        {
            isSelected = false;
        }
        UpdateGrabState();
    }

    void OnDestroy()
    {
        // Unsubscribe from events to prevent memory leaks
        if (subscribedSelectionManager != null)
        {
            subscribedSelectionManager.OnObjectSelected -= OnObjectSelected;
            subscribedSelectionManager.OnSelectionCleared -= OnSelectionCleared;
            subscribedSelectionManager = null;
        }
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnGrabEntered);
            grabInteractable.selectExited.RemoveListener(OnGrabExited);
        }
    }

    void OnGrabEntered(SelectEnterEventArgs args)
    {
        bool wasGrabbed = isGrabbed;
        isGrabbed = true;

        // NearFarInteractor initially places its far attach anchor at the collider hit point.
        // These objects intentionally use their center as the dynamic attach point so they
        // rotate about their own pivot. Align the interactor anchor to that same center before
        // the first grab update; otherwise every re-grab moves the center to the front surface
        // hit and makes the object creep closer to the controller.
        if (args.interactorObject is NearFarInteractor nearFar &&
            nearFar.interactionAttachController != null &&
            nearFar.interactionAttachController.hasOffset)
        {
            nearFar.interactionAttachController.MoveTo(transform.position);
        }

        UpdateGrabState();

        CollisionObjectPublisher publisher = GetComponent<CollisionObjectPublisher>();
        if (!wasGrabbed && publisher != null)
        {
            ObjectMetricsLogger.Instance?.LogEvent("grab_start", publisher.objectId);
        }
    }

    void OnGrabExited(SelectExitEventArgs args)
    {
        // With multi-select enabled, releasing either controller is not necessarily the end
        // of the grab. Keep the interactable enabled until the last controller lets go.
        isGrabbed = grabInteractable != null && grabInteractable.isSelected;
        UpdateGrabState();

        CollisionObjectPublisher publisher = GetComponent<CollisionObjectPublisher>();
        if (!isGrabbed && publisher != null)
        {
            // ObjectMetricsLogger makes this relative to the robot base transform itself.
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", publisher.objectId, transform.position, transform.rotation);
        }
    }

    void OnObjectSelected(GameObject selectedObject)
    {
        isSelected = (selectedObject == gameObject);
        UpdateGrabState();
    }

    void OnSelectionCleared()
    {
        isSelected = false;
        UpdateGrabState();
    }

    void UpdateGrabState()
    {
        if (grabInteractable != null)
        {
            // Keep enabled while selected OR while actively held by an XRI interactor
            grabInteractable.enabled = isSelected || isGrabbed;
        }
    }

    // Public method to force update grab state (useful for external calls)
    public void RefreshGrabState()
    {
        isSelected = SelectionManager.Instance != null &&
                    SelectionManager.Instance.SelectedObject == gameObject;
        UpdateGrabState();
    }
}
