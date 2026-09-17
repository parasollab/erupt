using System;
using System.Collections;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Plays the bridge's plan (/ferl/plan) on the real FR3 model, and turns a drag of the
// end-effector handle during playback into a FERL correction (/ferl/correction): the
// waypoint being executed is FERL's correction time t, the released joint state is a_H.
// Not TrajectoryReplay: that drives a ghost, loops, and has no pause or waypoint index.
public class FERLTrajectoryPlayer : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private Quest3RobotInteractionController interaction;
    [Tooltip("Seconds between waypoints when the plan carries no timing (or useMessageTiming is off).")]
    [SerializeField] private float secondsPerWaypoint = 0.4f;
    [SerializeField] private bool useMessageTiming = true;
    [Tooltip("planar: the tool's xy push through the Jacobian (demo default); raw: the joint difference.")]
    [SerializeField] private string correctionMode = "planar";
    [SerializeField] private bool loopPlayback = false;

    [Header("ROS")]
    [SerializeField] private string planTopic = "/ferl/plan";
    [SerializeField] private string correctionTopic = "/ferl/correction";

    public JointTrajectoryMsg Plan { get; private set; }
    public int PlanSeq { get; private set; }
    public bool HasPlan => Plan != null && Plan.points != null && Plan.points.Length > 0;
    public bool IsPlaying { get; private set; }
    public bool IsPaused => paused;
    public int CurrentIndex { get; private set; }
    public int LastCorrectionIndex { get; private set; } = -1;

    public event Action PlanReceived;
    public event Action<bool> PlayStateChanged;
    public event Action<int> CorrectionPublished;

    private ROSConnection ros;
    private string[] unityJointNames = Array.Empty<string>();
    private double[][] waypoints = Array.Empty<double[]>();
    private float[] times = Array.Empty<float>();
    private Coroutine playback;
    private bool paused;
    private int pausedIndex;
    private double[] referenceQ;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        JointTrajectoryMsg.Register();
        StringMsg.Register();
        ros.Subscribe<JointTrajectoryMsg>(planTopic, OnPlan);
        ros.RegisterPublisher<StringMsg>(correctionTopic);
        if (interaction == null)
            interaction = FindFirstObjectByType<Quest3RobotInteractionController>();
        if (ikController == null)
            ikController = FindFirstObjectByType<DirectArticulationIKController>();
        if (interaction != null)
        {
            interaction.HandleDragStarted += OnDragStarted;
            interaction.HandleDragEnded += OnDragEnded;
        }
    }

    private void OnDestroy()
    {
        if (ros != null)
            ros.Unsubscribe<JointTrajectoryMsg>(planTopic, OnPlan);
        if (interaction != null)
        {
            interaction.HandleDragStarted -= OnDragStarted;
            interaction.HandleDragEnded -= OnDragEnded;
        }
    }

    private void OnPlan(JointTrajectoryMsg msg)
    {
        Stop();
        Plan = msg;
        PlanSeq = ParseSeq(msg.header != null ? msg.header.frame_id : "");
        unityJointNames = FerlJointNames.UnityNamesForRos(msg.joint_names);
        waypoints = new double[msg.points.Length][];
        times = new float[msg.points.Length];
        for (int i = 0; i < msg.points.Length; i++)
        {
            waypoints[i] = msg.points[i].positions;
            var duration = msg.points[i].time_from_start;
            times[i] = duration != null ? (float)(duration.sec + duration.nanosec * 1e-9) : i * secondsPerWaypoint;
        }
        CurrentIndex = 0;
        LastCorrectionIndex = -1;
        Debug.Log($"[FERLTrajectoryPlayer] Received plan {PlanSeq} with {waypoints.Length} waypoints.");
        GoToStart();
        PlanReceived?.Invoke();
    }

    private static int ParseSeq(string frameId)
    {
        const string prefix = "plan_";
        if (!string.IsNullOrEmpty(frameId) && frameId.StartsWith(prefix) &&
            int.TryParse(frameId.Substring(prefix.Length), out int seq))
            return seq;
        return 0;
    }

    public void GoToStart()
    {
        if (!HasPlan)
            return;
        Apply(waypoints[0]);
        CurrentIndex = 0;
    }

    public void TogglePlay()
    {
        if (IsPlaying) Stop();
        else Play();
    }

    public void Play()
    {
        if (!HasPlan || ikController == null)
        {
            Debug.LogWarning("[FERLTrajectoryPlayer] No plan to play yet (send the plan command first).");
            return;
        }
        Stop();
        IsPlaying = true;
        paused = false;
        playback = StartCoroutine(Playback(0));
        PlayStateChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (playback != null)
        {
            StopCoroutine(playback);
            playback = null;
        }
        bool was = IsPlaying;
        IsPlaying = false;
        paused = false;
        if (was)
            PlayStateChanged?.Invoke(false);
    }

    private IEnumerator Playback(int fromIndex)
    {
        do
        {
            if (fromIndex == 0)
                Apply(waypoints[0]);
            for (int i = Math.Max(1, fromIndex); i < waypoints.Length; i++)
            {
                CurrentIndex = i;
                double[] from = CurrentJoints() ?? waypoints[i - 1];
                double[] to = waypoints[i];
                float duration = useMessageTiming ? Mathf.Max(0.02f, times[i] - times[i - 1]) : secondsPerWaypoint;
                float elapsed = 0f;
                var lerped = new double[to.Length];
                while (elapsed < duration)
                {
                    if (paused)
                    {
                        // The person is dragging the tool: hold here until they let go, then
                        // continue toward this waypoint from wherever they left the arm.
                        yield return null;
                        from = CurrentJoints() ?? from;
                        elapsed = 0f;
                        continue;
                    }
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    for (int j = 0; j < lerped.Length; j++)
                        lerped[j] = from[j] + (to[j] - from[j]) * t;
                    Apply(lerped);
                    yield return null;
                }
                Apply(to);
            }
            fromIndex = 0;
            if (loopPlayback)
                yield return new WaitForSeconds(0.5f);
        } while (loopPlayback);
        IsPlaying = false;
        playback = null;
        PlayStateChanged?.Invoke(false);
    }

    private void Apply(double[] positions)
    {
        if (ikController == null || positions == null)
            return;
        ikController.ApplyJointState(unityJointNames, positions);
        if (interaction != null && interaction.Handle != null && ikController.EndEffector != null && !paused)
            interaction.Handle.position = ikController.EndEffector.position;
    }

    private double[] CurrentJoints()
    {
        if (ikController == null)
            return null;
        // The plan's joint order is ROS order (joint1..7); read back in that same order.
        var result = new double[unityJointNames.Length];
        for (int i = 0; i < unityJointNames.Length; i++)
        {
            if (!ikController.TryGetJointAngle(unityJointNames[i], out float radians))
                return null;
            result[i] = radians;
        }
        return result;
    }

    private void OnDragStarted()
    {
        if (!IsPlaying || paused)
            return;
        paused = true;
        pausedIndex = Mathf.Clamp(CurrentIndex, 0, waypoints.Length - 1);
        referenceQ = (double[])waypoints[pausedIndex].Clone();
        Debug.Log($"[FERLTrajectoryPlayer] Playback paused at waypoint {pausedIndex} for a correction.");
    }

    private void OnDragEnded()
    {
        if (!paused)
            return;
        double[] corrected = CurrentJoints();
        paused = false;
        if (corrected == null || referenceQ == null)
            return;
        PublishCorrection(pausedIndex, referenceQ, corrected);
    }

    public void PublishCorrection(int index, double[] reference, double[] corrected)
    {
        var writer = new FerlJsonWriter();
        writer.BeginObject();
        writer.Key("plan_seq").Value(PlanSeq);
        writer.Key("index").Value(index);
        writer.Key("reference_q").Doubles(reference);
        writer.Key("corrected_q").Doubles(corrected);
        writer.Key("mode").Value(correctionMode);
        writer.Key("timestamp").Value(Time.timeSinceLevelLoad);
        writer.EndObject();
        ros.Publish(correctionTopic, new StringMsg(writer.ToString()));
        LastCorrectionIndex = index;
        CorrectionPublished?.Invoke(index);
        Debug.Log($"[FERLTrajectoryPlayer] Published correction at waypoint {index} of plan {PlanSeq}.");
    }
}
