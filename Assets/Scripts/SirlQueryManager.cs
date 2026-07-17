using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.SirlVrMsgs;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;

public enum SirlMode { Similarity, Preference }

public enum SirlState { Idle, Requesting, Playing, AwaitingSelection, Published }

// Drives a SIRL query round: fetch N trajectories (ROS service or mock),
// play them together on translucent ghost robots (each with its own end
// effector path line), collect the user's selection from the menu, and
// publish the result.
public class SirlQueryManager : MonoBehaviour
{
    [SerializeField] private SirlGhostSpawner ghostSpawner;
    [SerializeField] private DirectArticulationIKController referenceIk;

    [Tooltip("Generate synthetic trajectories instead of calling the ROS service.")]
    [SerializeField] private bool mockMode = true;

    [Tooltip("Hide the other ghost bodies (their path lines stay visible) while replaying a single trajectory via the per-row Replay button; all ghosts reappear afterward.")]
    [SerializeField] private bool hideOthersDuringPlayback = true;

    [Header("ROS names (sirl_vr_msgs / query_node.py in Desktop/sirl)")]
    [SerializeField] private string similarityServiceName = "/sirl/get_similarity_query";
    [SerializeField] private string preferenceServiceName = "/sirl/get_preference_query";
    [SerializeField] private string similarityAnswerTopic = "/sirl/similarity/answer";
    [SerializeField] private string preferenceAnswerTopic = "/sirl/preference/answer";

    [Tooltip("Sent with every query and answer so query_node.py can group a user's session. Auto-generated if left blank.")]
    [SerializeField] private string sessionId = "";

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

    // Queries published so far this session, split by mode.
    public int SimilarityCompletedCount { get; private set; }
    public int PreferenceCompletedCount { get; private set; }

    // Fired on any state/selection change; the panel re-renders off this.
    public event Action StateChanged;
    // Ghost index currently isolated by a single-trajectory replay, -1 when none
    // (all ghosts playing together doesn't single one out; watch State == Playing for that).
    public event Action<int> NowPlaying;

    private readonly List<int> selection = new List<int>();
    private ROSConnection ros;
    private SirlGhost[] ghosts = Array.Empty<SirlGhost>();
    private Coroutine sequenceRoutine;

    // Service responses arrive off the main thread; hand them to Update.
    private JointTrajectoryMsg[] pendingTrajectories;
    private uint pendingQueryId;

    // The query_id the active Trajectories came from; echoed back in the answer.
    private uint currentQueryId;
    private int mockQueryIdCounter;

    void Start()
    {
        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString();

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterRosService<GetSimilarityQueryRequest, GetSimilarityQueryResponse>(similarityServiceName);
        ros.RegisterRosService<GetPreferenceQueryRequest, GetPreferenceQueryResponse>(preferenceServiceName);
        ros.RegisterPublisher<SimilarityAnswerMsg>(similarityAnswerTopic);
        ros.RegisterPublisher<PreferenceAnswerMsg>(preferenceAnswerTopic);
    }

    void Update()
    {
        if (pendingTrajectories != null)
        {
            var trajectories = pendingTrajectories;
            var queryId = pendingQueryId;
            pendingTrajectories = null;
            OnTrajectoriesReceived(queryId, trajectories);
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
            pendingQueryId = (uint)mockQueryIdCounter++;
            pendingTrajectories = GenerateMockTrajectories(TrajectoryCount);
            return;
        }

        if (Mode == SirlMode.Similarity)
        {
            ros.SendServiceMessage<GetSimilarityQueryResponse>(
                similarityServiceName,
                new GetSimilarityQueryRequest(sessionId),
                resp => { pendingQueryId = resp?.query_id ?? 0; pendingTrajectories = resp?.trajectories; });
        }
        else
        {
            ros.SendServiceMessage<GetPreferenceQueryResponse>(
                preferenceServiceName,
                new GetPreferenceQueryRequest(sessionId),
                resp => { pendingQueryId = resp?.query_id ?? 0; pendingTrajectories = resp?.trajectories; });
        }
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
        sequenceRoutine = StartCoroutine(PlayAllRoutine());
    }

    public void StopReplays()
    {
        StopSequence();
        foreach (var ghost in ghosts)
            ghost.Replay.StopReplay();
        ShowAll();
        NowPlaying?.Invoke(-1);
        if (State == SirlState.Playing)
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
            var pair = new byte[] { (byte)ordered[0], (byte)ordered[1] };
            var msg = new SimilarityAnswerMsg(currentQueryId, pair, sessionId);
            ros.Publish(similarityAnswerTopic, msg);
            Debug.Log($"[SirlQueryManager] Published similarity answer on {similarityAnswerTopic}: " +
                      $"query_id={currentQueryId} most similar pair = ({ordered[0]}, {ordered[1]}) of {Trajectories.Length} trajectories.");
            SimilarityCompletedCount++;
        }
        else
        {
            byte preferred = (byte)selection[0];
            var msg = new PreferenceAnswerMsg(currentQueryId, preferred, sessionId);
            ros.Publish(preferenceAnswerTopic, msg);
            Debug.Log($"[SirlQueryManager] Published preference answer on {preferenceAnswerTopic}: " +
                      $"query_id={currentQueryId} preferred = {selection[0]} of {Trajectories.Length} trajectories.");
            PreferenceCompletedCount++;
        }

        State = SirlState.Published;
        StateChanged?.Invoke();
    }

    private void OnTrajectoriesReceived(uint queryId, JointTrajectoryMsg[] trajectories)
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

        currentQueryId = queryId;
        Trajectories = trajectories;
        ghosts = ghostSpawner.Spawn(trajectories.Length, ghostColors);

        // Draws each ghost's end effector path and leaves it posed at the trajectory's start.
        for (int i = 0; i < ghosts.Length; i++)
            ghosts[i].Replay.ShowPath(trajectories[i], ghosts[i].Color);

        sequenceRoutine = StartCoroutine(PlayAllRoutine());
    }

    // Starts every ghost's trajectory at once so all N can be compared side by side.
    private IEnumerator PlayAllRoutine()
    {
        State = SirlState.Playing;
        StateChanged?.Invoke();

        yield return StopAllAndWait();
        ShowAll();

        for (int i = 0; i < ghosts.Length; i++)
            ghosts[i].Replay.StartReplay(Trajectories[i], loop: false);

        yield return new WaitUntil(() => ghosts.All(g => !g.Replay.IsRunning));

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
