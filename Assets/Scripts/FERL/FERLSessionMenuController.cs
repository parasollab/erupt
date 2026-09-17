using System;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UIElements;

// The FERL session panel: endpoints, plan, play, traces, corrections, learn, save. Every
// button calls a public method here, which the desktop keyboard shortcuts reuse. Status
// comes back on /ferl/status as flat JSON that JsonUtility can parse.
public class FERLSessionMenuController : MonoBehaviour
{
    [Serializable]
    public class FerlStatus
    {
        public int seq;
        public string phase = "";
        public bool busy;
        public string message = "";
        public string result = "";
        public string error = "";
        public float elapsed;
        public string[] features = Array.Empty<string>();
        public float[] weights = Array.Empty<float>();
        public float confidence = -1f;
        public float confidence_threshold = 1f;
        public bool needs_traces;
        public bool has_start;
        public bool has_goal;
        public int plan_seq;
        public bool plan_usable;
        public string infeasibility = "";
        public int plan_length;
        public string[] object_slots = Array.Empty<string>();
        public string scene_state = "";
        public int n_robot_traces;
        public int n_env_traces;
        public int n_corrections;
        public int traces_since_learn;
        public string[] round_trip_errors = Array.Empty<string>();
        public float[] home_ee = Array.Empty<float>();
        public string last_log = "";
    }

    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private FERLTrajectoryPlayer player;
    [SerializeField] private RobotTraceRecorder robotTraceRecorder;
    [SerializeField] private EnvTraceRecorder envTraceRecorder;
    [Tooltip("Optional: spawns start/goal ghosts when endpoints are set from the robot's pose.")]
    [SerializeField] private SpawnGhosts ghostSpawner;
    [SerializeField] private RewardMapVisualizer rewardMap;
    [SerializeField] private int rewardMapPoints = 1500;

    [Header("Endpoints from objects")]
    [SerializeField] private string startObjectId = "blue_marker";
    [SerializeField] private string goalObjectId = "orange_marker";

    [Header("ROS")]
    [SerializeField] private string commandTopic = "/ferl/command";
    [SerializeField] private string statusTopic = "/ferl/status";

    public FerlStatus LastStatus { get; private set; }
    public event Action<FerlStatus> StatusChanged;

    private ROSConnection ros;
    private Button setStartButton, setGoalButton, ikEndpointsButton, planButton, playStopButton;
    private Button robotTraceButton, envTraceButton, envCorrectionButton, learnButton, learnOnlyButton, replanButton, resetButton, saveButton, syncButton, rewardMapButton;
    private string rewardMapFeature = "off";
    private Label statusLabel, featuresLabel, hintLabel, resultLabel;
    private string lastResultMessage = "";
    private bool loggedHomeEe;
    private int statusCount;
    private float lastStatusTime = -1f;
    private float enabledAt;
    private float nextLivenessCheck;
    [SerializeField] private float staleAfterSeconds = 4f;

    private void OnEnable()
    {
        ros = ROSConnection.GetOrCreateInstance();
        // The generated Register() is a RuntimeInitializeOnLoadMethod (AfterSceneLoad), which
        // runs after OnEnable of the first scene; subscribing before it sends an empty message
        // type and the endpoint rejects the subscription in player builds.
        StringMsg.Register();
        ros.RegisterPublisher<StringMsg>(commandTopic);
        ros.Subscribe<StringMsg>(statusTopic, OnStatus);
        if (player != null)
        {
            player.PlayStateChanged += OnPlayStateChanged;
            player.PlanReceived += OnPlanReceived;
            player.CorrectionPublished += OnCorrectionPublished;
        }
        statusCount = 0;
        lastStatusTime = -1f;
        enabledAt = Time.time;

        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();
        VisualElement root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("FERLSessionMenuController: No UIDocument/rootVisualElement found.");
            return;
        }

        setStartButton = Bind(root, "ferlSetStart", SetStart);
        setGoalButton = Bind(root, "ferlSetGoal", SetGoal);
        ikEndpointsButton = Bind(root, "ferlIkEndpoints", IkEndpoints);
        planButton = Bind(root, "ferlPlan", Plan);
        playStopButton = Bind(root, "ferlPlayStop", TogglePlay);
        robotTraceButton = Bind(root, "ferlRecordRobotTrace", ToggleRobotTrace);
        envTraceButton = Bind(root, "ferlRecordEnvTrace", ToggleEnvTrace);
        envCorrectionButton = Bind(root, "ferlEnvCorrection", EnvCorrection);
        learnButton = Bind(root, "ferlLearn", Learn);
        learnOnlyButton = Bind(root, "ferlLearnOnly", LearnOnly);
        replanButton = Bind(root, "ferlReplan", Replan);
        resetButton = Bind(root, "ferlReset", ResetSession);
        saveButton = Bind(root, "ferlSave", Save);
        syncButton = Bind(root, "ferlSync", Sync);
        rewardMapButton = Bind(root, "ferlRewardMap", CycleRewardMap);
        if (rewardMap != null)
            rewardMap.Updated += OnRewardMapUpdated;
        statusLabel = root.Q<Label>("ferlStatusLabel");
        featuresLabel = root.Q<Label>("ferlFeaturesLabel");
        hintLabel = root.Q<Label>("ferlHintLabel");
        resultLabel = root.Q<Label>("ferlResultLabel");

        UpdateWaitingText();
        Sync();
    }

    private void OnDisable()
    {
        if (rewardMap != null)
            rewardMap.Updated -= OnRewardMapUpdated;
        if (ros != null)
            ros.Unsubscribe<StringMsg>(statusTopic, OnStatus);
        if (player != null)
        {
            player.PlayStateChanged -= OnPlayStateChanged;
            player.PlanReceived -= OnPlanReceived;
            player.CorrectionPublished -= OnCorrectionPublished;
        }
    }

    private void OnCorrectionPublished(int index)
    {
        ShowResult($"correction at waypoint {index} sent; waiting for the confidence gate ...", false, pending: true);
    }

    private void ShowResult(string text, bool bad, bool pending = false)
    {
        if (resultLabel == null)
            return;
        resultLabel.text = text;
        resultLabel.style.color = pending ? new Color(1f, 0.86f, 0.6f) : bad ? new Color(1f, 0.55f, 0.45f) : new Color(0.67f, 1f, 0.67f);
    }

    // Liveness: until the first status arrives, say what we are waiting for and whether the
    // TCP link is even up; afterwards, flag a bridge that has gone quiet.
    private void Update()
    {
        if (Time.time < nextLivenessCheck)
            return;
        nextLivenessCheck = Time.time + 0.5f;
        if (LastStatus == null)
            UpdateWaitingText();
        else if (Time.time - lastStatusTime > staleAfterSeconds)
            SetText(hintLabel, $"no /ferl/status for {Time.time - lastStatusTime:F0} s. Either the bridge stopped, or another Unity instance " +
                               "connected to the endpoint (it only delivers to the newest client): make this the only instance and press Sync, or restart the endpoint.");
    }

    private void UpdateWaitingText()
    {
        string link = ros == null ? "no ROSConnection"
            : ros.HasConnectionError ? $"cannot reach {ros.RosIPAddress}:{ros.RosPort}"
            : ros.HasConnectionThread ? $"TCP link to {ros.RosIPAddress}:{ros.RosPort}"
            : "connecting ...";
        SetText(statusLabel, $"waiting for /ferl/status ({Time.time - enabledAt:F0} s)\n{link}\n" +
                             "start: ros2 launch preference_rl ferl_bridge.launch.py (and make sure no other Unity instance is connected: the endpoint serves one)");
    }

    private void OnPlanReceived()
    {
        if (player == null)
            return;
        SetText(hintLabel, $"plan {player.PlanSeq} received ({(player.Plan != null ? player.Plan.points.Length : 0)} waypoints): robot moved to its start, press Play plan");
        RefreshButtons();
    }

    private static Button Bind(VisualElement root, string name, Action action)
    {
        Button button = root.Q<Button>(name);
        if (button == null)
            Debug.LogError($"FERLSessionMenuController: '{name}' not found in UXML.");
        else
            button.clicked += action;
        return button;
    }

    private static void SetText(Label label, string text)
    {
        if (label != null)
            label.text = text;
    }


    // ---- commands -------------------------------------------------------------------

    public void SendCommand(string json)
    {
        if (ros == null)
            ros = ROSConnection.GetOrCreateInstance();
        ros.Publish(commandTopic, new StringMsg(json));
    }

    private static string Simple(string command)
    {
        var writer = new FerlJsonWriter();
        writer.BeginObject().Key("command").Value(command).Key("client").Value(FerlClient.Id).EndObject();
        return writer.ToString();
    }

    private bool TryReadJoints(out double[] positions)
    {
        if (ikController == null)
            ikController = FindFirstObjectByType<DirectArticulationIKController>();
        if (FerlJointNames.TryReadArmPositions(ikController, out positions))
            return true;
        Debug.LogError("FERLSessionMenuController: could not read the FR3 joints (fr3_joint1..7).");
        return false;
    }

    private void SendEndpoint(string command, double[] positions)
    {
        var writer = new FerlJsonWriter();
        writer.BeginObject().Key("command").Value(command).Key("client").Value(FerlClient.Id).Key("positions").Doubles(positions).EndObject();
        SendCommand(writer.ToString());
    }

    public void SetStart()
    {
        if (!TryReadJoints(out double[] q))
            return;
        SendEndpoint("set_start", q);
        ghostSpawner?.SpawnStartGhostFromPose(ikController, FerlJointNames.UnityArmNames, q);
        SetText(hintLabel, "start = current pose");
    }

    public void SetGoal()
    {
        if (!TryReadJoints(out double[] q))
            return;
        SendEndpoint("set_goal", q);
        ghostSpawner?.SpawnGoalGhostFromPose(ikController, FerlJointNames.UnityArmNames, q);
        SetText(hintLabel, "goal = current pose");
    }

    public void IkEndpoints()
    {
        var writer = new FerlJsonWriter();
        writer.BeginObject().Key("command").Value("ik_endpoints").Key("client").Value(FerlClient.Id)
            .Key("start_object_id").Value(startObjectId)
            .Key("goal_object_id").Value(goalObjectId).EndObject();
        SendCommand(writer.ToString());
        SetText(hintLabel, $"endpoints: IK onto {startObjectId} -> {goalObjectId}");
    }

    public void Plan()
    {
        player?.Stop();
        SendCommand(Simple("plan"));
        SetText(hintLabel, "planning (about a minute) ...");
    }

    public void TogglePlay()
    {
        if (player == null)
            return;
        player.TogglePlay();
    }

    public void ToggleRobotTrace()
    {
        if (robotTraceRecorder == null)
            return;
        bool wasRecording = robotTraceRecorder.IsRecording;
        bool ok = wasRecording ? robotTraceRecorder.StopRecording() : robotTraceRecorder.StartRecording();
        ShowResult(robotTraceRecorder.LastMessage, !ok, pending: !wasRecording && ok);
        if (wasRecording && ok)
            lastResultMessage = "";  // let the bridge's "robot trace stored" acknowledgement replace it
        RefreshButtons();
    }

    public void ToggleEnvTrace()
    {
        if (envTraceRecorder == null)
            return;
        bool wasRecording = envTraceRecorder.IsRecording;
        if (!wasRecording)
        {
            // An env trace is pinned to the plan it was recorded against; recording while the
            // bridge is still planning (or with a stale local plan) is rejected later.
            FerlStatus s = LastStatus;
            if (s != null && s.busy && s.phase == "planning")
            {
                ShowResult("wait for the plan to finish before recording an env trace", true);
                return;
            }
            if (s != null && player != null && s.plan_seq != player.PlanSeq)
            {
                ShowResult($"the bridge's plan ({s.plan_seq}) is not the one shown here ({player.PlanSeq}): press Sync first", true);
                return;
            }
        }
        bool ok = wasRecording ? envTraceRecorder.StopRecording() : envTraceRecorder.StartRecording();
        ShowResult(envTraceRecorder.LastMessage, !ok, pending: !wasRecording && ok);
        if (wasRecording && ok)
            lastResultMessage = "";
        RefreshButtons();
    }

    public void EnvCorrection()
    {
        SendCommand(Simple("env_correction"));
        SetText(hintLabel, "environmental correction: E' > E at the fixed plan, replanning ...");
    }

    public void Learn()
    {
        player?.Stop();
        SendCommand(Simple("learn"));
        SetText(hintLabel, "learning + replanning (a minute or two) ...");
    }

    // Update the reward (new feature and/or reweight) but keep the current plan, so the
    // reward map can be inspected or more taught before spending a minute on a replan.
    public void LearnOnly()
    {
        player?.Stop();
        var writer = new FerlJsonWriter();
        writer.BeginObject().Key("command").Value("learn").Key("client").Value(FerlClient.Id).Key("replan").Value(false).EndObject();
        SendCommand(writer.ToString());
        SetText(hintLabel, "learning, plan kept (press Replan afterwards to apply the new reward) ...");
    }

    public void Replan()
    {
        player?.Stop();
        SendCommand(Simple("replan"));
        SetText(hintLabel, "replanning under the current reward (about a minute) ...");
    }

    public void ResetSession()
    {
        player?.Stop();
        SendCommand(Simple("reset"));
    }

    public void Save()
    {
        SendCommand(Simple("save"));
    }

    public void Sync()
    {
        SendCommand(Simple("sync"));
    }

    // Cycles off -> total cost -> each reward feature (known and learned) -> off. The bridge
    // samples reachable end-effector positions and sends their values; the visualizer draws them.
    public void CycleRewardMap()
    {
        var options = new System.Collections.Generic.List<string> { "off", "total" };
        if (LastStatus != null)
            options.AddRange(LastStatus.features);
        int index = options.IndexOf(rewardMapFeature);
        rewardMapFeature = options[(index + 1) % options.Count];
        RequestRewardMap(rewardMapFeature);
    }

    public void RequestRewardMap(string feature)
    {
        rewardMapFeature = feature;
        var writer = new FerlJsonWriter();
        writer.BeginObject().Key("command").Value("reward_map").Key("client").Value(FerlClient.Id)
            .Key("feature").Value(feature).Key("points").Value(rewardMapPoints).EndObject();
        SendCommand(writer.ToString());
        if (feature == "off")
        {
            rewardMap?.Clear();
            ShowResult("reward map hidden", false);
        }
        else
            ShowResult($"reward map: sampling {rewardMapPoints} states for '{feature}' ...", false, pending: true);
        RefreshButtons();
    }

    private void OnRewardMapUpdated(RewardMapVisualizer map)
    {
        if (map.IsVisible)
            ShowResult($"reward map '{map.CurrentFeature}': {map.PointCount} points, blue {map.MinValue:F2} -> red {map.MaxValue:F2}", false);
        RefreshButtons();
    }

    // ---- status ---------------------------------------------------------------------

    private void OnStatus(StringMsg msg)
    {
        FerlStatus status;
        try
        {
            status = JsonUtility.FromJson<FerlStatus>(msg.data);
        }
        catch (Exception error)
        {
            Debug.LogWarning($"FERLSessionMenuController: bad status JSON: {error.Message}");
            return;
        }
        if (status == null)
        {
            Debug.LogWarning("FERLSessionMenuController: status JSON parsed to null: " + msg.data);
            return;
        }
        LastStatus = status;
        lastStatusTime = Time.time;
        statusCount++;
        if (statusCount <= 3 || statusCount % 60 == 0)
            Debug.Log($"[FERLSessionMenu] status #{statusCount}: phase={status.phase} busy={status.busy} plan_seq={status.plan_seq} usable={status.plan_usable} msg='{status.message}' err='{status.error}'");
        if (!loggedHomeEe && status.home_ee != null && status.home_ee.Length == 3)
        {
            loggedHomeEe = true;
            Debug.Log($"[FERLSessionMenuController] Bridge home_ee (ROS base frame): ({status.home_ee[0]:F3}, {status.home_ee[1]:F3}, {status.home_ee[2]:F3}); compare with the SceneGraphPublisher TCP log.");
        }
        RefreshButtons();
        StatusChanged?.Invoke(status);
    }

    private void OnPlayStateChanged(bool playing) => RefreshButtons();

    private void RefreshButtons()
    {
        FerlStatus s = LastStatus;
        bool busy = s != null && s.busy;
        planButton?.SetEnabled(!busy);
        learnButton?.SetEnabled(!busy);
        learnOnlyButton?.SetEnabled(!busy);
        replanButton?.SetEnabled(!busy);
        envCorrectionButton?.SetEnabled(!busy);
        ikEndpointsButton?.SetEnabled(!busy);
        if (playStopButton != null)
            playStopButton.text = player != null && player.IsPlaying ? "Stop" : "Play plan";
        if (robotTraceButton != null)
            robotTraceButton.text = robotTraceRecorder != null && robotTraceRecorder.IsRecording ? "Stop robot trace" : "Record robot trace";
        if (envTraceButton != null)
            envTraceButton.text = envTraceRecorder != null && envTraceRecorder.IsRecording ? "Stop env trace" : "Record env trace";
        if (rewardMapButton != null)
            rewardMapButton.text = rewardMapFeature == "off" ? "Reward map: off" : $"Reward map: {rewardMapFeature}";

        if (s == null)
            return;
        string phase = s.busy ? $"{s.phase} ({s.elapsed:F0}s)" : s.phase;
        string plan = s.plan_seq > 0 ? $"plan {s.plan_seq}: {(s.plan_usable ? $"{s.plan_length} waypoints" : "unusable")}" : "no plan";
        string endpoints = $"{(s.has_start ? "start" : "-")}/{(s.has_goal ? "goal" : "-")}";
        string counts = $"traces {s.n_robot_traces}r/{s.n_env_traces}e ({s.traces_since_learn} new), corrections {s.n_corrections}";
        string conf = s.confidence >= 0f ? $"confidence {s.confidence:F2}/{s.confidence_threshold:F1}{(s.needs_traces ? " NEEDS TRACES" : "")}" : "";
        string error = string.IsNullOrEmpty(s.error) ? "" : $"\nERROR: {s.error}";
        SetText(statusLabel, $"[{phase}] {s.message}\n{plan} | {endpoints} | scene {s.scene_state}\n{counts}\n{conf}{error}");
        if (!s.busy && resultLabel != null)
        {
            // `result` is the outcome of the last user command; scene updates never touch it.
            // Older bridges have no `result`: fall back to the message unless it is a scene
            // update or a progress line.
            string fallback = s.message != null && !s.message.StartsWith("scene ") && !s.message.EndsWith("...") ? s.message : "";
            string result = !string.IsNullOrEmpty(s.error) ? "FAILED: " + s.error : (!string.IsNullOrEmpty(s.result) ? s.result : fallback);
            if (!string.IsNullOrEmpty(result) && result != lastResultMessage)
            {
                lastResultMessage = result;
                ShowResult(result, !string.IsNullOrEmpty(s.error) || result.Contains("failed") || result.Contains("cannot explain") || result.Contains("discarded"));
            }
        }
        if (!s.busy && string.IsNullOrEmpty(s.error))
        {
            string next = !s.has_start || !s.has_goal ? "next: IK endpoints (or Set start / Set goal)"
                : s.plan_seq == 0 || !s.plan_usable ? "next: Plan"
                : s.n_corrections == 0 ? "next: Play plan and drag the end effector to correct, or record traces"
                : s.needs_traces ? "next: record a robot or env trace, then Learn (+ replan, or Learn only then Replan)"
                : "next: Learn + replan (or Learn only, then Replan)";
            SetText(hintLabel, next);
        }

        var features = new System.Text.StringBuilder();
        for (int i = 0; i < s.features.Length; i++)
        {
            float w = i < s.weights.Length ? s.weights[i] : 0f;
            features.Append(i > 0 ? "  " : "").Append(s.features[i]).Append('=').Append(w.ToString("F2"));
        }
        SetText(featuresLabel, features.Length > 0 ? features.ToString() : "(no reward yet)");
        if (!string.IsNullOrEmpty(s.error))
            SetText(hintLabel, s.error);
    }
}
