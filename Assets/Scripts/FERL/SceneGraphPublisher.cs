using System;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Collects every SceneGraphObject into preference_rl's SceneGraph JSON and publishes it on
// /ferl/scene_graph whenever the scene changes (plus a slow heartbeat so a bridge started
// late still gets it). Also the one place the workspace bounds and carried object live.
public class SceneGraphPublisher : MonoBehaviour
{
    public static SceneGraphPublisher Instance { get; private set; }

    [Header("Frame")]
    [Tooltip("The BaseTransform child of the robot root (yaw -90): objects are expressed relative to it.")]
    [SerializeField] private Transform baseTransform;
    [Tooltip("Optional: the robot's TCP transform, logged once in ROS coordinates for the frame check.")]
    [SerializeField] private Transform endEffectorForFrameCheck;
    [Tooltip("Where a carried object snaps to (defaults to the frame-check transform, i.e. the TCP).")]
    [SerializeField] private Transform carryAnchor;
    [SerializeField, Range(0.05f, 1f)] private float carriedAlpha = 0.35f;

    [Header("Workspace (ROS base frame, metres)")]
    [SerializeField] private Vector3 workspaceMinRos = new Vector3(0.22f, -0.40f, 0.10f);
    [SerializeField] private Vector3 workspaceMaxRos = new Vector3(0.78f, 0.45f, 0.45f);
    [SerializeField] private bool drawWorkspaceGizmo = true;

    [Header("ROS")]
    [SerializeField] private string sceneTopic = "/ferl/scene_graph";
    [SerializeField] private float heartbeatSeconds = 2f;
    [Tooltip("Grab releases that moved an object less than this (metres) are not reported as edits.")]
    [SerializeField] private float minReportedDisplacement = 0.005f;
    [Tooltip("Grab releases that turned an object less than this (degrees about ROS z) are not reported as edits.")]
    [SerializeField] private float minReportedTurnDegrees = 5f;

    public Transform BaseTransform => baseTransform;
    public string CarriedObjectId { get; private set; }
    public SceneGraphSnapshot LastSnapshot { get; private set; }
    public string LastJson { get; private set; }

    public event Action<SceneGraphSnapshot, SceneEditData> Edited;
    public event Action<SceneGraphSnapshot> Published;

    private ROSConnection ros;
    private bool dirty = true;
    private float nextHeartbeat;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Start()
    {
        if (baseTransform == null)
        {
            GameObject found = GameObject.Find("BaseTransform");
            if (found != null)
                baseTransform = found.transform;
            else
                Debug.LogError("[SceneGraphPublisher] No BaseTransform assigned or found; positions will be world-frame.");
        }

        ros = ROSConnection.GetOrCreateInstance();
        StringMsg.Register();
        ros.RegisterPublisher<StringMsg>(sceneTopic);
        LogFrameCheck();
        PublishNow();
    }

    private void Update()
    {
        if (dirty || Time.time >= nextHeartbeat)
            PublishNow();
    }

    public SceneGraphSnapshot Snapshot()
    {
        var snapshot = new SceneGraphSnapshot { carriedObjectId = CarriedObjectId };
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (SceneGraphObject obj in SceneGraphObject.All)
        {
            if (obj == null || !obj.isActiveAndEnabled)
                continue;
            if (string.IsNullOrEmpty(obj.ObjectId))
                obj.ObjectId = SceneGraphObject.SanitizeId(obj.gameObject.name);
            if (!seen.Add(obj.ObjectId))
            {
                Debug.LogWarning($"[SceneGraphPublisher] Duplicate object id '{obj.ObjectId}' on '{obj.name}'; skipping it.");
                continue;
            }
            snapshot.objects.Add(obj.ComputeNode(baseTransform));
        }
        for (int axis = 0; axis < 3; axis++)
        {
            snapshot.workspaceLow[axis] = Mathf.Min(workspaceMinRos[axis], workspaceMaxRos[axis]);
            snapshot.workspaceHigh[axis] = Mathf.Max(workspaceMinRos[axis], workspaceMaxRos[axis]);
        }
        if (CarriedObjectId != null && !seen.Contains(CarriedObjectId))
        {
            CarriedObjectId = null;
            snapshot.carriedObjectId = null;
        }
        return snapshot;
    }

    public void PublishNow()
    {
        LastSnapshot = Snapshot();
        LastJson = LastSnapshot.ToJson();
        dirty = false;
        nextHeartbeat = Time.time + Mathf.Max(0.5f, heartbeatSeconds);
        if (ros != null)
            ros.Publish(sceneTopic, new StringMsg(LastJson));
        Published?.Invoke(LastSnapshot);
    }

    public void MarkDirty()
    {
        dirty = true;
    }

    // A leg of a drag (a grab release, or a mid-grab sample while an env trace records): a
    // displacement edit when the object moved or turned enough to count, else just a dirty
    // scene. The turn (radians about ROS z) rides along as trace metadata; the bridge treats
    // both as pose changes of the same object, never as a new shape. Returns whether an edit
    // was reported.
    public bool NotifyMoved(SceneGraphObject obj, Vector3 displacementRos, double turnRos = 0.0)
    {
        bool moved = displacementRos.magnitude >= minReportedDisplacement;
        bool turned = Math.Abs(turnRos) * Mathf.Rad2Deg >= minReportedTurnDegrees;
        if (!moved && !turned)
        {
            MarkDirty();
            return false;
        }
        NotifyEdit(new SceneEditData
        {
            objectId = obj.ObjectId,
            displacement = new double[] { displacementRos.x, displacementRos.y, displacementRos.z },
            yaw = turned ? turnRos : (double?)null,
        });
        return true;
    }

    // An edit in preference_rl's sense (displace / toggle / carry): publish immediately and
    // tell the env-trace recorder, which snapshots the scene after every edit.
    public void NotifyEdit(SceneEditData edit)
    {
        PublishNow();
        Edited?.Invoke(LastSnapshot, edit);
    }

    public bool IsCarried(SceneGraphObject obj) => obj != null && obj.ObjectId == CarriedObjectId;

    public SceneGraphObject CarriedObject()
    {
        if (CarriedObjectId == null)
            return null;
        foreach (SceneGraphObject obj in SceneGraphObject.All)
            if (obj != null && obj.ObjectId == CarriedObjectId)
                return obj;
        return null;
    }

    public void SetCarried(SceneGraphObject obj, bool carried)
    {
        if (obj == null)
            return;
        Transform anchor = carryAnchor != null ? carryAnchor : endEffectorForFrameCheck;
        if (carried)
        {
            if (CarriedObjectId == obj.ObjectId)
                return;
            // Only one object can be in the hand.
            SceneGraphObject previous = CarriedObject();
            if (previous != null && previous != obj)
                previous.SetCarried(false, anchor);
            obj.SetCarried(true, anchor, carriedAlpha);
            if (!obj.IsCarried)
                return;
            CarriedObjectId = obj.ObjectId;
        }
        else
        {
            if (CarriedObjectId != obj.ObjectId)
                return;
            obj.SetCarried(false, anchor);
            CarriedObjectId = null;
        }
        NotifyEdit(new SceneEditData { objectId = obj.ObjectId, carried = carried });
    }

    private void LogFrameCheck()
    {
        if (endEffectorForFrameCheck == null || baseTransform == null)
            return;
        Vector3 tcp = SceneGraphObject.LocalToRos(baseTransform.InverseTransformPoint(endEffectorForFrameCheck.position));
        Debug.Log($"[SceneGraphPublisher] TCP at scene start in ROS base frame: ({tcp.x:F3}, {tcp.y:F3}, {tcp.z:F3}). " +
                  "Compare with home_ee in /ferl/status (expected ~ (0.307, 0.000, 0.487) at the Franka home pose).");
    }

    private void OnDrawGizmos()
    {
        if (!drawWorkspaceGizmo || baseTransform == null)
            return;
        Vector3 low = new Vector3(workspaceMinRos.x, workspaceMinRos.z, workspaceMinRos.y);
        Vector3 high = new Vector3(workspaceMaxRos.x, workspaceMaxRos.z, workspaceMaxRos.y);
        Gizmos.matrix = baseTransform.localToWorldMatrix;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireCube((low + high) * 0.5f, high - low);
        Gizmos.matrix = Matrix4x4.identity;
    }
}
