using Erupt.Ros;
using Erupt.Interaction;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using RosMessageTypes.BuiltinInterfaces;
using Erupt.Environment;
using Erupt.Plugins.MoveIt;

namespace Erupt.Plugins.Mtc
{
    public class PickPlaceTaskRecorder : MonoBehaviour
    {
        [SerializeField] private SelectionHighlighter selectionManager;
        [SerializeField] private GameObject worldOrigin;

        [Header("Task delivery")]
        [Tooltip("The captured task is sent as a /pick_place action goal through this client " +
                 "(sibling component by default). Only without one is it published on the legacy /pick_place_task topic.")]
        [SerializeField] private PickPlaceClient pickPlaceAction;

        [Tooltip("false plans only, so the solutions can be browsed and one executed from the MTC tab. " +
                 "true plans and executes in one goal, skipping the browser.")]
        [SerializeField] private bool executeOnServer = false;

        public bool IsRecording { get; private set; }

        // Invoked when a task is successfully captured; passes the object_id.
        // Event so multiple UIs (wrist menu + MTC dashboard) can both observe completion.
        public event System.Action<string> OnRecordingComplete;

        // Invoked with the reason when a recording ends without a task being sent (stopped with
        // nothing placed, or cancelled), so a UI can say why instead of silently going back to idle.
        public event System.Action<string> OnRecordingDiscarded;

        // Invoked with the captured task before it is sent: object_id and the demonstrated place
        // pose in world space, so a listener can repeat the task for later objects.
        public event System.Action<string, Vector3, Quaternion> OnTaskCaptured;

        private IRosBus ros;
        private Erupt.Robot.RobotInstance robot;
        private bool started;
        private XRGrabInteractable watchedInteractable;
        private CollisionObjectPublisher watchedPublisher;

        // Pick pose — snapshotted when the object is first watched; restored on stop.
        private Vector3 pickWorldPos;
        private Quaternion pickWorldRot;
        // Pick poses of every object watched during this recording. An object selected again after
        // it was carried (the selection was cleared, so the user selects it again to grip it) keeps
        // the pick pose it started from: re-capturing it where it hovers would reset the object to
        // mid-air afterwards while MoveIt still has it at its real pick pose.
        private readonly Dictionary<GameObject, (Vector3 pos, Quaternion rot)> pickPoses = new Dictionary<GameObject, (Vector3, Quaternion)>();

        // Set when the object has been released at least once during recording.
        private bool objectWasPlaced;

        // Relative: the task goes to the server in the namespace of the robot whose bus this is.
        private const string Topic = "pick_place_task";

        /// <summary>
        /// The robot the recorded task is for. The place pose is expressed in its planning frame
        /// (without a robot: relative to the world origin this component was given).
        /// </summary>
        public void SetRobot(Erupt.Robot.RobotInstance target) => robot = target;

        /// <summary>Send captured tasks through this client (the MTC plugin passes the robot's own).</summary>
        public void SetPickPlaceClient(PickPlaceClient client) => pickPlaceAction = client;

        /// <summary>Let go of the bus, the robot and the client, so the recorder can serve another robot.</summary>
        public void Release()
        {
            ros = null;
            robot = null;
            pickPlaceAction = null;
        }

        // The Unity pose of the frame the place pose is expressed in.
        private Transform PlaceFrame =>
            robot != null && robot.PlanningFrame != null ? robot.PlanningFrame
            : worldOrigin != null ? worldOrigin.transform : null;

        private string PlaceFrameId => robot != null ? robot.PlanningFrameId : "world";

        void Start()
        {
            // The recorder keeps the latest sample from the single interaction sink rather
            // than tapping input directly. Guidelines Part 3.
            InteractionSampleBus.Sample += OnInteractionSample;

            if (pickPlaceAction == null) pickPlaceAction = GetComponent<PickPlaceClient>();
            started = true;
            if (ros != null) RegisterTaskPublisher();
        }

        /// <summary>
        /// Hands over the ROS transport (the MTC plugin does it at registration). Only the legacy
        /// task topic needs it; without a bus a task captured for that topic is not published.
        /// </summary>
        public void Initialise(IRosBus bus)
        {
            if (bus == null) throw new System.ArgumentNullException(nameof(bus));
            if (ros == bus) return;
            if (ros != null) throw new System.InvalidOperationException("PickPlaceTaskRecorder already has a ROS bus.");
            ros = bus;
            if (started) RegisterTaskPublisher();
        }

        private void RegisterTaskPublisher()
        {
            if (pickPlaceAction == null) ros.RegisterPublisher<PickPlaceTaskMsg>(Topic);
        }

        public void StartRecording()
        {
            if (IsRecording) return;

            IsRecording = true;
            objectWasPlaced = false;
            pickPoses.Clear();
            selectionManager.Service.SelectionChanged += OnSelectionChanged;

            // Watch whatever is already selected
            if (selectionManager.SelectedObject != null)
                WatchObject(selectionManager.SelectedObject);

            Debug.Log("PickPlaceTaskRecorder: recording started");
        }

        public void StopRecording()
        {
            if (!IsRecording) return;

            IsRecording = false;
            selectionManager.Service.SelectionChanged -= OnSelectionChanged;

            if (objectWasPlaced && watchedPublisher != null)
            {
                string objectId = watchedPublisher.objectId;
                Transform origin = PlaceFrame;
                Vector3 worldPos = watchedPublisher.transform.position;
                Quaternion worldRot = watchedPublisher.transform.rotation;
                Vector3 relPos = origin != null ? origin.InverseTransformPoint(worldPos) : worldPos;
                Quaternion relRot = origin != null ? Quaternion.Inverse(origin.rotation) * worldRot : worldRot;

                OnTaskCaptured?.Invoke(objectId, worldPos, worldRot);
                SendPickPlaceTask(objectId, relPos, relRot);
                ResetWatchedObjectToPickPose();
                ClearWatchedObject();
                pickPoses.Clear();
                OnRecordingComplete?.Invoke(objectId);
            }
            else
            {
                string reason = watchedPublisher != null
                    ? $"'{watchedPublisher.objectId}' was never released"
                    : "no object was selected";
                ClearWatchedObject();
                pickPoses.Clear();
                Debug.LogWarning($"PickPlaceTaskRecorder: nothing sent — {reason}");
                OnRecordingDiscarded?.Invoke(reason);
            }

            Debug.Log("PickPlaceTaskRecorder: recording stopped");
        }

        /// <summary>
        /// Stops the recording without sending anything: the watched object goes back to its pick
        /// pose and listeners hear <see cref="OnRecordingDiscarded"/>.
        /// </summary>
        public void CancelRecording()
        {
            if (!IsRecording) return;

            IsRecording = false;
            selectionManager.Service.SelectionChanged -= OnSelectionChanged;
            ResetWatchedObjectToPickPose();
            ClearWatchedObject();
            pickPoses.Clear();
            Debug.Log("PickPlaceTaskRecorder: recording cancelled");
            OnRecordingDiscarded?.Invoke("recording cancelled");
        }

        private void OnSelectionChanged(ISelectable current)

        {

            if (current != null && current.GameObject != null) OnObjectSelected(current.GameObject);

        }


        private void OnObjectSelected(GameObject obj)
        {
            if (obj == null) return;
            if (watchedPublisher != null && obj == watchedPublisher.gameObject) return; // re-selected: keep watching from the same pick pose
            ClearWatchedObject();
            WatchObject(obj);
        }

        private void WatchObject(GameObject obj)
        {
            var gi = obj.GetComponent<XRGrabInteractable>();
            var pub = obj.GetComponent<CollisionObjectPublisher>();
            if (gi == null || pub == null) return;

            watchedInteractable = gi;
            watchedPublisher = pub;
            if (pickPoses.TryGetValue(obj, out var known))
            {
                // Watched earlier in this recording: it may have been carried since, so its pick
                // pose is the one it started from.
                pickWorldPos = known.pos;
                pickWorldRot = known.rot;
            }
            else
            {
                pickWorldPos = pub.transform.position;
                pickWorldRot = pub.transform.rotation;
                pickPoses[obj] = (pickWorldPos, pickWorldRot);
            }
            watchedPublisher.pausePublishing = true;
            gi.selectExited.AddListener(OnGrabReleased);
        }

        private void ResetWatchedObjectToPickPose()
        {
            if (watchedPublisher == null) return;

            watchedPublisher.transform.SetPositionAndRotation(pickWorldPos, pickWorldRot);
            Debug.Log($"PickPlaceTaskRecorder: reset '{watchedPublisher.objectId}' to pick pose");
        }

        private void ClearWatchedObject()
        {
            if (watchedInteractable != null)
            {
                watchedInteractable.selectExited.RemoveListener(OnGrabReleased);
                watchedInteractable = null;
            }
            if (watchedPublisher != null)
            {
                watchedPublisher.pausePublishing = false;
                watchedPublisher = null;
            }
        }

        private void OnGrabReleased(SelectExitEventArgs args)
        {
            if (!IsRecording || watchedPublisher == null) return;

            objectWasPlaced = true;
            Debug.Log($"PickPlaceTaskRecorder: object released — pose will be read from transform when recording stops");
        }

        /// <summary>
        /// A stand-in for the XRI release: something other than an XRI grab (a desktop mouse drag,
        /// a script) has put the watched object down. Counts exactly like a hand letting go, so
        /// stopping the recording sends the pose the object is left in.
        /// </summary>
        public void NotifyObjectReleased(GameObject obj)
        {
            if (!IsRecording || watchedPublisher == null || obj == null) return;
            if (obj != watchedPublisher.gameObject && !obj.transform.IsChildOf(watchedPublisher.transform)) return;
            objectWasPlaced = true;
            Debug.Log("PickPlaceTaskRecorder: object put down — pose will be read from transform when recording stops");
        }

        private void SendPickPlaceTask(string objectId, Vector3 relPos, Quaternion relRot)
        {
            PointMsg rosPos = RosUnityConversion.UnityToRosPosition(relPos);
            QuaternionMsg rosRot = RosUnityConversion.UnityToRosQuaternion(relRot);

            if (pickPlaceAction != null)
            {
                SendPickPlaceGoal(objectId, rosPos, rosRot);
                return;
            }

            var msg = new PickPlaceTaskMsg(
                objectId,
                new PoseStampedMsg(
                    new HeaderMsg(new TimeMsg(), PlaceFrameId),
                    new PoseMsg(rosPos, rosRot)
                )
            );

            if (ros == null)
            {
                Debug.LogWarning($"PickPlaceTaskRecorder: no ROS bus yet; the task for object_id={objectId} was not published.", this);
                return;
            }
            ros.Publish(Topic, msg);
            Debug.Log($"PickPlaceTaskRecorder: published PickPlaceTask object_id={objectId} place_pose=({rosPos.x:F3}, {rosPos.y:F3}, {rosPos.z:F3})");
        }

        // The action reports acceptance, per-stage feedback and a result, so failures are
        // logged here rather than disappearing into a fire-and-forget topic. The client also
        // puts them on its status line (rejected while busy, aborted, cancelled).
        private async void SendPickPlaceGoal(string objectId, PointMsg rosPos, QuaternionMsg rosRot)
        {
            Debug.Log($"PickPlaceTaskRecorder: sending {pickPlaceAction.ActionName} goal object_id={objectId} place_pose=({rosPos.x:F3}, {rosPos.y:F3}, {rosPos.z:F3})");
            try
            {
                PoseStampedMsg placePose = pickPlaceAction.PlacePose(rosPos, rosRot);
                var result = executeOnServer
                    ? await pickPlaceAction.PlanAndExecuteAsync(objectId, placePose)
                    : await pickPlaceAction.PlanAsync(objectId, placePose);
                Debug.Log($"PickPlaceTaskRecorder: {pickPlaceAction.ActionName} finished status={result.Status} " +
                          $"success={result.Result?.success} message='{result.Result?.message}'");
            }
            catch (System.Exception exception)
            {
                // Rejected-while-busy and lost connections are expected outcomes, already on the status line.
                Debug.LogWarning($"PickPlaceTaskRecorder: {pickPlaceAction.ActionName} goal failed — {exception.Message}");
            }
        }

        void OnDestroy()
        {
            if (IsRecording)
                StopRecording();

            InteractionSampleBus.Sample -= OnInteractionSample;
        }

        /// <summary>
        /// Latest routed manipulation sample, carrying modality and tracking confidence.
        /// Kept for callers that report on the capture; it is not part of the /pick_place_task payload.
        /// </summary>
        public InteractionSample LastSample { get; private set; }

        public bool HasSample { get; private set; }

        private void OnInteractionSample(InteractionSample sample)
        {
            LastSample = sample;
            HasSample = true;
        }
    }
}
