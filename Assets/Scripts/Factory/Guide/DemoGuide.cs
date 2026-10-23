using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Factory
{
    /// <summary>
    /// Step director for the factory demo. Reads the live state of the placement controller,
    /// doors, vise, billets, recorder and MTC client every frame, resolves the one step the user
    /// should do next (<see cref="DemoGuideSteps.Resolve"/>) and publishes it, the thing to point
    /// at, the card text and how long the user has been stuck. Nothing here changes the demo's
    /// state except two pieces of bookkeeping the components do not keep themselves: whether the
    /// last execution loaded the part (<see cref="LoadedByRobot"/>) and the last problem worth a
    /// line on the card (<see cref="Notice"/>). The card, the highlight and the off-screen
    /// chevron are separate components that only read from here.
    /// </summary>
    public class DemoGuide : MonoBehaviour
    {
        private const string LogTag = "[DemoGuide]";

        [Header("Demo state")]
        [SerializeField] private RobotPlacementController placement;
        [SerializeField] private MachineDoors doors;
        [SerializeField] private MachineVise vise;
        [SerializeField] private ReleasedPartGravity gravity;
        [SerializeField] private GrabbablePartSet billets;
        [SerializeField] private PickPlaceClient client;
        [SerializeField] private PickPlaceTaskRecorder recorder;
        [SerializeField, Tooltip("Found in the scene when empty.")] private SelectionManager selection;
        [SerializeField, Tooltip("Editor stand-in for the hands; its drag counts as a grab.")] private DesktopMouseInteractor mouse;
        [SerializeField, Tooltip("Switched between the vise and the conveyor zone for the load and the unload.")] private GuidedRecording recording;
        [SerializeField, Tooltip("Where the unload puts the part down (the conveyor's DropAnchor).")] private Transform conveyorAnchor;
        [SerializeField, Tooltip("Metres around the conveyor anchor that count as on the belt.")] private float conveyorZoneRadius = 0.25f;

        [Header("Targets")]
        [SerializeField, Tooltip("The floor ring of the placement phase.")] private Transform placementHandle;
        [SerializeField] private RobotPlacementPanel placementPanel;
        [SerializeField] private MachineDoorPanel doorPanel;
        [SerializeField] private MTCDashboardPanel dashboard;

        [Header("Timing")]
        [SerializeField, Tooltip("Seconds in one step before the extra hint line appears and the highlight pulses faster.")]
        private float hintAfterSeconds = 30f;
        [SerializeField, Tooltip("Seconds in one step before the card suggests asking the person nearby.")]
        private float helpAfterSeconds = 75f;
        [SerializeField, Tooltip("The connection counts as lost only after this many seconds without a ready planning action.")]
        private float offlineGraceSeconds = 8f;
        [SerializeField, Tooltip("Seconds the machine 'runs the job' with the doors closed before the unload starts.")]
        private float machiningSeconds = 8f;

        public GuideStep Step { get; private set; } = GuideStep.Welcome;
        public GuideSignals Signals { get; private set; }
        public GuideTarget Target { get; private set; } = GuideTarget.None;
        /// <summary>World object the highlight frames (ring, billet, jaw window, or the panel holding the button).</summary>
        public Transform TargetTransform { get; private set; }
        /// <summary>Panel and element name when the target is a button, else null.</summary>
        public UIDocument TargetDocument { get; private set; }
        public string TargetElementName { get; private set; }
        /// <summary>Box to frame in the target's local frame when it has no renderers (the jaw window); zero = use the renderers.</summary>
        public Vector3 TargetBoxSize { get; private set; }
        public Vector3 TargetBoxLocalCentre { get; private set; }
        public float SecondsInStep => Time.time - _stepStarted;
        /// <summary>0 = fresh, 1 = hint shown, 2 = "ask for help" shown. Waiting steps never escalate.</summary>
        public int Escalation { get; private set; }
        public bool RobotOnline { get; private set; } = true;
        /// <summary>Last problem worth a line on the card (plan failed, part fell, ...); cleared when the step changes.</summary>
        public string Notice { get; private set; }
        public bool LoadedByRobot { get; private set; }
        /// <summary>The job ran (the machining timer elapsed with the doors closed on the clamped part).</summary>
        public bool Machined { get; private set; }
        /// <summary>The unload execution finished successfully.</summary>
        public bool UnloadedByRobot { get; private set; }
        /// <summary>The billet the robot loaded; the one the unload is about.</summary>
        public GameObject MachinedBillet { get; private set; }
        public bool WelcomeDone { get; private set; }
        public float MachiningSeconds => machiningSeconds;
        /// <summary>A billet held by something other than XRI or the mouse rig (probes, other input backends).</summary>
        public GameObject ExternalGrab { get; set; }
        public GuideText Text => DemoGuideSteps.Describe(Step, Signals, RobotOnline);

        public event Action<GuideStep> StepChanged;
        /// <summary>Anything on the card may have changed (step, text variant, escalation, notice).</summary>
        public event Action Changed;

        private float _stepStarted;
        private float _offlineSince = -1f;
        private bool _unloadPlanned;       // a plan was requested after the job ran: the solutions are the unload's
        private PickPlacePhase _lastPhase = PickPlacePhase.Idle;
        private GuideSignals _lastSignals;
        private bool _lastOnline = true;
        private int _lastEscalation;
        private string _lastNotice;
        private readonly List<XRGrabInteractable> _billetGrabs = new List<XRGrabInteractable>();

        private void Awake()
        {
            if (selection == null) selection = SelectionManager.Instance;
            if (mouse == null) mouse = FindFirstObjectByType<DesktopMouseInteractor>(FindObjectsInactive.Include);
            if (recording == null) recording = GetComponent<GuidedRecording>();
            _stepStarted = Time.time;
        }

        private void OnEnable()
        {
            if (client != null) client.OnPhaseChanged += OnClientPhaseChanged;
            if (gravity != null) gravity.Settled += OnPartSettled;
        }

        private void OnDisable()
        {
            if (client != null) client.OnPhaseChanged -= OnClientPhaseChanged;
            if (gravity != null) gravity.Settled -= OnPartSettled;
        }

        private void Start()
        {
            if (selection == null) selection = SelectionManager.Instance;
            if (client != null) _lastPhase = client.Phase;
            Evaluate();
        }

        private void Update() => Evaluate();

        // ---- public API ------------------------------------------------------------------

        /// <summary>The welcome step is over: "Begin" was pressed on the placement panel (or a probe says so).</summary>
        public void CompleteWelcome()
        {
            if (WelcomeDone) return;
            WelcomeDone = true;
            Evaluate();
        }

        /// <summary>Back to the first step with the bookkeeping cleared (demo restart).</summary>
        public void ResetProgress()
        {
            WelcomeDone = false;
            ClearLoad();
            Notice = null;
            Evaluate();
        }

        private void ClearLoad()
        {
            LoadedByRobot = false;
            Machined = false;
            UnloadedByRobot = false;
            MachinedBillet = null;
            _unloadPlanned = false;
        }

        /// <summary>Jumps the machining timer (probes, docent).</summary>
        public void CompleteMachining()
        {
            if (Step == GuideStep.Machining) Machined = true;
        }

        /// <summary>Puts a line on the card until the step changes (part recovered, ...).</summary>
        public void Notify(string notice)
        {
            Notice = notice;
            Debug.Log($"{LogTag} notice: {notice}");
            Evaluate();
        }

        /// <summary>The billet the user should pick next: the highest one still on the stand.</summary>
        public GameObject TopBillet()
        {
            GameObject best = null;
            float bestY = float.MinValue;
            foreach (GameObject part in Parts())
            {
                if (part == null || !part.activeInHierarchy) continue;
                var pub = part.GetComponent<CollisionObjectPublisher>();
                if (pub != null && pub.attachedToRobot) continue;
                if (vise != null && vise.PresentPart == part) continue;
                if (part.transform.position.y > bestY) { bestY = part.transform.position.y; best = part; }
            }
            return best;
        }

        public bool IsBillet(GameObject go)
        {
            if (go == null) return false;
            foreach (GameObject part in Parts())
                if (part == go) return true;
            return false;
        }

        // ---- bookkeeping -----------------------------------------------------------------

        private void OnClientPhaseChanged()
        {
            if (client == null) return;
            PickPlacePhase now = client.Phase;
            PickPlacePhase before = _lastPhase;
            _lastPhase = now;
            if (now != PickPlacePhase.Idle) return;
            if (before == PickPlacePhase.Executing)
            {
                if (client.LastOutcome == PickPlaceOutcome.Succeeded)
                {
                    if (Machined)
                    {
                        UnloadedByRobot = true;
                        Debug.Log($"{LogTag} execution succeeded: the part is unloaded");
                    }
                    else
                    {
                        LoadedByRobot = true;
                        Debug.Log($"{LogTag} execution succeeded: the part is loaded");
                    }
                }
                else
                    Notify(client.LastOutcome == PickPlaceOutcome.Canceled
                        ? "The move was cancelled. Press Execute to run it again."
                        : "The robot stopped before finishing. Press Execute to try again.");
            }
            else if (before == PickPlacePhase.Planning && client.LastOutcome != PickPlaceOutcome.Succeeded && client.LastOutcome != PickPlaceOutcome.Canceled)
            {
                Notify(client.LastOutcome == PickPlaceOutcome.Unavailable || client.LastOutcome == PickPlaceOutcome.Disconnected
                    ? "The robot is not connected, so it could not plan. Ask the person next to you."
                    : "The robot could not find a way to do that. Try again: set the billet lower and centred between the jaws.");
            }
        }

        private void OnPartSettled(ReleasedPartGravity.DropReport report)
        {
            if (!LoadedByRobot || report.preview || Machined) return;
            // Where the robot's release actually ended up: in the jaws, or beside them.
            if (vise != null) vise.ScanPresence();
            if (vise != null && vise.PresentPart == report.part && !vise.PresentPartHeldByRobot) return;
            LoadedByRobot = false;
            Notify("The billet landed beside the vise. Select it and show the robot the spot again.");
        }

        // ---- evaluation ------------------------------------------------------------------

        private IEnumerable<GameObject> Parts()
        {
            if (billets == null || billets.Parts == null) yield break;
            foreach (GameObject part in billets.Parts) yield return part;
        }

        private bool AnyBilletGrabbed()
        {
            foreach (GameObject part in Parts())
            {
                if (part == null) continue;
                if (ExternalGrab == part) return true;
                var grab = part.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected) return true;
                if (mouse != null && mouse.isActiveAndEnabled && mouse.Dragged != null && mouse.Dragged.IsChildOf(part.transform)) return true;
            }
            return false;
        }

        private void UpdateOnline()
        {
            bool ready = client == null || client.Available;
            if (ready) { _offlineSince = -1f; RobotOnline = true; return; }
            if (_offlineSince < 0f) _offlineSince = Time.time;
            RobotOnline = Time.time - _offlineSince < offlineGraceSeconds;
        }

        private void Evaluate()
        {
            UpdateOnline();

            var s = new GuideSignals
            {
                welcomeDone = WelcomeDone,
                placing = placement != null && placement.Phase == RobotPlacementController.PlacementPhase.Placing,
                handleGrabbed = placement != null && placement.HandleGrabbed,
                reachable = placement != null && placement.Markers != null ? placement.Markers.ReachableCount : 0,
                targetCount = placement != null && placement.Markers != null ? placement.Markers.Count : 0,
                doorsOpen = doors != null && doors.IsOpen,
                doorsMoving = doors != null && doors.IsMoving,
                billetSelected = selection != null && IsBillet(selection.SelectedObject),
                billetGrabbed = AnyBilletGrabbed(),
                billetInVise = vise != null && vise.PresentPart != null && !vise.PresentPartHeldByRobot,
                recording = recorder != null && recorder.IsRecording,
                clientPhase = client != null ? client.Phase : PickPlacePhase.Idle,
                solutionCount = client != null && client.SolutionIds != null ? client.SolutionIds.Count : 0,
                loadedByRobot = LoadedByRobot,
                partSettling = gravity != null && gravity.SettlingCount > 0,
                viseClosed = vise != null && vise.IsClosed,
                robotInJaws = vise != null && vise.RobotInJaws,
            };
            // A reposition starts the load over; a fresh plan before the job ran does too.
            if (s.placing) ClearLoad();
            else if (s.clientPhase == PickPlacePhase.Planning && !Machined) LoadedByRobot = false;
            // Bookkeeping for the unload: which billet, whether the listed solutions are its own, the job timer.
            if (LoadedByRobot && !Machined && s.billetInVise && vise != null && vise.PresentPart != null) MachinedBillet = vise.PresentPart;
            if (Machined && s.clientPhase == PickPlacePhase.Planning) _unloadPlanned = true;
            if (Step == GuideStep.Machining && !Machined && SecondsInStep >= machiningSeconds)
            {
                Machined = true;
                Debug.Log($"{LogTag} the job ran: unload next");
            }
            s.loadedByRobot = LoadedByRobot;
            s.machined = Machined;
            s.unloaded = UnloadedByRobot && !s.partSettling;
            s.billetOnConveyor = MachinedBillet != null && !s.billetGrabbed
                                 && GuidedRecording.OnConveyor(conveyorAnchor, MachinedBillet.transform.position, conveyorZoneRadius);
            if (Machined && !_unloadPlanned) s.solutionCount = 0; // the load's solutions are not the unload's
            if (recording != null)
                recording.Zone = Machined ? GuidedRecording.RecordingZone.Conveyor : GuidedRecording.RecordingZone.Vise;

            GuideStep step = DemoGuideSteps.Resolve(s);
            bool stepChanged = step != Step;
            if (stepChanged)
            {
                Debug.Log($"{LogTag} step {Step} -> {step}");
                Step = step;
                _stepStarted = Time.time;
                Notice = null;
            }
            Signals = s;
            ResolveTarget(step, s);

            Escalation = DemoGuideSteps.IsWaiting(step) ? 0
                : SecondsInStep >= helpAfterSeconds ? 2
                : SecondsInStep >= hintAfterSeconds ? 1 : 0;

            if (stepChanged) StepChanged?.Invoke(step);
            if (stepChanged || !SameText(s, _lastSignals) || RobotOnline != _lastOnline || Escalation != _lastEscalation || Notice != _lastNotice)
                Changed?.Invoke();
            _lastSignals = s;
            _lastOnline = RobotOnline;
            _lastEscalation = Escalation;
            _lastNotice = Notice;
        }

        // The fields Describe() varies its text on.
        private static bool SameText(in GuideSignals a, in GuideSignals b) =>
            a.handleGrabbed == b.handleGrabbed && a.reachable == b.reachable && a.doorsMoving == b.doorsMoving
            && a.billetGrabbed == b.billetGrabbed && a.recording == b.recording && a.robotInJaws == b.robotInJaws;

        private void ResolveTarget(GuideStep step, in GuideSignals s)
        {
            Target = DemoGuideSteps.TargetOf(step);
            TargetTransform = null;
            TargetDocument = null;
            TargetElementName = null;
            TargetBoxSize = Vector3.zero;
            TargetBoxLocalCentre = Vector3.zero;
            switch (Target)
            {
                case GuideTarget.PlacementHandle:
                    TargetTransform = placementHandle;
                    break;
                case GuideTarget.Billet:
                    GameObject billet = s.billetSelected && selection != null ? selection.SelectedObject : TopBillet();
                    TargetTransform = billet != null ? billet.transform : null;
                    break;
                case GuideTarget.MachinedBillet:
                    GameObject machined = MachinedBillet != null ? MachinedBillet : (vise != null ? vise.PresentPart : null);
                    TargetTransform = machined != null ? machined.transform : null;
                    break;
                case GuideTarget.Conveyor:
                    TargetTransform = conveyorAnchor;
                    if (conveyorAnchor != null)
                    {
                        TargetBoxSize = new Vector3(conveyorZoneRadius * 2f, 0.1f, conveyorZoneRadius * 2f);
                        TargetBoxLocalCentre = new Vector3(0f, 0.03f, 0f);
                    }
                    break;
                case GuideTarget.ReleaseButton:
                    SetButtonTarget(doorPanel != null ? doorPanel.GetComponent<UIDocument>() : null, "viseReleaseButton");
                    break;
                case GuideTarget.ViseWindow:
                    TargetTransform = vise != null ? vise.jawWindow : null;
                    if (vise != null)
                    {
                        // The opening between the jaw plates, from the parallels up past the plates.
                        TargetBoxSize = new Vector3(vise.jawWidth, vise.jawHeight * 2.5f, vise.openGap);
                        TargetBoxLocalCentre = new Vector3(0f, TargetBoxSize.y * 0.5f, 0f);
                    }
                    break;
                case GuideTarget.BeginButton:
                    SetButtonTarget(placementPanel != null ? placementPanel.GetComponent<UIDocument>() : null, "placementBeginButton");
                    break;
                case GuideTarget.ConfirmButton:
                    SetButtonTarget(placementPanel != null ? placementPanel.GetComponent<UIDocument>() : null, "placementConfirmButton");
                    break;
                case GuideTarget.DoorOpenButton:
                    SetButtonTarget(doorPanel != null ? doorPanel.GetComponent<UIDocument>() : null, "doorOpenButton");
                    break;
                case GuideTarget.DoorCloseButton:
                    SetButtonTarget(doorPanel != null ? doorPanel.GetComponent<UIDocument>() : null, "doorCloseButton");
                    break;
                case GuideTarget.ClampButton:
                    SetButtonTarget(doorPanel != null ? doorPanel.GetComponent<UIDocument>() : null, "viseClampButton");
                    break;
                case GuideTarget.ExecuteButton:
                    SetButtonTarget(dashboard != null ? dashboard.GetComponent<UIDocument>() : null, "mtcExecuteButton");
                    break;
            }
        }

        private void SetButtonTarget(UIDocument doc, string elementName)
        {
            if (doc == null || !doc.isActiveAndEnabled) return;
            TargetDocument = doc;
            TargetElementName = elementName;
            TargetTransform = doc.transform;
        }
    }
}
