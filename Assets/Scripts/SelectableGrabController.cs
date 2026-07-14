using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit;
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
            SelectionManager.Instance.OnObjectSelected += OnObjectSelected;
            SelectionManager.Instance.OnSelectionCleared += OnSelectionCleared;
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
        if (SelectionManager.Instance != null)
        {
            SelectionManager.Instance.OnObjectSelected -= OnObjectSelected;
            SelectionManager.Instance.OnSelectionCleared -= OnSelectionCleared;
        }
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnGrabEntered);
            grabInteractable.selectExited.RemoveListener(OnGrabExited);
        }
    }

    void OnGrabEntered(SelectEnterEventArgs args)
    {
        isGrabbed = true;
        UpdateGrabState();

        CollisionObjectPublisher publisher = GetComponent<CollisionObjectPublisher>();
        if (publisher != null)
        {
            ObjectMetricsLogger.Instance?.LogEvent("grab_start", publisher.objectId);
        }
    }

    void OnGrabExited(SelectExitEventArgs args)
    {
        isGrabbed = false;
        UpdateGrabState();

        CollisionObjectPublisher publisher = GetComponent<CollisionObjectPublisher>();
        if (publisher != null)
        {
            Vector3 relPos = publisher.GetRelativePosition(transform.position);
            Quaternion relRot = publisher.GetRelativeRotation(transform.rotation);
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", publisher.objectId, relPos, relRot);
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