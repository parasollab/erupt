using System;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Records an environmental trace: the scene as it is when recording starts, then one
// snapshot after every edit (move, attribute toggle, carried change) until stopped.
// The protocol is the mirror of a robot trace: start with the scene arranged so the current
// plan is most unacceptable, edit it step by step toward acceptable, then stop.
// A single long grab yields several waypoints: while recording, an object being dragged is
// snapshotted as a new displacement edit every sampleDistance metres (or sampleTurnDegrees)
// of travel, so there is no need to let go at each waypoint, though that still works too.
public class EnvTraceRecorder : MonoBehaviour
{
    [SerializeField] private SceneGraphPublisher publisher;
    [SerializeField] private FERLTrajectoryPlayer player;
    [SerializeField] private string traceTopic = "/ferl/env_trace";
    [SerializeField] private string userId = "";

    [Header("Waypoints from a long grab")]
    [Tooltip("While recording, a dragged object is snapshotted as a new edit each time it has travelled this far (metres) since the last snapshot.")]
    [SerializeField] private float sampleDistance = 0.03f;
    [Tooltip("... or turned this much (degrees about ROS z) since the last snapshot.")]
    [SerializeField] private float sampleTurnDegrees = 15f;

    public bool IsRecording { get; private set; }
    public int SnapshotCount => snapshots.Count;
    public int PublishedTraces { get; private set; }
    public string LastMessage { get; private set; } = "";

    private readonly List<SceneGraphSnapshot> snapshots = new List<SceneGraphSnapshot>();
    private readonly List<SceneEditData> edits = new List<SceneEditData>();
    private readonly List<float> stamps = new List<float>();
    private ROSConnection ros;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(traceTopic);
        if (publisher == null)
            publisher = SceneGraphPublisher.Instance;
    }

    private void OnDestroy()
    {
        if (publisher != null)
            publisher.Edited -= OnEdited;
    }

    private void Update()
    {
        if (!IsRecording)
            return;
        // Objects are only ever added to or removed from All on enable/disable, so sampling
        // (which publishes and appends a snapshot) is safe inside this loop.
        foreach (SceneGraphObject obj in SceneGraphObject.All)
        {
            if (obj == null || !obj.IsMoving)
                continue;
            if (obj.PendingDisplacement.magnitude >= sampleDistance
                || Math.Abs(obj.PendingTurn) * Mathf.Rad2Deg >= sampleTurnDegrees)
                obj.SampleMove();
        }
    }

    public bool StartRecording()
    {
        if (IsRecording)
            return true;
        if (publisher == null)
            publisher = SceneGraphPublisher.Instance;
        if (publisher == null)
        {
            Debug.LogError("[EnvTraceRecorder] No SceneGraphPublisher in the scene.");
            return false;
        }
        if (player == null || !player.HasPlan)
        {
            LastMessage = "env trace needs a plan to hold fixed: press Plan first";
            Debug.LogWarning("[EnvTraceRecorder] " + LastMessage);
            return false;
        }
        snapshots.Clear();
        edits.Clear();
        stamps.Clear();
        snapshots.Add(publisher.Snapshot());
        stamps.Add(Time.timeSinceLevelLoad);
        publisher.Edited += OnEdited;
        IsRecording = true;
        LastMessage = $"recording env trace: edit the scene from most unacceptable toward acceptable (a drag is sampled every {sampleDistance * 100f:F0} cm; toggles count too), then stop";
        Debug.Log("[EnvTraceRecorder] " + LastMessage);
        return true;
    }

    private void OnEdited(SceneGraphSnapshot snapshot, SceneEditData edit)
    {
        if (!IsRecording)
            return;
        snapshots.Add(snapshot);
        edits.Add(edit);
        stamps.Add(Time.timeSinceLevelLoad);
    }

    public bool StopRecording()
    {
        if (!IsRecording)
            return false;
        IsRecording = false;
        publisher.Edited -= OnEdited;
        if (snapshots.Count < 2)
        {
            LastMessage = "env trace discarded: no scene edits were made while recording";
            Debug.LogWarning("[EnvTraceRecorder] " + LastMessage);
            return false;
        }

        var writer = new FerlJsonWriter();
        writer.BeginObject();
        writer.Key("user_id").Value(userId ?? "");
        writer.Key("direction").Value("decreasing");
        writer.Key("plan_seq").Value(player != null ? player.PlanSeq : 0);
        writer.Key("timestamps").BeginArray();
        for (int i = 0; i < stamps.Count; i++)
            writer.Value(stamps[i] - stamps[0]);
        writer.EndArray();
        writer.Key("snapshots").BeginArray();
        foreach (SceneGraphSnapshot snapshot in snapshots)
            snapshot.WriteJson(writer);
        writer.EndArray();
        writer.Key("edits").BeginArray();
        foreach (SceneEditData edit in edits)
            edit.WriteJson(writer);
        writer.EndArray();
        writer.Key("edited_object_id").Value(edits[0].objectId);
        writer.Key("edit_type").Value(edits[0].Kind);
        writer.Key("source").Value("vr_env");
        writer.EndObject();
        ros.Publish(traceTopic, new StringMsg(writer.ToString()));
        PublishedTraces++;
        LastMessage = $"env trace #{PublishedTraces} sent ({snapshots.Count} scenes, {edits.Count} edits)";
        Debug.Log("[EnvTraceRecorder] " + LastMessage);
        return true;
    }

    public void Toggle()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }
}
