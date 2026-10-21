using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    // World-space UI Toolkit panel for a machine: Open / Close buttons for its doors and
    // Clamp / Release for the vise on its table, with the vise's own report of what the last
    // clamp did (and how far the robot dropped the part). Placed just outside the machine like
    // its pendant. Same UIDocument/XRI stack as the MTC dashboard (the MoveItPlanningRequestMenu
    // prefab with MachineDoorPanel.uxml as the source asset).
    public class MachineDoorPanel : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private MachineDoors doors;
        [SerializeField] private MachineVise vise;
        [SerializeField, Tooltip("Reports how the robot's released parts landed; optional.")]
        private ReleasedPartGravity gravity;
        [SerializeField, Tooltip("Seconds between refreshes of the vise lines while nothing is animating.")]
        private float refreshSeconds = 0.5f;

        private VisualElement root;
        private Button openButton, closeButton, clampButton, releaseButton;
        private Label titleLabel, statusLabel, viseStatusLabel, viseDetailLabel, viseDropLabel;
        private Coroutine bindWhenReady, refreshLoop;

        public MachineDoors Doors => doors;
        public MachineVise Vise => vise;

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
            if (refreshLoop != null) { StopCoroutine(refreshLoop); refreshLoop = null; }
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
            clampButton = root.Q<Button>("viseClampButton");
            releaseButton = root.Q<Button>("viseReleaseButton");
            titleLabel = root.Q<Label>("doorTitleLabel");
            statusLabel = root.Q<Label>("doorStatusLabel");
            viseStatusLabel = root.Q<Label>("viseStatusLabel");
            viseDetailLabel = root.Q<Label>("viseDetailLabel");
            viseDropLabel = root.Q<Label>("viseDropLabel");
            if (openButton != null) openButton.clicked += OnOpen;
            if (closeButton != null) closeButton.clicked += OnClose;
            if (clampButton != null) clampButton.clicked += OnClamp;
            if (releaseButton != null) releaseButton.clicked += OnRelease;
            if (doors != null) doors.StateChanged += OnStateChanged;
            if (vise != null)
            {
                vise.StateChanged += OnViseStateChanged;
                vise.PresenceChanged += OnVisePresenceChanged;
            }
            if (gravity != null) gravity.Settled += OnPartSettled;
            if (titleLabel != null && doors != null && !string.IsNullOrEmpty(doors.machineName))
                titleLabel.text = doors.machineName;
            Refresh();
            RefreshVise();
            if (refreshLoop == null) refreshLoop = StartCoroutine(RefreshLoop());
        }

        private void Unbind()
        {
            if (openButton != null) openButton.clicked -= OnOpen;
            if (closeButton != null) closeButton.clicked -= OnClose;
            if (clampButton != null) clampButton.clicked -= OnClamp;
            if (releaseButton != null) releaseButton.clicked -= OnRelease;
            if (doors != null) doors.StateChanged -= OnStateChanged;
            if (vise != null)
            {
                vise.StateChanged -= OnViseStateChanged;
                vise.PresenceChanged -= OnVisePresenceChanged;
            }
            if (gravity != null) gravity.Settled -= OnPartSettled;
            openButton = closeButton = clampButton = releaseButton = null;
        }

        private IEnumerator RefreshLoop()
        {
            var wait = new WaitForSeconds(Mathf.Max(0.1f, refreshSeconds));
            while (true)
            {
                yield return wait;
                RefreshVise(); // the "held by the robot" flag changes without an event
            }
        }

        private void OnOpen() { if (doors != null) doors.Open(); }
        private void OnClose() { if (doors != null) doors.Close(); }
        private void OnClamp() { if (vise != null) vise.Clamp(); }
        private void OnRelease() { if (vise != null) vise.Release(); }
        private void OnStateChanged(MachineDoors.DoorState _) => Refresh();
        private void OnViseStateChanged(MachineVise.ViseState _) => RefreshVise();
        private void OnVisePresenceChanged(GameObject _) => RefreshVise();
        private void OnPartSettled(ReleasedPartGravity.DropReport _) => RefreshVise();

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

        private void RefreshVise()
        {
            if (vise == null)
            {
                if (viseStatusLabel != null) viseStatusLabel.text = "No vise assigned";
                if (viseDetailLabel != null) viseDetailLabel.text = "";
                if (viseDropLabel != null) viseDropLabel.text = "";
                clampButton?.SetEnabled(false);
                releaseButton?.SetEnabled(false);
                return;
            }

            if (viseStatusLabel != null)
                viseStatusLabel.text = vise.State switch
                {
                    MachineVise.ViseState.Open => "Vise open",
                    MachineVise.ViseState.Closing => "Clamping...",
                    MachineVise.ViseState.Opening => "Releasing...",
                    MachineVise.ViseState.Clamped => "Clamped - ready to run",
                    MachineVise.ViseState.ClampedPoor => "Clamped - check the part",
                    _ => "Closed on nothing",
                };
            if (viseDetailLabel != null)
            {
                viseDetailLabel.text = vise.Describe();
                viseDetailLabel.EnableInClassList("mtc-row--failed", vise.State == MachineVise.ViseState.ClampedPoor || vise.State == MachineVise.ViseState.Empty);
                viseDetailLabel.EnableInClassList("mtc-row--done", vise.State == MachineVise.ViseState.Clamped);
            }
            if (viseDropLabel != null)
            {
                if (gravity != null && gravity.HasDrop)
                {
                    var d = gravity.LastDrop;
                    string line = $"{d.partId} dropped {d.dropHeight * 1000f:F0} mm";
                    if (d.tiltDegrees > 10f) line += $", landed tilted {d.tiltDegrees:F0} deg";
                    if (d.dropHeight > gravity.dropWarning) line += " - release closer to the parallels";
                    if (d.preview) line += " (preview)";
                    viseDropLabel.text = line;
                    viseDropLabel.EnableInClassList("mtc-row--failed", d.dropHeight > gravity.dropWarning || d.tiltDegrees > 10f);
                }
                else viseDropLabel.text = "";
            }
            clampButton?.SetEnabled(vise.CanClamp);
            releaseButton?.SetEnabled(vise.CanRelease);
        }
    }
}
