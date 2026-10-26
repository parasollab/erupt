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
        [SerializeField, Tooltip("Is the robot's resting pose clear of the cell where it stands? A spot where the arm touches the machine plans nothing.")]
        private RobotClearanceCheck clearance;
        [SerializeField, Tooltip("Ring colour while the robot touches something where it stands.")]
        private Color blockedRingColor = new Color(1f, 0.25f, 0.2f);
        [SerializeField, Tooltip("The target inside the machine (the vise drop anchor). The free-space reach markers call it reachable from " +
                                 "further away than the planner can actually place through the door opening.")]
        private Transform insideMachineTarget;
        [SerializeField, Tooltip("Metres (horizontal, base axis to the inside-machine target) beyond which the load plan fails: " +
                                 "0.52 at the authored spot and 0.64 plan, 0.68 fails after 45 s (2026-10-26 sweep).")]
        private float insideMachineMaxReach = 0.65f;

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
        [SerializeField, Tooltip("Follow /joint_states while placing, so the arm stands as the real robot does and the clearance " +
                                 "check tests the pose the planner will start from (outside executions the Unity arm is otherwise " +
                                 "wherever the scene or the IK handle left it).")]
        private bool mirrorJointStatesWhilePlacing = true;
        [SerializeField, Tooltip("Hide the reachability shell and slice once the placement is confirmed; they come back " +
                                 "with the placement phase (the wrist menu can still show them meanwhile).")]
        private bool hideReachabilityWhenConfirmed = true;
        [SerializeField, Tooltip("Once the planner is reachable, detach whatever MoveIt still holds in the gripper from an earlier " +
                                 "session (a billet left there by a cancelled execution fails every later plan). " +
                                 "Everything this scene publishes is re-sent on Confirm anyway.")]
        private bool clearPlanningSceneOnStart = true;

        private Pose _defaultHandlePose, _startHandlePose;
        private Vector3 _robotLocalPos, _pedestalLocalPos;
        private Quaternion _robotLocalRot, _pedestalLocalRot;
        private Pose _lastAppliedHandlePose;
        private Renderer _ring;
        private MaterialPropertyBlock _ringBlock;
        private Color _ringColor = Color.white;
        private bool _ringTinted;
        private readonly List<CollisionObjectPublisher> _pausedPublishers = new List<CollisionObjectPublisher>();
        private bool _shellWasVisible = true, _sliceWasVisible, _wasConfirmedOnce;
        private Coroutine _rosMapResume, _startupClear;

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
        public RobotClearanceCheck Clearance => clearance;
        /// <summary>The robot, as it stands, touches nothing in the cell (true without a check).</summary>
        public bool RobotClear => clearance == null || clearance.Clear;
        /// <summary>What the robot touches, for the card and the panel ("fr3_link6 / vf2_body").</summary>
        public string RobotBlockedBy => clearance != null && !clearance.Clear ? $"{clearance.BlockingLink} / {clearance.BlockingObstacle}" : null;
        /// <summary>Horizontal distance from the base axis to the target inside the machine (0 without one).</summary>
        public float InsideMachineDistance
        {
            get
            {
                if (insideMachineTarget == null) return 0f;
                Transform b = baseAnchor != null ? baseAnchor : robotRoot.transform;
                Vector3 d = insideMachineTarget.position - b.position; d.y = 0f;
                return d.magnitude;
            }
        }
        /// <summary>The robot stands close enough to the machine for the planner to place inside it.</summary>
        public bool MachineInReach => insideMachineTarget == null || InsideMachineDistance <= insideMachineMaxReach;
        public float InsideMachineMaxReach => insideMachineMaxReach;
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
            _ring = handle.GetComponentInChildren<Renderer>(true);
            if (_ring != null && _ring.sharedMaterial != null && _ring.sharedMaterial.HasProperty("_BaseColor")) _ringColor = _ring.sharedMaterial.GetColor("_BaseColor");
            _ringBlock = new MaterialPropertyBlock();
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
                if (clearPlanningSceneOnStart) _startupClear = StartCoroutine(ClearPlanningSceneWhenReady());
            }
            else
                ApplyPhaseObjects();
        }

        // Waits for the planner (its action server comes up with move_group), then empties the gripper in
        // the planning scene once, before the first Confirm re-ADDs this scene's objects. Dropped if the user confirms first.
        private IEnumerator ClearPlanningSceneWhenReady()
        {
            float deadline = Time.time + 60f;
            while (Phase == PlacementPhase.Placing && Time.time < deadline && !(pickPlaceClient != null && pickPlaceClient.Available))
                yield return null;
            _startupClear = null;
            if (Phase != PlacementPhase.Placing) yield break;
            if (pickPlaceClient != null && !pickPlaceClient.Available)
            {
                Debug.LogWarning($"{LogTag} planner not reachable within 60 s; the gripper was not emptied in the planning scene at start");
                yield break;
            }
            var attachedListener = FindFirstObjectByType<AttachedCollisionObjectListener>();
            int released = attachedListener != null ? attachedListener.ReleaseAll() : 0;
            CollisionObjectPublisher.DetachAllFromRobot(ok => Debug.Log($"{LogTag} gripper emptied in the planning scene at start: {ok}; {released} stale attached object(s) released locally"));
        }

        private void Update()
        {
            if (Phase != PlacementPhase.Placing) return;
            UpdateRingTint();
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
            if (_startupClear != null) { StopCoroutine(_startupClear); _startupClear = null; }
            if (mirrorJointStatesWhilePlacing && trajectoryPlayer != null && trajectoryPlayer.IsMirroring) trajectoryPlayer.StopLiveMirror();
            Phase = PlacementPhase.Confirmed;
            _wasConfirmedOnce = true;
            ApplyPhaseObjects();
            _ringTinted = true; UpdateRingTint(); // reset to the plain ring for the next placement
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

        /// <summary>
        /// Back to the placement phase regardless of what is still going on (demo restart, which has
        /// already cancelled the goal and the recording); says why it would otherwise have refused.
        /// </summary>
        public void ForceReposition()
        {
            if (Phase == PlacementPhase.Placing) return;
            if (!CanReposition)
                Debug.LogWarning($"{LogTag} repositioning anyway: client busy={pickPlaceClient != null && pickPlaceClient.Busy}, " +
                                 $"recording={recorder != null && recorder.IsRecording}, mirroring={trajectoryPlayer != null && trajectoryPlayer.IsMirroring}");
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
            if (!RobotClear)
                list.Add($"The robot touches {RobotBlockedBy} where it stands: move it away");
            if (!MachineInReach)
                list.Add($"Too far from the machine to place inside it ({InsideMachineDistance:F2} m to the vise, max {insideMachineMaxReach:F2}): move closer to the doors");
            return list;
        }

        // The ring goes red while the robot's resting pose intersects the cell (the clearance check runs
        // only while placing, so the tint is refreshed here and reset when the phase ends).
        private void UpdateRingTint()
        {
            if (_ring == null) return;
            bool blocked = !RobotClear || !MachineInReach;
            if (blocked == _ringTinted) return;
            _ringTinted = blocked;
            _ring.GetPropertyBlock(_ringBlock);
            if (blocked) { _ringBlock.SetColor("_BaseColor", blockedRingColor); _ringBlock.SetColor("_Color", blockedRingColor); }
            else _ringBlock.Clear();
            _ring.SetPropertyBlock(_ringBlock);
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
            if (mirrorJointStatesWhilePlacing && trajectoryPlayer != null) trajectoryPlayer.StartLiveMirror();
            // Solutions were planned against the old base pose; they are meaningless once it moves.
            if (pickPlaceClient != null) pickPlaceClient.ForgetTask();
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
