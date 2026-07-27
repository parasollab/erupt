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
    [SerializeField] private SelectionManager selectionManager;
    [SerializeField] private GameObject worldOrigin;

    public bool IsRecording { get; private set; }

    // Invoked when a task is successfully captured; passes the object_id.
    public System.Action<string> OnRecordingComplete;

    private ROSConnection ros;
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
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<PickPlaceTaskMsg>(Topic);
    }

    public void StartRecording()
    {
        if (IsRecording) return;

        IsRecording = true;
        objectWasPlaced = false;
        selectionManager.OnObjectSelected += OnObjectSelected;

        // Watch whatever is already selected
        if (selectionManager.SelectedObject != null)
            WatchObject(selectionManager.SelectedObject);

        Debug.Log("PickPlaceTaskRecorder: recording started");
    }

    public void StopRecording()
    {
        if (!IsRecording) return;

        IsRecording = false;
        selectionManager.OnObjectSelected -= OnObjectSelected;

        if (objectWasPlaced && watchedPublisher != null)
        {
            string objectId = watchedPublisher.objectId;
            Transform origin = worldOrigin != null ? worldOrigin.transform : null;
            Vector3 worldPos = watchedPublisher.transform.position;
            Quaternion worldRot = watchedPublisher.transform.rotation;
            Vector3 relPos = origin != null ? origin.InverseTransformPoint(worldPos) : worldPos;
            Quaternion relRot = origin != null ? Quaternion.Inverse(origin.rotation) * worldRot : worldRot;

            PublishPickPlaceTask(objectId, relPos, relRot);
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

    private void PublishPickPlaceTask(string objectId, Vector3 relPos, Quaternion relRot)
    {
        PointMsg rosPos = RosUnityConversion.UnityToRosPosition(relPos);
        QuaternionMsg rosRot = RosUnityConversion.UnityToRosQuaternion(relRot);

        var msg = new PickPlaceTaskMsg(
            objectId,
            new PoseStampedMsg(
                RosMessageCompatibility.CreateHeader("world"),
                new PoseMsg(rosPos, rosRot)
            )
        );

        ros.Publish(Topic, msg);
        Debug.Log($"PickPlaceTaskRecorder: published PickPlaceTask object_id={objectId} place_pose=({rosPos.x:F3}, {rosPos.y:F3}, {rosPos.z:F3})");
    }

    void OnDestroy()
    {
        if (IsRecording)
            StopRecording();
    }
}
