using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Factory
{
    /// <summary>
    /// First phase of the factory demo: the user drags a floor handle and the robot (an
    /// articulation chain, moved with TeleportRoot) and its pedestal follow it, with the
    /// reachability volume riding on the robot base. Confirm freezes the pose and opens the
    /// pick-and-place phase.
    ///
    /// Everything Unity publishes to MoveIt is expressed relative to the robot base (the
    /// CollisionObjectPublishers' worldOrigin), so a base move changes the base-relative pose of
    /// every room-fixed object without any of them noticing. While placing, the publishers are
    /// paused; Confirm makes them all re-send (the VF-2 meshes are large, so this happens once
    /// per confirm, never while dragging). The visualizer's ROS map (obstacle-carved, in the
    /// base frame) is held off for the same reason and re-requested after the re-sends.
    /// </summary>
    public class RobotPlacementController : MonoBehaviour
    {
        public enum PlacementPhase { Placing, Confirmed }

        private const string LogTag = "[RobotPlacement]";

        [Header("Robot")]
        [SerializeField, Tooltip("Robot root (fr3). Its RobotReachProfile base anchor is the MoveIt world origin.")]
        private GameObject robotRoot;
        [SerializeField, Tooltip("Pedestal the robot stands on; moved with it (it is not a MoveIt object).")]
        private Transform pedestal;
        [SerializeField, Tooltip("World origin the publishers use (BaseTransform under the robot). Publishers under it ride with the robot.")]
        private Transform baseAnchor;

        [Header("Handle")]
        [SerializeField, Tooltip("Grabbable floor handle. The robot and pedestal keep the offset they have from it at Awake.")]
        private Transform handle;
        [SerializeField] private XRGrabInteractable handleInteractable;
        [SerializeField, Tooltip("Where the robot starts the placement phase, as a world x/z offset from its authored (ideal) pose, " +
                                 "so the user has to bring it into place. Zero = start at the authored pose.")]
        private Vector2 startOffset = Vector2.zero;
        [SerializeField, Tooltip("Yaw (degrees) the robot starts with, relative to its authored pose.")]
        private float startYawDegrees = 0f;
        [SerializeField, Tooltip("World x/z rectangle the handle may be nudged into (the grab transformer clamps grabs separately).")]
        private Vector2 cellBoundsMin = new Vector2(-1.2f, -0.2f);
        [SerializeField] private Vector2 cellBoundsMax = new Vector2(1.2f, 1.03f);
        [SerializeField] private float nudgeStepMetres = 0.05f;
        [SerializeField] private float rotateStepDegrees = 15f;
        [SerializeField, Tooltip("Handle movement below this (m / deg) is not applied to the robot.")]
        private float applyPositionEpsilon = 0.001f;
        [SerializeField] private float applyAngleEpsilon = 0.1f;

        [Header("Feedback")]
        [SerializeField] private ReachabilityVolumeVisualizer reachability;
        [SerializeField] private ReachTargetMarkerSet markers;

        [Header("Pick-and-place stack")]
        [SerializeField] private PickPlaceClient pickPlaceClient;
        [SerializeField] private MTCTrajectoryPlayer trajectoryPlayer;
        [SerializeField] private PickPlaceTaskRecorder recorder;
        [SerializeField] private SpawnGhosts ghosts;
        [SerializeField, Tooltip("Wrist menus whose MTC / record buttons are hidden until the placement is confirmed.")]
        private WristMenuController[] wristMenus = new WristMenuController[0];
        [SerializeField, Tooltip("Active only once the placement is confirmed (MTC dashboard, door menu).")]
        private GameObject[] gatedObjects = new GameObject[0];
        [SerializeField, Tooltip("Active only while placing (handle, reach markers).")]
        private GameObject[] placementObjects = new GameObject[0];

        [Header("Behaviour")]
        [SerializeField, Tooltip("Pause every CollisionObjectPublisher while placing; all re-send on confirm.")]
        private bool pausePublishersWhilePlacing = true;
        [SerializeField, Tooltip("Seconds after confirm before the obstacle-aware ROS map is requested again, so the server has seen the re-sent scene.")]
        private float rosMapRequestDelay = 1.5f;
        [SerializeField, Tooltip("Start the scene in the placement phase.")]
        private bool startInPlacement = true;
        [SerializeField, Tooltip("Hide the reachability shell and slice once the placement is confirmed; they come back " +
                                 "with the placement phase (the wrist menu can still show them meanwhile).")]
        private bool hideReachabilityWhenConfirmed = true;

        private Pose _defaultHandlePose, _startHandlePose;
        private Vector3 _robotLocalPos, _pedestalLocalPos;
        private Quaternion _robotLocalRot, _pedestalLocalRot;
        private Pose _lastAppliedHandlePose;
        private readonly List<CollisionObjectPublisher> _pausedPublishers = new List<CollisionObjectPublisher>();
        private bool _shellWasVisible = true, _sliceWasVisible, _wasConfirmedOnce;
        private Coroutine _rosMapResume;

        public PlacementPhase Phase { get; private set; } = PlacementPhase.Confirmed;
        public event Action<PlacementPhase> PhaseChanged;
        public event Action BasePoseApplied;

        public bool HandleGrabbed => handleInteractable != null && handleInteractable.isSelected;
        public bool CanConfirm => Phase == PlacementPhase.Placing && !HandleGrabbed;
        public bool CanReposition => Phase == PlacementPhase.Confirmed
                                     && !(pickPlaceClient != null && pickPlaceClient.Busy)
                                     && !(recorder != null && recorder.IsRecording)
                                     && !(trajectoryPlayer != null && trajectoryPlayer.IsMirroring);
        public PickPlaceClient PickPlaceClient => pickPlaceClient;
        public ReachTargetMarkerSet Markers => markers;
        public ReachabilityVolumeVisualizer Reachability => reachability;
        public Transform Handle => handle;
        /// <summary>Authored (ideal) handle pose; the cell layout the validator checks.</summary>
        public Pose DefaultHandlePose => _defaultHandlePose;
        /// <summary>Where the robot is put when the placement phase first opens ("Start over" returns here).</summary>
        public Pose StartHandlePose => _startHandlePose;

        private void Awake()
        {
            if (handle == null || robotRoot == null)
            {
                Debug.LogError($"{LogTag} handle and robotRoot must be assigned; disabled.", this);
                enabled = false;
                return;
            }
            _defaultHandlePose = new Pose(handle.position, handle.rotation);
            _robotLocalPos = handle.InverseTransformPoint(robotRoot.transform.position);
            _robotLocalRot = Quaternion.Inverse(handle.rotation) * robotRoot.transform.rotation;
            if (pedestal != null)
            {
                _pedestalLocalPos = handle.InverseTransformPoint(pedestal.position);
                _pedestalLocalRot = Quaternion.Inverse(handle.rotation) * pedestal.rotation;
            }
            _lastAppliedHandlePose = _defaultHandlePose;
            _startHandlePose = new Pose(_defaultHandlePose.position + new Vector3(startOffset.x, 0f, startOffset.y),
                                        Quaternion.Euler(0f, _defaultHandlePose.rotation.eulerAngles.y + startYawDegrees, 0f));
        }

        private void Start()
        {
            if (startInPlacement)
            {
                EnterPlacing();
                // The scene is authored with the robot at its ideal spot; the user has to find it.
                handle.SetPositionAndRotation(_startHandlePose.position, _startHandlePose.rotation);
                ApplyHandlePose(SanitisedHandlePose());
                if (markers != null) markers.Refresh();
            }
            else
                ApplyPhaseObjects();
        }

        private void Update()
        {
            if (Phase != PlacementPhase.Placing) return;
            Pose current = SanitisedHandlePose();
            if (Vector3.Distance(current.position, _lastAppliedHandlePose.position) < applyPositionEpsilon
                && Quaternion.Angle(current.rotation, _lastAppliedHandlePose.rotation) < applyAngleEpsilon)
                return;
            ApplyHandlePose(current);
        }

        // ---- public API ------------------------------------------------------------------

        /// <summary>Freezes the robot where it is, re-sends the planning scene and opens the pick-and-place phase.</summary>
        public void Confirm()
        {
            if (!CanConfirm) return;
            ApplyHandlePose(SanitisedHandlePose());
            Phase = PlacementPhase.Confirmed;
            _wasConfirmedOnce = true;
            ApplyPhaseObjects();
            RepublishScene();
            if (hideReachabilityWhenConfirmed && reachability != null)
            {
                _shellWasVisible = reachability.ShellVisible;
                _sliceWasVisible = reachability.SliceVisible;
                reachability.SetVisible(false);
            }
            foreach (WristMenuController w in wristMenus) if (w != null) w.SetMtcControlsAvailable(true);
            if (_rosMapResume != null) StopCoroutine(_rosMapResume);
            _rosMapResume = StartCoroutine(ResumeRosMapAfterDelay());
            Debug.Log($"{LogTag} confirmed: base at {BaseDescription()}");
            PhaseChanged?.Invoke(Phase);
        }

        /// <summary>Back to the placement phase (only when no plan is recording or executing).</summary>
        public void BeginReposition()
        {
            if (!CanReposition) return;
            EnterPlacing();
        }

        /// <summary>Puts the robot back where the placement phase started (not at the ideal spot).</summary>
        public void ResetToStart()
        {
            if (Phase != PlacementPhase.Placing || HandleGrabbed) return;
            handle.SetPositionAndRotation(_startHandlePose.position, _startHandlePose.rotation);
        }

        /// <summary>Moves the handle by a world-axis offset (metres) on the floor.</summary>
        public void Nudge(float worldDx, float worldDz)
        {
            if (Phase != PlacementPhase.Placing || HandleGrabbed) return;
            Vector3 p = handle.position + new Vector3(worldDx, 0f, worldDz);
            p.x = Mathf.Clamp(p.x, cellBoundsMin.x, cellBoundsMax.x);
            p.z = Mathf.Clamp(p.z, cellBoundsMin.y, cellBoundsMax.y);
            handle.position = p;
        }

        public void NudgeStep(float stepsX, float stepsZ) => Nudge(stepsX * nudgeStepMetres, stepsZ * nudgeStepMetres);

        /// <summary>Turns the handle about the world up axis (degrees, Unity sign: positive is clockwise seen from above).</summary>
        public void Rotate(float deltaYawDegrees)
        {
            if (Phase != PlacementPhase.Placing || HandleGrabbed) return;
            handle.rotation = Quaternion.Euler(0f, handle.rotation.eulerAngles.y + deltaYawDegrees, 0f);
        }

        public void RotateStep(float steps) => Rotate(steps * rotateStepDegrees);

        /// <summary>Human-readable reasons the current placement may be a poor one. Never blocks Confirm.</summary>
        public List<string> Warnings()
        {
            var list = new List<string>();
            if (markers == null) return list;
            if (reachability == null || !reachability.HasMap)
            {
                list.Add("No reachability map loaded");
                return list;
            }
            var missing = new List<string>();
            for (int i = 0; i < markers.Count; i++)
                if (!markers.IsReachable(i)) missing.Add(markers.TargetLabel(i));
            if (missing.Count > 0)
                list.Add($"Not reachable top-down: {string.Join(", ", missing)}");
            return list;
        }

        public string BaseDescription()
        {
            Transform b = baseAnchor != null ? baseAnchor : robotRoot.transform;
            Vector3 p = b.position;
            return $"({p.x:F2}, {p.y:F2}, {p.z:F2}) yaw {handle.rotation.eulerAngles.y:F0} deg";
        }

        // ---- phases ----------------------------------------------------------------------

        private void EnterPlacing()
        {
            Phase = PlacementPhase.Placing;
            if (_rosMapResume != null) { StopCoroutine(_rosMapResume); _rosMapResume = null; }
            if (trajectoryPlayer != null)
            {
                if (trajectoryPlayer.IsPlaying) trajectoryPlayer.Stop();
                if (trajectoryPlayer.IsMirroring) trajectoryPlayer.StopLiveMirror();
            }
            if (ghosts != null) ghosts.ClearGhosts();
            if (pausePublishersWhilePlacing) PausePublishers();
            if (reachability != null)
            {
                reachability.SetRosMapEnabled(false);
                if (hideReachabilityWhenConfirmed && _wasConfirmedOnce)
                {
                    reachability.SetShellVisible(_shellWasVisible);
                    reachability.SetSliceVisible(_sliceWasVisible);
                }
            }
            ApplyPhaseObjects();
            foreach (WristMenuController w in wristMenus) if (w != null) w.SetMtcControlsAvailable(false);
            if (markers != null) markers.Refresh();
            Debug.Log($"{LogTag} placing: base at {BaseDescription()}; {_pausedPublishers.Count} publisher(s) paused");
            PhaseChanged?.Invoke(Phase);
        }

        private void ApplyPhaseObjects()
        {
            bool placing = Phase == PlacementPhase.Placing;
            foreach (GameObject go in placementObjects) if (go != null && go.activeSelf != placing) go.SetActive(placing);
            foreach (GameObject go in gatedObjects) if (go != null && go.activeSelf == placing) go.SetActive(!placing);
        }

        private Pose SanitisedHandlePose()
        {
            Vector3 p = handle.position;
            p.y = _defaultHandlePose.position.y;
            Quaternion r = Quaternion.Euler(0f, handle.rotation.eulerAngles.y, 0f);
            return new Pose(p, r);
        }

        private void ApplyHandlePose(Pose handlePose)
        {
            Matrix4x4 m = Matrix4x4.TRS(handlePose.position, handlePose.rotation, Vector3.one);
            ArticulationRootMover.MoveRoot(robotRoot, m.MultiplyPoint3x4(_robotLocalPos), handlePose.rotation * _robotLocalRot);
            if (pedestal != null)
                pedestal.SetPositionAndRotation(m.MultiplyPoint3x4(_pedestalLocalPos), handlePose.rotation * _pedestalLocalRot);
            _lastAppliedHandlePose = handlePose;
            BasePoseApplied?.Invoke();
        }

        // ---- planning scene --------------------------------------------------------------

        private void PausePublishers()
        {
            _pausedPublishers.Clear();
            foreach (CollisionObjectPublisher pub in FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pub.pausePublishing) continue;
                pub.pausePublishing = true;
                _pausedPublishers.Add(pub);
            }
        }

        // Same rule as RobotBaseTFPublisher.TriggerRepublish, run once: objects that stay in the
        // room get a fresh ADD at their new base-relative pose; objects parented under the base
        // moved with it, so MoveIt already has their pose and they are only told the move is known.
        private void RepublishScene()
        {
            foreach (CollisionObjectPublisher pub in _pausedPublishers)
                if (pub != null) pub.pausePublishing = false;
            _pausedPublishers.Clear();

            int republished = 0, riding = 0;
            foreach (CollisionObjectPublisher pub in FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pub.pausePublishing) continue;
                if (baseAnchor != null && pub.transform.IsChildOf(baseAnchor))
                {
                    pub.MarkTransformAsPublished();
                    riding++;
                    continue;
                }
                pub.ForceRepublish();
                republished++;
            }
            var bridge = FindFirstObjectByType<SceneAnchorCollisionBridge>();
            if (bridge != null) bridge.RepublishAll();
            Debug.Log($"{LogTag} planning scene re-sent: {republished} room object(s), {riding} riding with the robot");
        }

        private IEnumerator ResumeRosMapAfterDelay()
        {
            yield return new WaitForSeconds(rosMapRequestDelay);
            _rosMapResume = null;
            if (Phase == PlacementPhase.Confirmed && reachability != null)
                reachability.SetRosMapEnabled(true);
        }
    }
}
