using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
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
    private bool locked = false;
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

        // A two-handed scale gesture ends when either controller lets go, so check on every
        // release, not just the last one.
        LogTwoHandedScaleIfEnded(publisher);

        if (!isGrabbed && publisher != null)
        {
            // ObjectMetricsLogger makes this relative to the robot base transform itself.
            ObjectMetricsLogger.Instance?.LogEvent("grab_end", publisher.objectId, transform.position, transform.rotation);
        }
    }

    // Logs a finished two-handed scale gesture exactly like WristMenuController logs a
    // uniform slider resize: one edit_operation carrying the final localScale and a
    // "resize:<shape>:<label>:<signed delta>" detail using the slider's own shape/label
    // names and its additive per-axis delta (new = old + delta on every axis). The erupt_ws
    // analysis therefore needs no changes to include these edits.
    void LogTwoHandedScaleIfEnded(CollisionObjectPublisher publisher)
    {
        XRTwoHandedScaleTransformer twoHand = GetComponent<XRTwoHandedScaleTransformer>();
        if (twoHand == null || !twoHand.IsGestureActive)
            return;

        Vector3 startScale = twoHand.GestureStartScale;
        twoHand.EndGesture();

        Vector3 endScale = transform.localScale;
        Vector3 axisDelta = endScale - startScale;
        // Two-hand scaling is uniform, so the per-axis deltas only differ when the start
        // scale was already non-uniform; the scale field holds the exact result regardless.
        float delta = (axisDelta.x + axisDelta.y + axisDelta.z) / 3f;
        if (Mathf.Approximately(delta, 0f))
            return;

        if (publisher == null)
        {
            Debug.LogWarning($"SelectableGrabController: two-handed resize on '{name}' not logged -- no CollisionObjectPublisher component.");
            return;
        }

        UniformResizeNames(gameObject, out string shape, out string label);
        string sign = delta >= 0 ? "+" : "";
        ObjectMetricsLogger.Instance?.LogEvent("edit_operation", publisher.objectId,
            scale: endScale,
            details: $"resize:{shape}:{label}:{sign}{delta:F3}");
        // Same reason the wrist menu does this: a scale-only change doesn't trip the
        // publisher's transform check, so push the new size to the planning scene.
        publisher.ForceRepublish();
    }

    // The shape and slider label WristMenuController uses for a uniform resize of this
    // object (see its edit-panel population and CreateToggleStack calls).
    static void UniformResizeNames(GameObject obj, out string shape, out string label)
    {
        MeshFilter meshFilter = obj.GetComponent<MeshFilter>();
        string meshName = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.name : "";
        if (meshName.Contains("Cube"))          { shape = "Cube";     label = "Uniform"; }
        else if (meshName.Contains("Sphere"))   { shape = "Sphere";   label = "Radius";  }
        else if (meshName.Contains("Cylinder")) { shape = "Cylinder"; label = "Uniform"; }
        else                                    { shape = "Mesh";     label = "Scale";   }
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
            grabInteractable.enabled = !locked && (isSelected || isGrabbed);
        }
    }

    /// <summary>
    /// While locked the object cannot be grabbed regardless of selection (the robot is
    /// carrying it). Disabling the interactable also drops any grab in progress.
    /// </summary>
    public void SetLocked(bool value)
    {
        locked = value;
        if (locked) isGrabbed = false;
        if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
        UpdateGrabState();
    }

    // Public method to force update grab state (useful for external calls)
    public void RefreshGrabState()
    {
        isSelected = SelectionManager.Instance != null &&
                    SelectionManager.Instance.SelectedObject == gameObject;
        UpdateGrabState();
    }
}
