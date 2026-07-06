using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Sirl;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;

public enum SirlMode { Similarity, Preference }

public enum SirlState { Idle, Requesting, PlayingSequential, AwaitingSelection, Published }

// Drives a SIRL query round: fetch N trajectories (ROS service or mock),
// play them one-by-one on translucent ghost robots, collect the user's
// selection from the menu, and publish the result.
public class SirlQueryManager : MonoBehaviour
{
    [SerializeField] private SirlGhostSpawner ghostSpawner;
    [SerializeField] private DirectArticulationIKController referenceIk;

    [Tooltip("Generate synthetic trajectories instead of calling the ROS service.")]
    [SerializeField] private bool mockMode = true;

    [Tooltip("Show only the ghost whose trajectory is playing; all ghosts reappear for selection.")]
    [SerializeField] private bool hideOthersDuringPlayback = true;

    [Header("ROS names (placeholders until real interfaces exist)")]
    [SerializeField] private string similarityServiceName = "/sirl/similarity_query_trajectories";
    [SerializeField] private string preferenceServiceName = "/sirl/preference_query_trajectories";
    [SerializeField] private string similarityResultTopic = "/sirl/similarity_query_result";
    [SerializeField] private string preferenceResultTopic = "/sirl/preference_query_result";

    [SerializeField] private Color[] ghostColors =
    {
        new Color(0f, 0.9f, 0.9f, 0.45f),   // cyan
        new Color(1f, 0.3f, 0.9f, 0.45f),   // magenta
        new Color(1f, 0.9f, 0.2f, 0.45f),   // yellow
    };

    [Header("Mock trajectory shape")]
    [SerializeField] private float mockDurationSeconds = 5f;
    [SerializeField] private int mockPointCount = 24;

    public SirlMode Mode { get; private set; } = SirlMode.Similarity;
    public SirlState State { get; private set; } = SirlState.Idle;
    public int TrajectoryCount => Mode == SirlMode.Similarity ? 3 : 2;
    public int RequiredSelectionCount => Mode == SirlMode.Similarity ? 2 : 1;
    public IReadOnlyList<Color> Colors => ghostColors;
    public IReadOnlyCollection<int> Selection => selection;
    public bool SelectionValid => selection.Count == RequiredSelectionCount;
    public JointTrajectoryMsg[] Trajectories { get; private set; } = Array.Empty<JointTrajectoryMsg>();

    // Fired on any state/selection change; the panel re-renders off this.
    public event Action StateChanged;
    // Ghost index currently playing in the sequential pass, -1 when none.
    public event Action<int> NowPlaying;

    private readonly List<int> selection = new List<int>();
    private ROSConnection ros;
    private SirlGhost[] ghosts = Array.Empty<SirlGhost>();
    private Coroutine sequenceRoutine;

    // Service responses arrive off the main thread; hand them to Update.
    private JointTrajectoryMsg[] pendingTrajectories;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterRosService<GetTrajectoriesRequest, GetTrajectoriesResponse>(similarityServiceName);
        ros.RegisterRosService<GetTrajectoriesRequest, GetTrajectoriesResponse>(preferenceServiceName);
        ros.RegisterPublisher<SimilarityQueryResultMsg>(similarityResultTopic);
        ros.RegisterPublisher<PreferenceQueryResultMsg>(preferenceResultTopic);
    }

    void Update()
    {
        if (pendingTrajectories != null)
        {
            var trajectories = pendingTrajectories;
            pendingTrajectories = null;
            OnTrajectoriesReceived(trajectories);
        }
    }

    public void SetMode(SirlMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ResetQuery();
    }

    public void ResetQuery()
    {
        StopSequence();
        ghostSpawner.Clear();
        ghosts = Array.Empty<SirlGhost>();
        Trajectories = Array.Empty<JointTrajectoryMsg>();
        selection.Clear();
        State = SirlState.Idle;
        StateChanged?.Invoke();
    }

    public void RequestTrajectories()
    {
        if (State == SirlState.Requesting) return;

        StopSequence();
        ghostSpawner.Clear();
        ghosts = Array.Empty<SirlGhost>();
        selection.Clear();
        State = SirlState.Requesting;
        StateChanged?.Invoke();

        if (mockMode)
        {
            pendingTrajectories = GenerateMockTrajectories(TrajectoryCount);
            return;
        }

        string service = Mode == SirlMode.Similarity ? similarityServiceName : preferenceServiceName;
        ros.SendServiceMessage<GetTrajectoriesResponse>(
            service,
            new GetTrajectoriesRequest((uint)TrajectoryCount),
            resp => pendingTrajectories = resp?.trajectories);
    }

    public void ReplayOne(int index)
    {
        if (index < 0 || index >= ghosts.Length || Trajectories.Length <= index) return;

        StopSequence();
        sequenceRoutine = StartCoroutine(ReplayOneRoutine(index));
    }

    private IEnumerator ReplayOneRoutine(int index)
    {
        yield return StopAllAndWait();

        ShowOnly(index);
        NowPlaying?.Invoke(index);
        ghosts[index].Replay.StartReplay(Trajectories[index], loop: false);
        yield return new WaitUntil(() => !ghosts[index].Replay.IsRunning);
        ShowAll();
        NowPlaying?.Invoke(-1);
        sequenceRoutine = null;
    }

    public void ReplayAll()
    {
        if (ghosts.Length == 0) return;
        StopSequence();
        sequenceRoutine = StartCoroutine(SequentialPlaybackRoutine());
    }

    public void StopReplays()
    {
        StopSequence();
        foreach (var ghost in ghosts)
            ghost.Replay.StopReplay();
        ShowAll();
        NowPlaying?.Invoke(-1);
        if (State == SirlState.PlayingSequential)
        {
            State = SirlState.AwaitingSelection;
            StateChanged?.Invoke();
        }
    }

    public void ToggleSelect(int index)
    {
        if (index < 0 || index >= Trajectories.Length) return;

        if (!selection.Remove(index))
        {
            selection.Add(index);
            // Keep at most the N most recent picks.
            while (selection.Count > RequiredSelectionCount)
                selection.RemoveAt(0);
        }
        StateChanged?.Invoke();
    }

    public void ConfirmAndPublish()
    {
        if (!SelectionValid)
        {
            Debug.LogWarning($"[SirlQueryManager] Cannot publish: need exactly {RequiredSelectionCount} selected, have {selection.Count}.");
            return;
        }

        if (Mode == SirlMode.Similarity)
        {
            var ordered = selection.OrderBy(i => i).ToList();
            var msg = new SimilarityQueryResultMsg(Trajectories, ordered[0], ordered[1]);
            ros.Publish(similarityResultTopic, msg);
            Debug.Log($"[SirlQueryManager] Published similarity result on {similarityResultTopic}: " +
                      $"most similar pair = ({ordered[0]}, {ordered[1]}) of {Trajectories.Length} trajectories.");
        }
        else
        {
            var msg = new PreferenceQueryResultMsg(Trajectories, selection[0]);
            ros.Publish(preferenceResultTopic, msg);
            Debug.Log($"[SirlQueryManager] Published preference result on {preferenceResultTopic}: " +
                      $"preferred = {selection[0]} of {Trajectories.Length} trajectories.");
        }

        State = SirlState.Published;
        StateChanged?.Invoke();
    }

    private void OnTrajectoriesReceived(JointTrajectoryMsg[] trajectories)
    {
        if (trajectories == null || trajectories.Length == 0)
        {
            Debug.LogWarning("[SirlQueryManager] Received no trajectories.");
            State = SirlState.Idle;
            StateChanged?.Invoke();
            return;
        }

        if (trajectories.Length != TrajectoryCount)
            Debug.LogWarning($"[SirlQueryManager] Expected {TrajectoryCount} trajectories, got {trajectories.Length}.");

        Trajectories = trajectories;
        ghosts = ghostSpawner.Spawn(trajectories.Length, ghostColors);

        // Idle ghosts hold their trajectory's start pose.
        for (int i = 0; i < ghosts.Length; i++)
            if (trajectories[i].points != null && trajectories[i].points.Length > 0)
                ghosts[i].Ik.ApplyJointState(trajectories[i].joint_names, trajectories[i].points[0].positions);

        sequenceRoutine = StartCoroutine(SequentialPlaybackRoutine());
    }

    private IEnumerator SequentialPlaybackRoutine()
    {
        State = SirlState.PlayingSequential;
        StateChanged?.Invoke();

        yield return StopAllAndWait();

        for (int i = 0; i < ghosts.Length; i++)
        {
            ShowOnly(i);
            NowPlaying?.Invoke(i);
            ghosts[i].Replay.StartReplay(Trajectories[i], loop: false);
            yield return new WaitUntil(() => !ghosts[i].Replay.IsRunning);
            yield return new WaitForSeconds(0.5f);
        }

        ShowAll();
        NowPlaying?.Invoke(-1);
        sequenceRoutine = null;
        State = SirlState.AwaitingSelection;
        StateChanged?.Invoke();
    }

    private IEnumerator StopAllAndWait()
    {
        foreach (var ghost in ghosts)
            ghost.Replay.StopReplay();
        yield return new WaitUntil(() => ghosts.All(g => !g.Replay.IsRunning));
    }

    private void StopSequence()
    {
        if (sequenceRoutine != null)
        {
            StopCoroutine(sequenceRoutine);
            sequenceRoutine = null;
        }
    }

    private void ShowOnly(int index)
    {
        if (!hideOthersDuringPlayback) return;
        for (int i = 0; i < ghosts.Length; i++)
            ghosts[i].SetVisible(i == index);
    }

    private void ShowAll()
    {
        foreach (var ghost in ghosts)
            ghost.SetVisible(true);
    }

    // Synthetic trajectories: sinusoidal offsets from the reference robot's
    // current pose, with a distinct pair of emphasized joints per trajectory.
    private JointTrajectoryMsg[] GenerateMockTrajectories(int count)
    {
        string[] names = referenceIk.GetJointStateNames();
        float[] home = referenceIk.GetJointStatePositions();
        if (names.Length == 0)
        {
            Debug.LogError("[SirlQueryManager] Reference IK controller has no joints; cannot mock.");
            return Array.Empty<JointTrajectoryMsg>();
        }

        var trajectories = new JointTrajectoryMsg[count];
        for (int k = 0; k < count; k++)
        {
            var points = new JointTrajectoryPointMsg[mockPointCount];
            for (int p = 0; p < mockPointCount; p++)
            {
                float t = (float)p / (mockPointCount - 1);
                // sin(2π t) is 0 at t=0 and t=1, so each pass starts and ends at home.
                float wave = Mathf.Sin(2f * Mathf.PI * t);

                var positions = new double[names.Length];
                for (int j = 0; j < names.Length; j++)
                {
                    float amplitude = 0.15f;
                    if (j == k % names.Length) amplitude = 0.5f;
                    else if (j == (k + 2) % names.Length) amplitude = 0.35f;
                    // Alternate direction per trajectory so they diverge visibly.
                    float sign = (k + j) % 2 == 0 ? 1f : -1f;
                    positions[j] = home[j] + sign * amplitude * wave;
                }

                float timeFromStart = t * mockDurationSeconds;
                points[p] = new JointTrajectoryPointMsg(
                    positions,
                    new double[0], new double[0], new double[0],
                    new DurationMsg((int)timeFromStart, (uint)((timeFromStart % 1f) * 1e9f)));
            }

            trajectories[k] = new JointTrajectoryMsg(new HeaderMsg(), names, points);
        }

        Debug.Log($"[SirlQueryManager] Generated {count} mock trajectories ({names.Length} joints, {mockPointCount} points, {mockDurationSeconds}s).");
        return trajectories;
    }

    // ─── Editor testing shims (no VR input needed) ────────────────────────────

    [ContextMenu("SIRL/Request Trajectories")]
    private void CtxRequest() => RequestTrajectories();

    [ContextMenu("SIRL/Replay All")]
    private void CtxReplayAll() => ReplayAll();

    [ContextMenu("SIRL/Select 0 and 1, Publish")]
    private void CtxSelectAndPublish()
    {
        selection.Clear();
        ToggleSelect(0);
        if (RequiredSelectionCount > 1) ToggleSelect(1);
        ConfirmAndPublish();
    }

    [ContextMenu("SIRL/Toggle Mode")]
    private void CtxToggleMode() =>
        SetMode(Mode == SirlMode.Similarity ? SirlMode.Preference : SirlMode.Similarity);
}
