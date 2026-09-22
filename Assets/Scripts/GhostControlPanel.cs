using UnityEngine;
using UnityEngine.UIElements;

// Overhead world-space panel exposing shared controls (start/goal visibility, replay speed)
// for all ghosts. SpawnGhosts owns a single instance of this (see SpawnGhosts.
// ToggleControlPanelForGhost) and reparents it onto whichever ghost was last selected, rather
// than each ghost spawning its own — so selecting a different ghost moves the same panel over.
// Mirrors MoveItPlanningRequestMenuUI's UIDocument/UXML setup (world-space UIDocument
// + XRSimpleInteractable + XRPokeFilter + BoxCollider), but lives on a child of a
// separate shell (BoxCollider only) so the panel itself can be dragged and repositioned
// independently of the poke-interaction collider. Dragging is driven by
// Quest3RobotInteractionController's custom grip-drag raycast pipeline (see BeginDrag/
// UpdateDrag/EndDrag below) rather than XRGrabInteractable, since this project's controller
// input bypasses the standard XR Interaction Toolkit select pipeline entirely.
public class GhostControlPanel : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    private VisualElement speedControlGroup;
    private Slider speedSlider;
    private Label speedLabel;
    private Toggle hideStartToggle;
    private Toggle hideGoalToggle;

    private SpawnGhosts spawner;

    private bool hasInitialized;

    private Transform cameraTransform;
    private bool billboardEnabled = true;
    private bool isGrabbed;

    // The shell is this object's parent: the BoxCollider-only root object that
    // Instantiate(prefab) actually returns. SpawnGhosts reparents it onto whichever ghost is
    // currently selected; its local transform (relative to that ghost) is what gets saved/
    // restored via SpawnGhosts.SavePanelOffset and reoriented by the billboard logic.
    private Transform Shell => transform.parent != null ? transform.parent : transform;

    private void OnEnable()
    {
        BindUI();
        SubscribeToSharedState();
    }

    private void OnDisable()
    {
        if (spawner != null)
            spawner.ControlStateChanged -= RefreshSharedState;
    }

    // Re-queries the UXML-backed elements and re-registers their callbacks every time this runs,
    // since UIDocument rebuilds rootVisualElement fresh on every OnEnable — any previously cached
    // element references and their event handlers are left pointing at an orphaned tree otherwise.
    private void BindUI()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument.rootVisualElement;
        if (root == null) return;

        if (hideStartToggle != null) hideStartToggle.UnregisterValueChangedCallback(OnHideStartToggleChanged);
        if (hideGoalToggle != null) hideGoalToggle.UnregisterValueChangedCallback(OnHideGoalToggleChanged);
        if (speedSlider != null) speedSlider.UnregisterValueChangedCallback(OnSpeedSliderChanged);

        speedControlGroup = root.Q<VisualElement>("speedControlGroup");
        speedSlider = root.Q<Slider>("speedSlider");
        speedLabel = root.Q<Label>("speedLabel");
        hideStartToggle = root.Q<Toggle>("hideStartToggle");
        hideGoalToggle = root.Q<Toggle>("hideGoalToggle");

        hideStartToggle?.RegisterValueChangedCallback(OnHideStartToggleChanged);
        hideGoalToggle?.RegisterValueChangedCallback(OnHideGoalToggleChanged);
        speedSlider?.RegisterValueChangedCallback(OnSpeedSliderChanged);

        if (hasInitialized)
            RefreshSharedState();
    }

    private void OnHideStartToggleChanged(ChangeEvent<bool> evt)
    {
        spawner?.SetStartGhostHidden(evt.newValue);
    }

    private void OnHideGoalToggleChanged(ChangeEvent<bool> evt)
    {
        spawner?.SetGoalGhostHidden(evt.newValue);
    }

    private void OnSpeedSliderChanged(ChangeEvent<float> evt)
    {
        spawner?.SetReplaySpeed(evt.newValue);
    }

    public void Init(SpawnGhosts owningSpawner)
    {
        spawner = owningSpawner;

        hasInitialized = true;

        BindUI();
        SubscribeToSharedState();
    }

    private void SubscribeToSharedState()
    {
        if (spawner == null) return;
        spawner.ControlStateChanged -= RefreshSharedState;
        spawner.ControlStateChanged += RefreshSharedState;
    }

    // Entry points for Quest3RobotInteractionController's grip-drag raycast pipeline
    // (see TryBeginHandleDrag/UpdateHandleDrag/EndHandleDrag there), which is what actually
    // drives movement in this project instead of XRGrabInteractable's select events.
    public Vector3 ShellPosition => Shell.position;

    public void BeginDrag()
    {
        isGrabbed = true;
        // Once the user has manually placed it, stop fighting them with auto-billboard.
        billboardEnabled = false;
    }

    public void UpdateDrag(Vector3 worldPosition)
    {
        Shell.position = worldPosition;
    }

    public void EndDrag()
    {
        isGrabbed = false;
        spawner?.SavePanelOffset(Shell.localPosition, Shell.localRotation);
    }

    public void SetCameraTransform(Transform camera)
    {
        cameraTransform = camera;
    }

    public void SetBillboardEnabled(bool enabled)
    {
        billboardEnabled = enabled;
    }

    private void LateUpdate()
    {
        if (!billboardEnabled || isGrabbed || cameraTransform == null) return;
        // The panel's readable face renders on the -Z side, so aim +Z away from the camera
        // (not LookAt(camera), which would point the readable face away from the viewer).
        Vector3 directionAwayFromCamera = Shell.position - cameraTransform.position;
        if (directionAwayFromCamera.sqrMagnitude < 0.0001f) return;
        Shell.rotation = Quaternion.LookRotation(directionAwayFromCamera);
    }

    private void RefreshSharedState()
    {
        if (spawner == null) return;

        hideStartToggle?.SetValueWithoutNotify(spawner.StartGhostHidden);
        hideGoalToggle?.SetValueWithoutNotify(spawner.GoalGhostHidden);

        if (speedControlGroup != null)
            speedControlGroup.style.display = DisplayStyle.Flex;
        if (speedSlider == null) return;
        speedSlider.lowValue = TrajectoryReplay.MinPlaybackSpeed;
        speedSlider.highValue = TrajectoryReplay.MaxPlaybackSpeed;
        speedSlider.SetEnabled(spawner.ReplayActive);
        speedSlider.SetValueWithoutNotify(spawner.ReplaySpeed);
        UpdateLabel(spawner.ReplaySpeed);
    }

    private void UpdateLabel(float value)
    {
        if (speedLabel != null)
            speedLabel.text = $"x{value:0.0}";
    }
}
