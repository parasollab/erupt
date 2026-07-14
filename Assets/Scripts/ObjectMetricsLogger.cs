using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.StudyInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.BuiltinInterfaces;

// Publishes ObjectEvent messages to /study/object_events for per-object interaction metrics
// (creation, deletion, grab start/end, edit operations). Callers just call LogEvent(...);
// this fills in the shared participant/task/scene context the same way StudyLogger.cs does.
// Self-bootstraps like StudyLogger/RosConnectionBootstrapper, so it doesn't need to be placed
// in any particular scene.
public class ObjectMetricsLogger : MonoBehaviour
{
    public static ObjectMetricsLogger Instance { get; private set; }

    private const string Topic = "/study/object_events";

    private ROSConnection ros;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        GameObject go = new GameObject(nameof(ObjectMetricsLogger));
        go.AddComponent<ObjectMetricsLogger>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        ros = ROSConnection.GetOrCreateInstance();
        // See StudyLogger.cs's Awake() for why this explicit call is needed: ObjectEventMsg's own
        // Register() defaults to AfterSceneLoad, which runs after this (BeforeSceneLoad).
        ObjectEventMsg.Register();
        ros.RegisterPublisher<ObjectEventMsg>(Topic);
    }

    public void LogEvent(string eventType, string objectId, Vector3? relativePos = null, Quaternion? relativeRot = null, string details = "")
    {
        string participantId = StudyController.Instance != null ? StudyController.Instance.ParticipantId : "unknown";
        int taskIndex = StudyController.Instance != null ? StudyController.Instance.TaskIndex : -1;
        int sceneIndex = StudyController.Instance != null ? StudyController.Instance.SceneIndexInTask : -1;
        string sceneName = SceneManager.GetActiveScene().name;

        PoseMsg pose = new PoseMsg(
            relativePos.HasValue ? RosUnityConversion.UnityToRosPosition(relativePos.Value) : new PointMsg(0, 0, 0),
            relativeRot.HasValue ? RosUnityConversion.UnityToRosQuaternion(relativeRot.Value) : new QuaternionMsg(0, 0, 0, 1));

        ObjectEventMsg msg = new ObjectEventMsg(
            participantId, eventType, objectId, sceneName, taskIndex, sceneIndex, pose, details ?? "", NowStamp());
        ros.Publish(Topic, msg);
        Debug.Log($"ObjectMetricsLogger: [{eventType}] object={objectId} details={details}");
    }

    private static TimeMsg NowStamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long totalSec = now.ToUnixTimeSeconds();
        long nanosec = (now.ToUnixTimeMilliseconds() - totalSec * 1000) * 1_000_000;
        return new TimeMsg((int)totalSec, (uint)nanosec);
    }
}
