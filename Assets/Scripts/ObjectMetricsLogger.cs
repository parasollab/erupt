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
        // A scene teardown emits object_deleted for every publisher in the scene (~90 in
        // factory scenes) within one frame; the connector's default queue of 10 dropped most
        // of the burst ("Queue full!" warnings). Same 512 the /collision_object topic uses.
        ros.RegisterPublisher<ObjectEventMsg>(Topic, CollisionObjectPublisher.CollisionObjectQueueSize);
    }

    // worldPos/worldRot are in world space; they're made relative to the robot's base
    // ("BaseTransform", present in every task scene) before being converted to ROS coordinates.
    // scale is an unsigned extents vector (e.g. a resized object's new lossyScale), converted with
    // the same axis order as position (no robot-base relativity applies to scale).
    public void LogEvent(string eventType, string objectId, Vector3? worldPos = null, Quaternion? worldRot = null, Vector3? scale = null, string details = "")
    {
        string participantId = StudyController.Instance != null ? StudyController.Instance.ParticipantId : "unknown";
        int taskIndex = StudyController.Instance != null ? StudyController.Instance.TaskIndex : -1;
        int sceneIndex = StudyController.Instance != null ? StudyController.Instance.SceneIndexInTask : -1;
        string sceneName = SceneManager.GetActiveScene().name;

        PoseMsg pose;
        if (worldPos.HasValue && worldRot.HasValue)
        {
            Transform robotBase = FindRobotBaseTransform();
            Vector3 relPos = worldPos.Value;
            Quaternion relRot = worldRot.Value;
            if (robotBase != null)
            {
                relPos = robotBase.InverseTransformPoint(worldPos.Value);
                relRot = Quaternion.Inverse(robotBase.rotation) * worldRot.Value;
            }
            else
            {
                Debug.LogWarning($"ObjectMetricsLogger: no 'BaseTransform' found in scene '{sceneName}' -- logging '{objectId}' pose relative to world instead of the robot base.");
            }
            pose = new PoseMsg(RosUnityConversion.UnityToRosPosition(relPos), RosUnityConversion.UnityToRosQuaternion(relRot));
        }
        else
        {
            pose = new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
        }

        Vector3Msg scaleMsg = scale.HasValue
            ? new Vector3Msg(scale.Value.x, scale.Value.z, scale.Value.y)
            : new Vector3Msg(0, 0, 0);

        ObjectEventMsg msg = new ObjectEventMsg(
            participantId, eventType, objectId, sceneName, taskIndex, sceneIndex, pose, scaleMsg, details ?? "", NowStamp());
        ros.Publish(Topic, msg);
        Debug.Log($"ObjectMetricsLogger: [{eventType}] object={objectId} details={details}");
    }

    private static Transform FindRobotBaseTransform()
    {
        GameObject go = GameObject.Find("BaseTransform");
        return go != null ? go.transform : null;
    }

    private static TimeMsg NowStamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long totalSec = now.ToUnixTimeSeconds();
        long nanosec = (now.ToUnixTimeMilliseconds() - totalSec * 1000) * 1_000_000;
#if ROS2
        return new TimeMsg((int)totalSec, (uint)nanosec);
#else
        return new TimeMsg((uint)totalSec, (uint)nanosec);
#endif
    }
}
