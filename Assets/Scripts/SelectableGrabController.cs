using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using System.Collections;

/// <summary>
/// Controls whether an object can be grabbed based on its selection state.
/// Objects can only be grabbed when they are selected through the SelectionManager.
/// </summary>
public class SelectableGrabController : MonoBehaviour
{
    private XRGrabInteractable grabInteractable;
    private bool isSelected = false;

    void Start()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        if (grabInteractable == null)
        {
            Debug.LogError("SelectableGrabController requires XRGrabInteractable component");
            return;
        }

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
            // Enable grabbing only when selected
            grabInteractable.enabled = isSelected;
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