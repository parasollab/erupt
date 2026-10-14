using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    // World-space UI Toolkit panel with Open / Close buttons for a machine's doors, placed just
    // outside the machine like its pendant. Same UIDocument/XRI stack as the MTC dashboard (the
    // MoveItPlanningRequestMenu prefab with MachineDoorPanel.uxml as the source asset).
    public class MachineDoorPanel : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private MachineDoors doors;

        private VisualElement root;
        private Button openButton, closeButton;
        private Label titleLabel, statusLabel;
        private Coroutine bindWhenReady;

        public MachineDoors Doors => doors;

        private void OnEnable()
        {
            if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
            if (uiDocument == null) uiDocument = GetComponentInChildren<UIDocument>(true);
            root = uiDocument != null ? uiDocument.rootVisualElement : null;
            if (root == null)
            {
                // The UIDocument may build its tree after this OnEnable; bind as soon as it exists.
                bindWhenReady = StartCoroutine(BindWhenRootReady());
                return;
            }
            Bind();
        }

        private void OnDisable()
        {
            if (bindWhenReady != null) { StopCoroutine(bindWhenReady); bindWhenReady = null; }
            Unbind();
        }

        private IEnumerator BindWhenRootReady()
        {
            while (uiDocument == null || uiDocument.rootVisualElement == null) yield return null;
            root = uiDocument.rootVisualElement;
            bindWhenReady = null;
            Bind();
        }

        private void Bind()
        {
            openButton = root.Q<Button>("doorOpenButton");
            closeButton = root.Q<Button>("doorCloseButton");
            titleLabel = root.Q<Label>("doorTitleLabel");
            statusLabel = root.Q<Label>("doorStatusLabel");
            if (openButton != null) openButton.clicked += OnOpen;
            if (closeButton != null) closeButton.clicked += OnClose;
            if (doors != null) doors.StateChanged += OnStateChanged;
            if (titleLabel != null && doors != null && !string.IsNullOrEmpty(doors.machineName))
                titleLabel.text = doors.machineName;
            Refresh();
        }

        private void Unbind()
        {
            if (openButton != null) openButton.clicked -= OnOpen;
            if (closeButton != null) closeButton.clicked -= OnClose;
            if (doors != null) doors.StateChanged -= OnStateChanged;
            openButton = closeButton = null;
        }

        private void OnOpen() { if (doors != null) doors.Open(); }
        private void OnClose() { if (doors != null) doors.Close(); }
        private void OnStateChanged(MachineDoors.DoorState _) => Refresh();

        private void Refresh()
        {
            if (doors == null)
            {
                if (statusLabel != null) statusLabel.text = "No machine assigned";
                openButton?.SetEnabled(false);
                closeButton?.SetEnabled(false);
                return;
            }
            MachineDoors.DoorState s = doors.State;
            if (statusLabel != null)
                statusLabel.text = s switch
                {
                    MachineDoors.DoorState.Open => "Doors open",
                    MachineDoors.DoorState.Closed => "Doors closed",
                    MachineDoors.DoorState.Opening => "Opening...",
                    _ => "Closing...",
                };
            openButton?.SetEnabled(s != MachineDoors.DoorState.Open && s != MachineDoors.DoorState.Opening);
            closeButton?.SetEnabled(s != MachineDoors.DoorState.Closed && s != MachineDoors.DoorState.Closing);
        }
    }
}
