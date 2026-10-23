using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Factory
{
    /// <summary>
    /// Turns "Start Recording / grab / drop / Stop Recording" into just "grab and drop": picking
    /// up a selected billet starts the pick-and-place recording, and letting go of it between
    /// the vise jaws stops the recording after a short settle, which sends the plan. A release
    /// anywhere else keeps the recording open (the guide tells the user to move it), and a
    /// re-grab within the settle time cancels the stop so the user can adjust. Only recordings
    /// this component started are stopped by it; the dashboard's manual buttons still work on
    /// their own. Grabs come from XRI (selectEntered/Exited on the billets) and, in the editor,
    /// from <see cref="DesktopMouseInteractor"/>'s drag events.
    /// </summary>
    public class GuidedRecording : MonoBehaviour
    {
        private const string LogTag = "[GuidedRecording]";

        [SerializeField] private PickPlaceTaskRecorder recorder;
        [SerializeField] private PickPlaceClient client;
        [SerializeField] private GrabbablePartSet billets;
        [SerializeField, Tooltip("Recording only runs once the robot placement is confirmed; empty = always.")]
        private RobotPlacementController placement;
        [SerializeField, Tooltip("Found in the scene when empty.")] private SelectionManager selection;
        [SerializeField, Tooltip("Editor stand-in for the hands; empty = XRI grabs only.")] private DesktopMouseInteractor mouse;
        public enum RecordingZone { Anywhere, Vise, Conveyor }

        [SerializeField, Tooltip("With the zone set to Vise, a release only stops the recording if the part rests between this vise's jaws.")]
        private MachineVise targetVise;
        [SerializeField, Tooltip("With the zone set to Conveyor, a release only stops the recording if the part rests near this anchor.")]
        private Transform conveyorAnchor;
        [SerializeField, Tooltip("Metres around the conveyor anchor (on the belt) that count as on the conveyor.")]
        private float conveyorZoneRadius = 0.25f;
        [SerializeField, Tooltip("Where a release has to happen to stop the recording; the guide switches it for the unload.")]
        private RecordingZone zone = RecordingZone.Vise;
        [SerializeField, Tooltip("Seconds after a release before the recording stops and the plan is sent (a re-grab cancels it).")]
        private float releaseSettleSeconds = 0.8f;
        [SerializeField] private bool autoRecord = true;

        public bool AutoRecord { get => autoRecord; set => autoRecord = value; }
        public RecordingZone Zone { get => zone; set => zone = value; }
        public Transform ConveyorAnchor => conveyorAnchor;

        /// <summary>The part's centre is over the belt near the anchor and roughly at belt height.</summary>
        public static bool OnConveyor(Transform anchor, Vector3 partPosition, float radius)
        {
            if (anchor == null) return false;
            Vector3 d = partPosition - anchor.position;
            float flat = new Vector2(d.x, d.z).magnitude;
            return flat <= radius && d.y > -0.05f && d.y < 0.15f;
        }

        public bool InTargetZone(GameObject part)
        {
            if (part == null) return false;
            switch (zone)
            {
                case RecordingZone.Vise:
                    if (targetVise == null) return true;
                    targetVise.ScanPresence();
                    return targetVise.PresentPart == part;
                case RecordingZone.Conveyor:
                    return conveyorAnchor == null || OnConveyor(conveyorAnchor, part.transform.position, conveyorZoneRadius);
                default:
                    return true;
            }
        }
        /// <summary>The running recording was started here (so a release may stop it).</summary>
        public bool StartedByGuide { get; private set; }
        public bool StopPending => _pendingStop != null;
        public GameObject WatchedPart { get; private set; }

        public event System.Action<GameObject> Started;
        public event System.Action<GameObject> Stopped;

        private readonly Dictionary<XRGrabInteractable, GameObject> _hooked = new Dictionary<XRGrabInteractable, GameObject>();
        private Coroutine _pendingStop, _hookParts;
        private bool _recorderHooked;

        private void Awake()
        {
            if (selection == null) selection = SelectionManager.Instance;
            if (mouse == null) mouse = FindFirstObjectByType<DesktopMouseInteractor>(FindObjectsInactive.Include);
        }

        private void OnEnable()
        {
            if (mouse != null)
            {
                mouse.DragStarted += OnMouseDragStarted;
                mouse.DragEnded += OnMouseDragEnded;
            }
            HookRecorder();
            _hookParts = StartCoroutine(HookPartsWhenReady());
        }

        // Idempotent: OnEnable may run before the recorder is assigned (components built at runtime).
        private void HookRecorder()
        {
            if (recorder == null || _recorderHooked) return;
            recorder.OnRecordingComplete += OnRecorderStopped;
            recorder.OnRecordingDiscarded += OnRecorderDiscarded;
            _recorderHooked = true;
        }

        private void OnDisable()
        {
            if (mouse != null)
            {
                mouse.DragStarted -= OnMouseDragStarted;
                mouse.DragEnded -= OnMouseDragEnded;
            }
            if (recorder != null && _recorderHooked)
            {
                recorder.OnRecordingComplete -= OnRecorderStopped;
                recorder.OnRecordingDiscarded -= OnRecorderDiscarded;
            }
            _recorderHooked = false;
            if (_hookParts != null) { StopCoroutine(_hookParts); _hookParts = null; }
            CancelPendingStop();
            foreach (var kv in _hooked)
            {
                if (kv.Key == null) continue;
                kv.Key.selectEntered.RemoveListener(OnXrGrabbed);
                kv.Key.selectExited.RemoveListener(OnXrReleased);
            }
            _hooked.Clear();
        }

        // GrabbablePartSet fills its list in Start; hook whatever it has once that ran.
        private IEnumerator HookPartsWhenReady()
        {
            yield return null;
            HookParts();
            _hookParts = null;
        }

        /// <summary>Subscribe to every billet's grab events (idempotent; call after parts are (re)created).</summary>
        public void HookParts()
        {
            if (billets == null || billets.Parts == null) return;
            foreach (GameObject part in billets.Parts)
            {
                if (part == null) continue;
                var grab = part.GetComponent<XRGrabInteractable>();
                if (grab == null || _hooked.ContainsKey(grab)) continue;
                grab.selectEntered.AddListener(OnXrGrabbed);
                grab.selectExited.AddListener(OnXrReleased);
                _hooked[grab] = part;
            }
        }

        public int HookedCount => _hooked.Count;

        private void OnXrGrabbed(SelectEnterEventArgs args)
        {
            var grab = args.interactableObject as XRGrabInteractable;
            if (grab != null && _hooked.TryGetValue(grab, out GameObject part)) NotifyGrabbed(part);
        }

        private void OnXrReleased(SelectExitEventArgs args)
        {
            var grab = args.interactableObject as XRGrabInteractable;
            if (grab == null || !_hooked.TryGetValue(grab, out GameObject part)) return;
            if (grab.isSelected) return; // the other hand still holds it
            NotifyReleased(part);
        }

        private void OnMouseDragStarted(GameObject dragged)
        {
            if (dragged != null && IsBillet(dragged)) NotifyGrabbed(dragged);
        }

        private void OnMouseDragEnded(GameObject dragged)
        {
            if (dragged != null && IsBillet(dragged)) NotifyReleased(dragged);
        }

        private bool IsBillet(GameObject go)
        {
            if (billets == null || billets.Parts == null) return false;
            foreach (GameObject part in billets.Parts) if (part == go) return true;
            return false;
        }

        /// <summary>A billet was picked up (XRI grab, mouse drag, or a probe).</summary>
        public void NotifyGrabbed(GameObject part)
        {
            CancelPendingStop();
            if (!autoRecord || recorder == null || part == null) return;
            if (recorder.IsRecording) return;
            if (client != null && client.Busy) return;
            if (placement != null && placement.Phase != RobotPlacementController.PlacementPhase.Confirmed) return;
            if (selection != null && selection.SelectedObject != part) return; // the recorder watches the selection
            HookRecorder();
            recorder.StartRecording();
            if (!recorder.IsRecording) return;
            StartedByGuide = true;
            WatchedPart = part;
            Debug.Log($"{LogTag} recording started by grabbing '{part.name}'");
            Started?.Invoke(part);
        }

        /// <summary>A billet was let go of; stops the recording after the settle if it sits in the target zone.</summary>
        public void NotifyReleased(GameObject part)
        {
            if (!StartedByGuide || recorder == null || !recorder.IsRecording) return;
            if (part != WatchedPart) return;
            // The recorder hears XRI releases itself and mouse releases through the rig; a release
            // reported straight to the guide (probes, other input backends) must count as well.
            recorder.NotifyObjectReleased(part);
            CancelPendingStop();
            _pendingStop = StartCoroutine(StopAfterSettle(part));
        }

        private IEnumerator StopAfterSettle(GameObject part)
        {
            yield return new WaitForSeconds(releaseSettleSeconds);
            _pendingStop = null;
            if (!recorder.IsRecording || !StartedByGuide) yield break;
            if (!InTargetZone(part))
            {
                Debug.Log($"{LogTag} '{part.name}' released outside the {zone} zone; still recording");
                yield break;
            }
            Debug.Log($"{LogTag} '{part.name}' set down in the target zone; stopping the recording");
            recorder.StopRecording();
        }

        private void CancelPendingStop()
        {
            if (_pendingStop == null) return;
            StopCoroutine(_pendingStop);
            _pendingStop = null;
        }

        private void OnRecorderStopped(string objectId) => Finish();
        private void OnRecorderDiscarded(string reason) => Finish();

        private void Finish()
        {
            if (!StartedByGuide) return;
            StartedByGuide = false;
            GameObject part = WatchedPart;
            WatchedPart = null;
            CancelPendingStop();
            Stopped?.Invoke(part);
        }
    }
}
