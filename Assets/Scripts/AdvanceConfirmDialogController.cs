using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

// Confirm/Cancel dialog shown by StudyController before any study advancement. Assembled at
// runtime from the XRI "UI Toolkit Grab UI" prefab (same world-space UI stack as
// CertifyPathMenuController), so the participant resolves it by pointing the controller ray
// at a button and pulling the trigger.
public class AdvanceConfirmDialogController : MonoBehaviour
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    public event System.Action Confirmed;
    public event System.Action Cancelled;

    private Button confirmButton;
    private Button cancelButton;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("AdvanceConfirmDialogController: No UIDocument/rootVisualElement found.");
            return;
        }

        confirmButton = root.Q<Button>("advanceConfirmButton");
        cancelButton = root.Q<Button>("advanceCancelButton");
        if (confirmButton == null || cancelButton == null)
        {
            Debug.LogError("AdvanceConfirmDialogController: Confirm/Cancel buttons not found in UXML.");
            return;
        }

        confirmButton.clicked += OnConfirmClicked;
        cancelButton.clicked += OnCancelClicked;
    }

    // Reconfigures the dialog as a requirements-not-met notice: no Confirm choice, just a
    // single OK button that dismisses (StudyController wires both events to close).
    public void SetBlockedMode(string title, string body)
    {
        VisualElement root = uiDocument?.rootVisualElement;
        Label titleLabel = root?.Q<Label>("advanceConfirmTitleLabel");
        Label bodyLabel = root?.Q<Label>("advanceConfirmInstructionsLabel");
        if (titleLabel != null)
            titleLabel.text = title;
        if (bodyLabel != null)
            bodyLabel.text = body;
        if (confirmButton != null)
            confirmButton.style.display = DisplayStyle.None;
        if (cancelButton != null)
            cancelButton.text = "OK";
    }

    private void OnDisable()
    {
        if (confirmButton != null)
            confirmButton.clicked -= OnConfirmClicked;
        if (cancelButton != null)
            cancelButton.clicked -= OnCancelClicked;
    }

    private void Update()
    {
        // Editor/desktop convenience: Enter confirms, Escape cancels, so the flow can be
        // tested without a headset (the ray-click path needs XR controllers).
        if (Keyboard.current == null)
            return;
        if (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)
            OnConfirmClicked();
        else if (Keyboard.current.escapeKey.wasPressedThisFrame)
            OnCancelClicked();
    }

    private void OnConfirmClicked()
    {
        Confirmed?.Invoke();
    }

    private void OnCancelClicked()
    {
        Cancelled?.Invoke();
    }
}
