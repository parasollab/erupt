using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.StudyInterfaces;
using RosMessageTypes.BuiltinInterfaces;

// Publishes StudyEvent messages to /study/events whenever a scene loads, pairing a
// "scene_enter" for the new scene with a "scene_exit" (carrying the elapsed time) for
// whichever scene was previously active. Self-bootstraps like RosConnectionBootstrapper,
// so it doesn't need to be placed in any particular scene.
public class StudyLogger : MonoBehaviour
{
    private const string Topic = "/study/events";

    private struct SceneContext
    {
        public string sceneName;
        public int taskIndex;
        public int sceneIndex;
        public float enterTime;
    }

    private ROSConnection ros;
    private SceneContext? _current;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        GameObject go = new GameObject(nameof(StudyLogger));
        go.AddComponent<StudyLogger>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        ros = ROSConnection.GetOrCreateInstance();
        // StudyEventMsg's own [RuntimeInitializeOnLoadMethod] Register() defaults to
        // AfterSceneLoad, which runs after this (BeforeSceneLoad); calling it explicitly here
        // guarantees MessageRegistry knows its RosMessageName before RegisterPublisher below
        // resolves it -- otherwise the resolved name is silently empty and never gets fixed up,
        // and the ROS-TCP-Endpoint server rejects the registration.
        StudyEventMsg.Register();
        ros.RegisterPublisher<StudyEventMsg>(Topic);
        SceneManager.sceneLoaded += OnSceneLoaded;
        Application.quitting += OnQuitting;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_current.HasValue)
        {
            PublishEvent("scene_exit", _current.Value, Time.realtimeSinceStartup - _current.Value.enterTime);
        }

        SceneContext ctx = new SceneContext
        {
            sceneName = scene.name,
            taskIndex = StudyController.Instance != null ? StudyController.Instance.TaskIndex : -1,
            sceneIndex = StudyController.Instance != null ? StudyController.Instance.SceneIndexInTask : -1,
            enterTime = Time.realtimeSinceStartup,
        };
        _current = ctx;
        PublishEvent("scene_enter", ctx, 0.0);
    }

    private void OnQuitting()
    {
        if (_current.HasValue)
        {
            PublishEvent("scene_exit", _current.Value, Time.realtimeSinceStartup - _current.Value.enterTime);
            _current = null;
        }
    }

    private void PublishEvent(string eventType, SceneContext ctx, double durationSec)
    {
        string participantId = StudyController.Instance != null ? StudyController.Instance.ParticipantId : "unknown";
        StudyEventMsg msg = new StudyEventMsg(
            participantId, eventType, ctx.sceneName, ctx.taskIndex, ctx.sceneIndex, durationSec, NowStamp());
        ros.Publish(Topic, msg);
        Debug.Log($"StudyLogger: [{eventType}] {ctx.sceneName} duration={durationSec:F1}s");
    }

    private static TimeMsg NowStamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long totalSec = now.ToUnixTimeSeconds();
        long nanosec = (now.ToUnixTimeMilliseconds() - totalSec * 1000) * 1_000_000;
        return RosMessageCompatibility.CreateTime(totalSec, (uint)nanosec);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Application.quitting -= OnQuitting;
    }
}
