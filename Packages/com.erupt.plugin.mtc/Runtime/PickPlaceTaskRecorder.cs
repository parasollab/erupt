using Erupt.Ros;
using Erupt.Interaction;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using RosMessageTypes.BuiltinInterfaces;

public class PickPlaceTaskRecorder : MonoBehaviour
{
    [SerializeField] private SelectionHighlighter selectionManager;
    [SerializeField] private GameObject worldOrigin;

    [Header("Task delivery")]
    [Tooltip("When assigned, the captured task is sent as a /pick_place action goal " +
             "instead of being published on /pick_place_task. Leave empty to keep the topic.")]
    [SerializeField] private PickPlaceActionClient pickPlaceAction;

    [Tooltip("Action goals only: false plans without executing.")]
    [SerializeField] private bool executeOnServer = true;

    public bool IsRecording { get; private set; }

    // Invoked when a task is successfully captured; passes the object_id.
    // Event so multiple UIs (wrist menu + MTC dashboard) can both observe completion.
    public event System.Action<string> OnRecordingComplete;

    private IRosBus ros;
    private XRGrabInteractable watchedInteractable;
    private CollisionObjectPublisher watchedPublisher;

    // Pick pose — snapshotted when the object is first watched; restored on stop.
    private Vector3 pickWorldPos;
    private Quaternion pickWorldRot;

    // Set when the object has been released at least once during recording.
    private bool objectWasPlaced;

    private const string Topic = "/pick_place_task";

    void Start()
    {
        // Study telemetry subscribes to the single interaction sink rather than
        // tapping input directly. Guidelines Part 3.
        InteractionSampleBus.Sample += OnInteractionSample;

        ros = RosBus.Instance;
        ros.RegisterPublisher<PickPlaceTaskMsg>(Topic);
    }

    public void StartRecording()
    {
        if (IsRecording) return;

        IsRecording = true;
        objectWasPlaced = false;
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
            Transform origin = worldOrigin != null ? worldOrigin.transform : null;
            Vector3 worldPos = watchedPublisher.transform.position;
            Quaternion worldRot = watchedPublisher.transform.rotation;
            Vector3 relPos = origin != null ? origin.InverseTransformPoint(worldPos) : worldPos;
            Quaternion relRot = origin != null ? Quaternion.Inverse(origin.rotation) * worldRot : worldRot;

            SendPickPlaceTask(objectId, relPos, relRot);
            ResetWatchedObjectToPickPose();
            ClearWatchedObject();
            OnRecordingComplete?.Invoke(objectId);
        }
        else
        {
            ClearWatchedObject();
        }

        Debug.Log("PickPlaceTaskRecorder: recording stopped");
    }

    private void OnSelectionChanged(ISelectable current)

    {

        if (current != null && current.GameObject != null) OnObjectSelected(current.GameObject);

    }


    private void OnObjectSelected(GameObject obj)
    {
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
        pickWorldPos = pub.transform.position;
        pickWorldRot = pub.transform.rotation;
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
                new HeaderMsg(new TimeMsg(), "world"),
                new PoseMsg(rosPos, rosRot)
            )
        );

        ros.Publish(Topic, msg);
        Debug.Log($"PickPlaceTaskRecorder: published PickPlaceTask object_id={objectId} place_pose=({rosPos.x:F3}, {rosPos.y:F3}, {rosPos.z:F3})");
    }

    // The action reports acceptance, per-stage feedback and a result, so failures are
    // logged here rather than disappearing into a fire-and-forget topic.
    private async void SendPickPlaceGoal(string objectId, PointMsg rosPos, QuaternionMsg rosRot)
    {
        Debug.Log($"PickPlaceTaskRecorder: sending {pickPlaceAction.ActionName} goal object_id={objectId} place_pose=({rosPos.x:F3}, {rosPos.y:F3}, {rosPos.z:F3})");
        try
        {
            var result = await pickPlaceAction.SendGoalAsync(objectId, rosPos, rosRot, executeOnServer);
            Debug.Log($"PickPlaceTaskRecorder: {pickPlaceAction.ActionName} finished status={result.Status} " +
                      $"success={result.Result?.success} message='{result.Result?.message}'");
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"PickPlaceTaskRecorder: {pickPlaceAction.ActionName} goal failed — {exception.Message}");
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
    /// Recorded for study reporting; the /pick_place_task payload is unchanged.
    /// </summary>
    public InteractionSample LastSample { get; private set; }

    public bool HasSample { get; private set; }

    private void OnInteractionSample(InteractionSample sample)
    {
        LastSample = sample;
        HasSample = true;
    }
}
