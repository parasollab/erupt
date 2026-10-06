using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Trajectory;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Records the person dragging the end effector (through the IK controller) as a joint-space
// path and publishes it on /ferl/robot_trace. FERL's protocol: start where the feature is
// expressed (e.g. right over the laptop) and move away from it, then stop.
// With NoPreference set the same drag is sent as a no-preference trace (frame_id
// "no_preference"): "in this scene, e.g. with the cup closed, everywhere along this drag is
// equally fine". The bridge ties its states together instead of ordering them.
public class RobotTraceRecorder : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private FERLTrajectoryPlayer player;
    [SerializeField] private float sampleHz = 15f;
    [SerializeField] private string traceTopic = "/ferl/robot_trace";

    public const string OrderedFrameId = "ferl_robot_trace";
    public const string NoPreferenceFrameId = "no_preference";

    // Read when the recording stops, so it can still be flipped while dragging.
    public bool NoPreference { get; set; }
    public bool IsRecording { get; private set; }
    public int SampleCount => samples.Count;
    public int PublishedTraces { get; private set; }
    public string LastMessage { get; private set; } = "";

    private readonly List<double[]> samples = new List<double[]>();
    private readonly List<float> stamps = new List<float>();
    private ROSConnection ros;
    private Coroutine sampling;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<JointTrajectoryMsg>(traceTopic);
        if (ikController == null)
            ikController = FindFirstObjectByType<DirectArticulationIKController>();
    }

    public bool StartRecording()
    {
        if (IsRecording)
            return true;
        if (player != null && player.IsPlaying)
            player.Stop();
        if (ikController == null)
        {
            Debug.LogError("[RobotTraceRecorder] No DirectArticulationIKController.");
            return false;
        }
        samples.Clear();
        stamps.Clear();
        IsRecording = true;
        sampling = StartCoroutine(Sample());
        LastMessage = NoPreference
            ? "recording NO-PREFERENCE robot trace: make the drag you would make to show the feature, in a scene where it does not matter (e.g. cup closed), then stop"
            : "recording robot trace: drag the end effector from where the feature is strongest to where it is absent, then stop";
        Debug.Log("[RobotTraceRecorder] " + LastMessage);
        return true;
    }

    public bool StopRecording()
    {
        if (!IsRecording)
            return false;
        IsRecording = false;
        if (sampling != null)
        {
            StopCoroutine(sampling);
            sampling = null;
        }
        if (samples.Count < 2)
        {
            LastMessage = $"robot trace discarded: only {samples.Count} sample(s); drag the end effector for at least a second while recording";
            Debug.LogWarning("[RobotTraceRecorder] " + LastMessage);
            return false;
        }
        var msg = new JointTrajectoryMsg
        {
            joint_names = FerlJointNames.RosArmNames,
            points = new JointTrajectoryPointMsg[samples.Count],
        };
        bool flat = NoPreference;
        msg.header.frame_id = flat ? NoPreferenceFrameId : OrderedFrameId;
        for (int i = 0; i < samples.Count; i++)
        {
            float t = stamps[i] - stamps[0];
            int sec = Mathf.FloorToInt(t);
            msg.points[i] = new JointTrajectoryPointMsg
            {
                positions = samples[i],
                time_from_start = new DurationMsg(sec, (uint)Mathf.RoundToInt((t - sec) * 1e9f)),
            };
        }
        ros.Publish(traceTopic, msg);
        PublishedTraces++;
        LastMessage = $"{(flat ? "no-preference robot trace" : "robot trace")} #{PublishedTraces} sent ({samples.Count} samples over {stamps[stamps.Count - 1] - stamps[0]:F1} s)";
        Debug.Log("[RobotTraceRecorder] " + LastMessage);
        return true;
    }

    public void Toggle()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    private IEnumerator Sample()
    {
        var wait = new WaitForSeconds(1f / Mathf.Max(1f, sampleHz));
        while (IsRecording)
        {
            if (FerlJointNames.TryReadArmPositions(ikController, out double[] positions))
            {
                samples.Add(positions);
                stamps.Add(Time.timeSinceLevelLoad);
            }
            yield return wait;
        }
    }
}
