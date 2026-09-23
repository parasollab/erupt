using System;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

/// <summary>
/// Shows, on every object AprilTag the tracker sees, whether the robot can reach that object.
/// Reachability comes from MoveIt's /compute_ik service, so it reflects the planner's own joint
/// limits and (optionally) the planning-scene collision state. Only the position matters: a short
/// list of candidate tool orientations is tried in turn and the first solvable one wins.
/// Everything robot-specific is derived from <see cref="robot"/>: an optional
/// <see cref="RobotReachProfile"/> on it, a child named BaseTransform as the ROS base frame, and
/// its live pose at query time, so swapping the robot prefab is enough to keep this working.
/// Results are cached per tag and re-queried only when the tag or the robot moves.
/// </summary>
public class TagReachabilityIndicator : MonoBehaviour
{
    public enum ReachState { Unknown, Querying, Reachable, Unreachable, Stale, NoReply, NoRos }

    [Serializable]
    public class DebugTag
    {
        [Tooltip("Tag ID to fake. Must not be the tracker's primary tag ID.")]
        public int id = 1;

        [Tooltip("Position relative to the robot root (Unity axes: +z is the robot's forward, +y up).")]
        public Vector3 offsetFromRobotRoot = new Vector3(0f, 0.05f, 0.4f);
    }

    private const string LogTag = "[TagReach]";
    private const string BaseTransformName = "BaseTransform";
    private static readonly Vector3 RosZ = new Vector3(0f, 0f, 1f);

    [Header("Robot")]
    [SerializeField, Tooltip("Root of the robot prefab instance. Everything robot-specific is derived from it: a " +
                             "RobotReachProfile on it (planning group, frame, IK link), a child named BaseTransform " +
                             "as the ROS base frame, and its pose at query time. Swap the robot by swapping this.")]
    private GameObject robot;

    [SerializeField, Tooltip("Optional. When set, the Unity arm's joint positions seed the IK request; otherwise " +
                             "move_group solves from its current robot state. Found in the scene when left empty.")]
    private DirectArticulationIKController seedController;

    [Header("Robot defaults (used when the robot has no RobotReachProfile)")]
    [SerializeField, Tooltip("MoveIt planning group /compute_ik solves for.")]
    private string planningGroupName = "ur_manipulator";

    [SerializeField, Tooltip("IK tip link. Empty = the group's SRDF tip link.")]
    private string ikLinkName = "";

    [SerializeField, Tooltip("ROS frame the query pose is expressed in. Must be a link of the robot.")]
    private string planningFrameId = "base_link";

    [SerializeField, Tooltip("Joint-name prefix of the Unity robot, rewritten to rosJointNamePrefix in the IK seed. " +
                             "Empty = send joint names unchanged.")]
    private string unityJointNamePrefix = "";

    [SerializeField, Tooltip("Joint-name prefix of the ROS robot model (e.g. \"panda_\"). See unityJointNamePrefix.")]
    private string rosJointNamePrefix = "";

    [SerializeField, Tooltip("Yaw about Unity Y from the robot root to the Unity transform matching planningFrameId, " +
                             "used only when no BaseTransform child exists. -90 matches the study scenes' convention.")]
    private float baseYawOffsetDegrees = -90f;

    [Header("Tags")]
    [SerializeField, Tooltip("Tracker that reports object tags through OnTagObserved.")]
    private AprilTagTracker tracker;

    [SerializeField, Tooltip("Tag IDs to show indicators for. Empty = every tag the tracker reports except its primary " +
                             "(robot) tag. Must be listed explicitly when the tracker's primary ID is -1.")]
    private List<int> objectTagIds = new List<int>();

    [SerializeField, Tooltip("Seconds after the last observation before an indicator starts fading out.")]
    private float hideAfterSeconds = 3f;

    [SerializeField, Tooltip("Fade-out duration once a tag has been unseen for hideAfterSeconds.")]
    private float fadeSeconds = 0.5f;

    [Header("IK query")]
    [SerializeField, Tooltip("move_group's kinematics service.")]
    private string ikServiceName = "/compute_ik";

    [SerializeField, Tooltip("Send a single request with identity orientation. Only valid when the ROS side has " +
                             "position_only_ik: true in kinematics.yaml; otherwise candidate orientations are tried.")]
    private bool positionOnlyIk = false;

    [SerializeField, Tooltip("Ask for a collision-free solution against the current planning scene.")]
    private bool avoidCollisions = true;

    [SerializeField, Tooltip("Query point = tag centre + outward tag normal * this distance, so the tool hovers above " +
                             "the object instead of touching it (and its collision object).")]
    private float standoffDistance = 0.05f;

    [SerializeField, Tooltip("Per-request solver time budget sent to MoveIt (KDL restarts from random seeds until it expires).")]
    private float ikTimeoutSeconds = 0.05f;

    [SerializeField, Tooltip("Give up on a request after this long. The ROS connector itself never times out.")]
    private float serviceReplyTimeoutSeconds = 3f;

    [SerializeField, Tooltip("Re-query a tag when it has moved this far (metres) since its last answer.")]
    private float requeryDistance = 0.02f;

    [SerializeField, Tooltip("Re-query every tag when the robot base has moved this far (metres).")]
    private float robotMoveDistance = 0.01f;

    [SerializeField, Tooltip("Re-query every tag when the robot base has rotated this much (degrees).")]
    private float robotMoveAngle = 1f;

    [SerializeField, Tooltip("Re-query a tag whose answer is older than this, even if nothing moved (planning scene may have changed).")]
    private float maxResultAgeSeconds = 30f;

    [SerializeField, Tooltip("Global minimum spacing between requests. Only one request is ever in flight.")]
    private float minSecondsBetweenRequests = 0.1f;

    [SerializeField, Tooltip("Retry interval after a request timed out or ROS was unavailable.")]
    private float noRosRetrySeconds = 5f;

    [Header("Indicator")]
    [SerializeField, Tooltip("Transparent URP/Lit material for the disc (a serialized asset so the transparent shader " +
                             "variant is kept in Android builds). Colour is overridden per indicator.")]
    private Material discMaterial;

    [SerializeField, Tooltip("Font for the label. Empty = TMP default.")]
    private TMP_FontAsset labelFont;

    [SerializeField, Tooltip("Transform the labels face (the headset camera). Empty = Camera.main.")]
    private Transform billboardCamera;

    [SerializeField, Tooltip("Disc diameter as a multiple of the tag's detection-square size.")]
    private float discDiameterFactor = 1.4f;

    [SerializeField, Tooltip("Label height above the disc as a multiple of the tag size.")]
    private float labelHeightFactor = 0.9f;

    [SerializeField, Tooltip("Label scale; 0.15 gives roughly 1.5 cm tall glyphs.")]
    private float labelScale = 0.15f;

    [SerializeField] private Color reachableColor = new Color(0.1f, 0.9f, 0.2f, 0.5f);
    [SerializeField] private Color unreachableColor = new Color(0.95f, 0.15f, 0.1f, 0.5f);
    [SerializeField] private Color checkingColor = new Color(0.7f, 0.7f, 0.7f, 0.45f);
    [SerializeField] private Color noRosColor = new Color(0.4f, 0.4f, 0.4f, 0.3f);

    [Header("Debug")]
    [SerializeField, Tooltip("Fake tags injected by the 'Inject debug tags' context menu, for editor testing with ROS.")]
    private List<DebugTag> debugTags = new List<DebugTag>();

    [SerializeField] private bool verboseLogging = false;

    private class TagEntry
    {
        public int id;
        public float size;
        public Pose tagPose;
        public float lastSeen;
        public ReachabilityIndicatorView view;
        public ReachState state = ReachState.Unknown;
        public bool hasResult;
        public bool lastResult;
        public Vector3 queriedWorldPos;
        public float resultTime = float.NegativeInfinity;
        public QuaternionMsg[] candidates;
        public int candidateIndex;
        public bool inFlight;
        public int serial;
        public float sentTime;
        public bool loggedTimeout;
        public bool loggedError;
        public bool viewDirty = true;
    }

    private readonly Dictionary<int, TagEntry> _entries = new Dictionary<int, TagEntry>();
    private readonly HashSet<string> _warnedSeedJoints = new HashSet<string>();
    private ROSConnection _ros;
    private RobotReachProfile _profile;
    private Transform _baseTransform;
    private bool _hasLastAnchor;
    private Pose _lastAnchor;
    private bool _requestInFlight;
    private float _lastSendTime = float.NegativeInfinity;
    private bool _rosWasOk = true;
    private bool _serviceRegistered;
    private bool _subscribed;

    private string GroupName => _profile != null && !string.IsNullOrEmpty(_profile.planningGroupName) ? _profile.planningGroupName : planningGroupName;
    private string IkLinkName => _profile != null ? (_profile.ikLinkName ?? "") : (ikLinkName ?? "");
    private string FrameId => _profile != null && !string.IsNullOrEmpty(_profile.planningFrameId) ? _profile.planningFrameId : planningFrameId;
    private float BaseYawOffset => _profile != null ? _profile.baseYawOffsetDegrees : baseYawOffsetDegrees;
    private string UnityJointPrefix => _profile != null ? (_profile.unityJointNamePrefix ?? "") : (unityJointNamePrefix ?? "");
    private string RosJointPrefix => _profile != null ? (_profile.rosJointNamePrefix ?? "") : (rosJointNamePrefix ?? "");

    /// <summary>Current state for a tag ID, if it has ever been observed.</summary>
    public bool TryGetState(int id, out ReachState state)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            state = entry.state;
            return true;
        }
        state = ReachState.Unknown;
        return false;
    }

    private void Awake()
    {
        if (robot == null)
            Debug.LogError($"{LogTag} 'robot' is not assigned; reachability queries are disabled.");
        if (tracker == null)
            Debug.LogError($"{LogTag} 'tracker' is not assigned; no tags will be observed.");
        if (discMaterial == null)
            Debug.LogWarning($"{LogTag} 'discMaterial' is not assigned; discs will use the primitive default material (opaque, may be stripped in builds).");

        if (robot != null)
            _profile = robot.GetComponentInChildren<RobotReachProfile>(true);
        if (seedController == null)
            seedController = FindFirstObjectByType<DirectArticulationIKController>(FindObjectsInactive.Exclude);
        if (billboardCamera == null && Camera.main != null)
            billboardCamera = Camera.main.transform;
    }

    private void Start()
    {
        _ros = ROSConnection.GetOrCreateInstance();
        try
        {
            _ros.RegisterRosService<GetPositionIKRequest, GetPositionIKResponse>(ikServiceName);
            _serviceRegistered = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{LogTag} Could not register {ikServiceName}: {e.Message}");
        }
        if (verboseLogging)
            Debug.Log($"{LogTag} group={GroupName} frame={FrameId} ikLink='{IkLinkName}' profile={(_profile != null ? "yes" : "none")} seed={(seedController != null ? seedController.name : "move_group current state")} jointPrefix='{UnityJointPrefix}'->'{RosJointPrefix}'");
    }

    private void OnEnable()
    {
        if (tracker != null && !_subscribed)
        {
            tracker.OnTagObserved += OnTagObserved;
            _subscribed = true;
        }
    }

    private void OnDisable()
    {
        if (tracker != null && _subscribed)
        {
            tracker.OnTagObserved -= OnTagObserved;
            _subscribed = false;
        }
    }

    private void OnDestroy()
    {
        OnDisable();
        ClearIndicators();
    }

    private bool IsObjectTag(int id)
    {
        if (objectTagIds != null && objectTagIds.Count > 0)
            return objectTagIds.Contains(id);
        return tracker == null || id != tracker.PrimaryTagId;
    }

    private void OnTagObserved(AprilTagTracker.TrackedTag tag)
    {
        if (!IsObjectTag(tag.Id))
            return;

        if (!_entries.TryGetValue(tag.Id, out var entry))
        {
            entry = new TagEntry { id = tag.Id, size = tag.Size };
            entry.view = ReachabilityIndicatorView.Create($"ReachIndicator {tag.Id}", transform, discMaterial, labelFont, billboardCamera, labelScale);
            entry.view.Init(tag.Size * discDiameterFactor, tag.Size * labelHeightFactor);
            _entries.Add(tag.Id, entry);
            if (verboseLogging)
                Debug.Log($"{LogTag} tag {tag.Id} first seen (size {tag.Size:F3} m)");
        }

        entry.size = tag.Size;
        entry.tagPose = tag.FilteredPose;
        entry.lastSeen = tag.LastSeenTime;

        if (entry.hasResult && !entry.inFlight && entry.state != ReachState.Querying &&
            Vector3.Distance(entry.tagPose.position, entry.queriedWorldPos) > requeryDistance)
        {
            entry.state = ReachState.Stale;
            entry.viewDirty = true;
        }
    }

    private void Update()
    {
        if (robot == null || tracker == null)
            return;

        Pose anchor = ResolveBaseAnchor();
        if (!_hasLastAnchor)
        {
            _lastAnchor = anchor;
            _hasLastAnchor = true;
        }
        else if (Vector3.Distance(anchor.position, _lastAnchor.position) > robotMoveDistance ||
                 Quaternion.Angle(anchor.rotation, _lastAnchor.rotation) > robotMoveAngle)
        {
            _lastAnchor = anchor;
            if (verboseLogging)
                Debug.Log($"{LogTag} robot base moved; re-querying all tags");
            foreach (var entry in _entries.Values)
                Invalidate(entry);
        }

        bool rosOk = _ros != null && _serviceRegistered && !_ros.HasConnectionError;
        if (rosOk != _rosWasOk)
        {
            _rosWasOk = rosOk;
            foreach (var entry in _entries.Values)
            {
                if (!rosOk)
                {
                    if (entry.inFlight)
                        Invalidate(entry);
                    entry.state = ReachState.NoRos;
                }
                else if (entry.state == ReachState.NoRos)
                {
                    entry.state = entry.hasResult ? ReachState.Stale : ReachState.Unknown;
                }
                entry.viewDirty = true;
            }
        }

        foreach (var entry in _entries.Values)
        {
            UpdateViewPlacement(entry);

            if (entry.inFlight && Time.time - entry.sentTime > serviceReplyTimeoutSeconds)
            {
                if (!entry.loggedTimeout)
                {
                    entry.loggedTimeout = true;
                    Debug.LogWarning($"{LogTag} tag {entry.id}: no reply from {ikServiceName} within {serviceReplyTimeoutSeconds:F1}s");
                }
                Invalidate(entry);
                entry.state = ReachState.NoReply;
                entry.resultTime = Time.time;
                entry.viewDirty = true;
            }

            if (entry.viewDirty)
                ApplyViewState(entry);
        }

        if (rosOk && !_requestInFlight && Time.time - _lastSendTime >= minSecondsBetweenRequests)
        {
            TagEntry next = PickEntryToQuery();
            if (next != null)
                SendCandidate(next, anchor);
        }
    }

    private void UpdateViewPlacement(TagEntry entry)
    {
        if (entry.view == null)
            return;
        float age = Time.time - entry.lastSeen;
        float visibility = age < hideAfterSeconds ? 1f : 1f - (age - hideAfterSeconds) / Mathf.Max(fadeSeconds, 0.01f);
        entry.view.SetVisibility(visibility);
        if (visibility <= 0f)
            return;
        Vector3 normal = entry.tagPose.rotation * Vector3.back;   // tag z points into the tag
        // Same convention as the tracker's default tagToObjectEuler: disc up = outward tag normal.
        entry.view.SetPose(entry.tagPose.position + normal * 0.002f, entry.tagPose.rotation * Quaternion.Euler(-90f, 0f, 0f));
    }

    private bool IsVisible(TagEntry entry) => Time.time - entry.lastSeen < hideAfterSeconds;

    private bool NeedsQuery(TagEntry entry)
    {
        if (entry.inFlight || !IsVisible(entry))
            return false;
        switch (entry.state)
        {
            case ReachState.Unknown:
            case ReachState.Stale:
            case ReachState.Querying:       // sequence in progress, next candidate pending
                return true;
            case ReachState.Reachable:
            case ReachState.Unreachable:
                return Time.time - entry.resultTime > maxResultAgeSeconds;
            case ReachState.NoReply:
                return Time.time - entry.resultTime > noRosRetrySeconds;
            default:
                return false;
        }
    }

    private TagEntry PickEntryToQuery()
    {
        TagEntry best = null;
        foreach (var entry in _entries.Values)
        {
            if (!NeedsQuery(entry))
                continue;
            // Finish a sequence that is already under way before starting another tag.
            if (entry.state == ReachState.Querying)
                return entry;
            if (best == null || entry.resultTime < best.resultTime)
                best = entry;
        }
        return best;
    }

    private void Invalidate(TagEntry entry)
    {
        if (entry.inFlight)
        {
            entry.inFlight = false;
            _requestInFlight = false;
        }
        entry.serial++;
        entry.candidateIndex = 0;
        entry.candidates = null;
        if (entry.hasResult)
            entry.state = ReachState.Stale;
        else if (entry.state == ReachState.Querying)
            entry.state = ReachState.Unknown;
        entry.viewDirty = true;
    }

    private void SendCandidate(TagEntry entry, Pose anchor)
    {
        if (entry.candidates == null || entry.candidateIndex == 0)
        {
            entry.candidates = BuildCandidates(entry, anchor);
            entry.candidateIndex = 0;
            entry.queriedWorldPos = entry.tagPose.position;
        }

        GetPositionIKRequest request;
        try
        {
            request = BuildRequest(entry, anchor, entry.candidates[entry.candidateIndex]);
        }
        catch (Exception e)
        {
            Debug.LogError($"{LogTag} tag {entry.id}: failed to build IK request: {e.Message}");
            entry.state = ReachState.NoReply;
            entry.resultTime = Time.time;
            entry.viewDirty = true;
            return;
        }

        entry.serial++;
        int serial = entry.serial;
        entry.inFlight = true;
        entry.sentTime = Time.time;
        entry.loggedTimeout = false;
        entry.state = ReachState.Querying;
        entry.viewDirty = true;
        _requestInFlight = true;
        _lastSendTime = Time.time;

        if (verboseLogging)
        {
            var p = request.ik_request.pose_stamped.pose.position;
            Debug.Log($"{LogTag} tag {entry.id}: candidate {entry.candidateIndex + 1}/{entry.candidates.Length} -> ({p.x:F3}, {p.y:F3}, {p.z:F3}) in {FrameId}");
        }

        try
        {
            _ros.SendServiceMessage<GetPositionIKResponse>(ikServiceName, request, response => OnIkResponse(entry, serial, response));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{LogTag} tag {entry.id}: {ikServiceName} call failed: {e.Message}");
            Invalidate(entry);
            entry.state = ReachState.NoReply;
            entry.resultTime = Time.time;
            entry.viewDirty = true;
        }
    }

    private void OnIkResponse(TagEntry entry, int serial, GetPositionIKResponse response)
    {
        if (serial != entry.serial)
            return;                         // abandoned (tag/robot moved, timeout, or ROS dropped)

        entry.inFlight = false;
        _requestInFlight = false;

        int code = response?.error_code?.val ?? MoveItErrorCodesMsg.FAILURE;
        if (code == MoveItErrorCodesMsg.SUCCESS)
        {
            SetResult(entry, true, entry.candidateIndex);
            return;
        }

        if (code == MoveItErrorCodesMsg.NO_IK_SOLUTION ||
            code == MoveItErrorCodesMsg.GOAL_IN_COLLISION ||
            code == MoveItErrorCodesMsg.TIMED_OUT)
        {
            entry.candidateIndex++;
            if (entry.candidateIndex >= entry.candidates.Length)
                SetResult(entry, false, entry.candidates.Length);
            // else: stay Querying; Update sends the next candidate under the global throttle.
            return;
        }

        if (!entry.loggedError)
        {
            entry.loggedError = true;
            Debug.LogError($"{LogTag} tag {entry.id}: {ikServiceName} returned error {code}; check planning group '{GroupName}', frame '{FrameId}' and IK link '{IkLinkName}'.");
        }
        entry.state = ReachState.NoReply;
        entry.resultTime = Time.time;
        entry.candidateIndex = 0;
        entry.viewDirty = true;
    }

    private void SetResult(TagEntry entry, bool reachable, int candidatesTried)
    {
        entry.state = reachable ? ReachState.Reachable : ReachState.Unreachable;
        entry.hasResult = true;
        entry.lastResult = reachable;
        entry.resultTime = Time.time;
        entry.candidateIndex = 0;
        entry.viewDirty = true;
        Debug.Log(reachable
            ? $"{LogTag} tag {entry.id}: reachable (candidate {candidatesTried + 1})"
            : $"{LogTag} tag {entry.id}: unreachable ({candidatesTried} candidate orientations failed)");
    }

    private void ApplyViewState(TagEntry entry)
    {
        entry.viewDirty = false;
        if (entry.view == null)
            return;

        Color previous = entry.lastResult ? reachableColor : unreachableColor;
        Color dimmed = new Color(previous.r, previous.g, previous.b, previous.a * 0.6f);
        switch (entry.state)
        {
            case ReachState.Reachable:
                entry.view.SetState(reachableColor, "Reachable");
                break;
            case ReachState.Unreachable:
                entry.view.SetState(unreachableColor, "Unreachable");
                break;
            case ReachState.NoRos:
                entry.view.SetState(noRosColor, "No ROS");
                break;
            case ReachState.NoReply:
                entry.view.SetState(entry.hasResult ? dimmed : noRosColor, "No reply");
                break;
            default:    // Unknown, Stale, Querying
                entry.view.SetState(entry.hasResult ? dimmed : checkingColor, "Checking…");
                break;
        }
    }

    // ---- Frames and request construction -------------------------------------------------

    /// <summary>
    /// Unity pose of the ROS planning frame. Preference: RobotReachProfile.baseAnchor, then a child
    /// named BaseTransform under the robot, then the robot root yawed by the configured offset.
    /// </summary>
    private Pose ResolveBaseAnchor()
    {
        if (_profile != null && _profile.baseAnchor != null)
            return new Pose(_profile.baseAnchor.position, _profile.baseAnchor.rotation);

        if (_baseTransform == null && robot != null)
        {
            foreach (Transform t in robot.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == BaseTransformName)
                {
                    _baseTransform = t;
                    break;
                }
            }
        }
        if (_baseTransform != null)
            return new Pose(_baseTransform.position, _baseTransform.rotation);

        Transform root = robot.transform;
        return new Pose(root.position, root.rotation * Quaternion.Euler(0f, BaseYawOffset, 0f));
    }

    /// <summary>Anchor-local Unity vector to ROS axes; same swap as Conversions.cs.</summary>
    private static Vector3 ToRosVector(Vector3 unityLocal) => new Vector3(unityLocal.x, unityLocal.z, unityLocal.y);

    private Vector3 QueryPointWorld(TagEntry entry)
    {
        Vector3 normal = entry.tagPose.rotation * Vector3.back;
        return entry.tagPose.position + normal * standoffDistance;
    }

    private GetPositionIKRequest BuildRequest(TagEntry entry, Pose anchor, QuaternionMsg orientation)
    {
        Vector3 local = Quaternion.Inverse(anchor.rotation) * (QueryPointWorld(entry) - anchor.position);

        var poseStamped = new PoseStampedMsg
        {
            header = new HeaderMsg { frame_id = FrameId },
            pose = new PoseMsg(RosUnityConversion.UnityToRosPosition(local), orientation)
        };

        var ik = new PositionIKRequestMsg
        {
            group_name = GroupName,
            robot_state = BuildSeedState(),
            avoid_collisions = avoidCollisions,
            ik_link_name = IkLinkName,
            pose_stamped = poseStamped,
            timeout = new DurationMsg { sec = 0, nanosec = (uint)Mathf.Max(0f, ikTimeoutSeconds * 1e9f) }
        };
        return new GetPositionIKRequest(ik);
    }

    private RobotStateMsg BuildSeedState()
    {
        // is_diff = true: an empty state means "start from move_group's current state" without warnings.
        var state = new RobotStateMsg { is_diff = true };
        if (seedController == null)
            return state;

        string[] unityNames = seedController.GetJointStateNames();
        float[] unityPositions = seedController.GetJointStatePositions();
        if (unityNames == null || unityPositions == null || unityNames.Length == 0 || unityNames.Length != unityPositions.Length)
            return state;

        // A joint name the ROS model does not know is not an IK error: move_group's IK service
        // throws on it and the whole process aborts. Only send names we can vouch for.
        var names = new List<string>(unityNames.Length);
        var positions = new List<double>(unityNames.Length);
        for (int i = 0; i < unityNames.Length; i++)
        {
            if (TryMapJointName(unityNames[i], out string rosName))
            {
                names.Add(rosName);
                positions.Add(unityPositions[i]);
            }
            else if (_warnedSeedJoints.Add(unityNames[i]))
            {
                Debug.LogWarning($"{LogTag} seed joint '{unityNames[i]}' does not carry the Unity prefix '{UnityJointPrefix}'; left out of the IK seed.");
            }
        }
        if (names.Count == 0)
            return state;

        state.joint_state = new JointStateMsg
        {
            name = names.ToArray(),
            position = positions.ToArray(),
            velocity = new double[names.Count],
            effort = new double[names.Count]
        };
        return state;
    }

    /// <summary>
    /// Rewrites a Unity joint name to the ROS model's name. With no prefixes configured the name
    /// passes through. With a Unity prefix configured, a name that does not carry it cannot be
    /// mapped and is rejected rather than sent as-is.
    /// </summary>
    private bool TryMapJointName(string unityName, out string rosName)
    {
        string unityPrefix = UnityJointPrefix;
        if (string.IsNullOrEmpty(unityPrefix))
        {
            rosName = unityName;
            return !string.IsNullOrEmpty(unityName);
        }
        if (!string.IsNullOrEmpty(unityName) && unityName.StartsWith(unityPrefix, StringComparison.Ordinal))
        {
            rosName = RosJointPrefix + unityName.Substring(unityPrefix.Length);
            return true;
        }
        rosName = null;
        return false;
    }

    /// <summary>Quaternion (ROS axes) that points the tool's +z along <paramref name="toolDirectionRos"/>.</summary>
    private static QuaternionMsg WithToolAxis(Vector3 toolDirectionRos)
    {
        toolDirectionRos.Normalize();
        // Pure algebra on ROS-axis vectors; quaternion composition is the same in Unity and tf.
        Quaternion q = Quaternion.FromToRotation(RosZ, toolDirectionRos);
        return new QuaternionMsg(q.x, q.y, q.z, q.w);
    }

    private QuaternionMsg[] BuildCandidates(TagEntry entry, Pose anchor)
    {
        if (positionOnlyIk)
            return new[] { new QuaternionMsg(0, 0, 0, 1) };

        Quaternion inv = Quaternion.Inverse(anchor.rotation);
        Vector3 normalRos = ToRosVector(inv * (entry.tagPose.rotation * Vector3.back)).normalized;
        Vector3 pointRos = ToRosVector(inv * (QueryPointWorld(entry) - anchor.position));
        Vector3 radial = new Vector3(pointRos.x, pointRos.y, 0f);
        radial = radial.sqrMagnitude > 1e-6f ? radial.normalized : Vector3.right;
        Vector3 down = -RosZ;

        var directions = new List<Vector3>
        {
            down,                                              // straight down (classic pick pose)
            -normalRos,                                        // into the tag along its normal (tilted/vertical tags)
            Vector3.Slerp(down, radial, 30f / 90f),            // 30 deg tilt away from the base
            Vector3.Slerp(down, radial, 60f / 90f),            // 60 deg
            radial,                                            // horizontal, pointing away from the base
            Vector3.Slerp(down, -radial, 30f / 90f),           // 30 deg tilt back toward the base
        };

        var result = new List<QuaternionMsg>(directions.Count);
        var used = new List<Vector3>(directions.Count);
        foreach (var dir in directions)
        {
            bool duplicate = false;
            foreach (var u in used)
            {
                if (Vector3.Angle(u, dir) < 5f)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
                continue;
            used.Add(dir);
            result.Add(WithToolAxis(dir));
        }
        return result.ToArray();
    }

    // ---- Debug helpers -----------------------------------------------------------------------

    [ContextMenu("Inject debug tags")]
    private void InjectDebugTags()
    {
        if (!Application.isPlaying || robot == null || tracker == null)
        {
            Debug.LogWarning($"{LogTag} Inject debug tags needs play mode with robot and tracker assigned.");
            return;
        }
        foreach (var debugTag in debugTags)
        {
            if (debugTag.id == tracker.PrimaryTagId)
            {
                Debug.LogWarning($"{LogTag} debug tag {debugTag.id} is the primary tag ID; skipped.");
                continue;
            }
            Vector3 position = robot.transform.TransformPoint(debugTag.offsetFromRobotRoot);
            // Tag lying flat, facing up: z (into the tag) points down, y (tag up) points along the robot's forward.
            Quaternion rotation = Quaternion.LookRotation(Vector3.down, robot.transform.forward);
            tracker.InjectDebugObservation(debugTag.id, 0.05f, new Pose(position, rotation));
        }
    }

    [ContextMenu("Re-query all tags")]
    private void RequeryAll()
    {
        foreach (var entry in _entries.Values)
            Invalidate(entry);
    }

    [ContextMenu("Clear indicators")]
    private void ClearIndicators()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.view != null)
                Destroy(entry.view.gameObject);
        }
        _entries.Clear();
        _requestInFlight = false;
    }

    [ContextMenu("Log frame sanity check")]
    private void LogFrameSanityCheck()
    {
        if (robot == null)
        {
            Debug.LogWarning($"{LogTag} robot not assigned.");
            return;
        }
        Pose anchor = ResolveBaseAnchor();
        Transform root = robot.transform;
        Vector3 forward = root.position + root.forward * 0.5f;   // expect ROS (0.5, 0, 0)
        Vector3 left = root.position - root.right * 0.5f;        // expect ROS (0, 0.5, 0)
        Vector3 up = root.position + root.up * 0.5f;             // expect ROS (0, 0, 0.5)
        foreach (var (label, world, expected) in new[] { ("root +z", forward, "(0.5, 0, 0)"), ("root -x", left, "(0, 0.5, 0)"), ("root +y", up, "(0, 0, 0.5)") })
        {
            Vector3 local = Quaternion.Inverse(anchor.rotation) * (world - anchor.position);
            var ros = RosUnityConversion.UnityToRosPosition(local);
            Debug.Log($"{LogTag} {label} -> ROS ({ros.x:F2}, {ros.y:F2}, {ros.z:F2}) in {FrameId}; expected {expected}. Anchor source: {(_profile != null && _profile.baseAnchor != null ? "profile.baseAnchor" : _baseTransform != null ? "BaseTransform child" : $"root yawed {BaseYawOffset} deg")}");
        }
    }
}
