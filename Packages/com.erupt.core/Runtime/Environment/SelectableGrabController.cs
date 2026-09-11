using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections;
using Erupt.Interaction;

/// <summary>
/// Controls whether an object can be grabbed based on its selection state.
/// Objects can only be grabbed when they are the current SelectionService selection.
/// Once actively grabbed by an XRI interactor, the interactable stays enabled until
/// the grab is released, even if the selection is cleared.
/// </summary>
public class SelectableGrabController : MonoBehaviour
{
    private XRGrabInteractable grabInteractable;
    private SelectionService selection;
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
        selection = FindFirstObjectByType<SelectionService>();
        if (selection != null) selection.SelectionChanged += OnSelectionChanged;

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
        isSelected = IsCurrent();
        UpdateGrabState();
    }

    void OnDestroy()
    {
        // Unsubscribe from events to prevent memory leaks
        if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
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
    }

    void OnGrabExited(SelectExitEventArgs args)
    {
        isGrabbed = false;
        UpdateGrabState();
    }

    void OnSelectionChanged(ISelectable current)
    {
        isSelected = current != null && current.GameObject == gameObject;
        UpdateGrabState();
    }

    bool IsCurrent() => selection != null && selection.Current != null && selection.Current.GameObject == gameObject;

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
        isSelected = IsCurrent();
        UpdateGrabState();
    }
}