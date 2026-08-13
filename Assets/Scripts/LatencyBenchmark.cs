using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;

// Scripted latency workload: runs a fixed sequence of create / move / delete
// operations through the normal CollisionObjectPublisher pipeline, so latency,
// message size, and mesh density are measured under a controlled, repeatable
// load instead of ad-hoc hand interaction.
//
// Every frame during a run is tagged with the current phase (idle / planning /
// create / move / delete) and its frame time is streamed to /fps_phase in
// per-second batches, giving per-phase FPS distributions. The run opens with a
// do-nothing idle window as the baseline, then a block of MoveIt planning
// requests (same /plan_kinematic_path service the menu UI uses), then the
// object workload.
//
// Trigger from the ROS side with:
//   ros2 topic pub --once /benchmark/start std_msgs/msg/String "data: ''"
// Objects are named bench<run>_<config>_<trial> (e.g. bench1_mesh5000_003), so
// runs are isolated in analysis with: analyze_metrics --objects bench
public class LatencyBenchmark : MonoBehaviour
{
    [Header("Trigger")]
    public string startTopic = "/benchmark/start";
    public string statusTopic = "/benchmark/status";
    public bool runOnStart = false;

    [Header("Workload")]
    public int trialsPerConfig = 10;
    public int movesPerTrial = 10;
    public float interOpDelaySeconds = 0.5f;
    public bool includeCube = true;
    public int[] meshTriangleTargets = { 500, 5000, 20000 };

    [Header("Placement")]
    public Vector3 spawnCenter = new Vector3(0f, 1f, 0.75f);
    public float objectScale = 0.15f;
    public float moveRadius = 0.1f;
    public GameObject worldOrigin;
    public Material objectMaterial;

    [Header("FPS Phases")]
    public string fpsPhaseTopic = "/fps_phase";
    public float idleSeconds = 30f;

    [Header("Planning Phase")]
    public int planningTrials = 10;
    public string motionPlanServiceName = "/plan_kinematic_path";
    public string jointStateTopic = "/joint_states";
    public string planningGroupName = "ur_manipulator";
    public string planningPipelineId = "ompl";
    public float allowedPlanningTimeSeconds = 5f;
    public float planningJointOffsetRad = 0.4f;

    private ROSConnection ros;
    private bool running;
    private int runCounter;

    // Per-phase frame time recording
    private string currentPhase;
    private string currentConfig = "";
    private readonly List<float> frameTimesMs = new List<float>();

    private JointStateMsg latestJointState;
    private bool planResponseReceived;
    private int lastPlanErrorCode;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(statusTopic);
        ros.RegisterPublisher<StringMsg>(fpsPhaseTopic);
        ros.Subscribe<StringMsg>(startTopic, _ => StartRun());
        ros.Subscribe<JointStateMsg>(jointStateTopic, msg => latestJointState = msg);
        try
        {
            ros.RegisterRosService<GetMotionPlanRequest, GetMotionPlanResponse>(motionPlanServiceName);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Benchmark] Could not register {motionPlanServiceName}: {e.Message}");
        }
        if (runOnStart)
            StartRun();
    }

    void Update()
    {
        if (currentPhase == null)
            return;
        frameTimesMs.Add(Time.unscaledDeltaTime * 1000f);
        if (frameTimesMs.Count >= 120)
            FlushFrameTimes();
    }

    void SetPhase(string phase, string config)
    {
        FlushFrameTimes();
        currentPhase = phase;
        currentConfig = config;
    }

    void ClearPhase()
    {
        FlushFrameTimes();
        currentPhase = null;
        currentConfig = "";
    }

    void FlushFrameTimes()
    {
        if (frameTimesMs.Count == 0 || currentPhase == null)
        {
            frameTimesMs.Clear();
            return;
        }
        ros.Publish(fpsPhaseTopic, new StringMsg(
            $"{currentPhase},{currentConfig},{string.Join(";", frameTimesMs.Select(ms => ms.ToString("F3")))}"));
        frameTimesMs.Clear();
    }

    [ContextMenu("Start Benchmark")]
    public void StartRun()
    {
        if (running)
        {
            Debug.LogWarning("[Benchmark] Already running; ignoring start request");
            return;
        }
        StartCoroutine(RunBenchmark());
    }

    IEnumerator RunBenchmark()
    {
        running = true;
        runCounter++;
        PublishStatus($"started run={runCounter} trials={trialsPerConfig} moves={movesPerTrial} delay={interOpDelaySeconds}s");

        // Baseline: nothing happening at all
        PublishStatus($"idle phase for {idleSeconds}s");
        SetPhase("idle", "");
        yield return new WaitForSeconds(idleSeconds);
        ClearPhase();

        yield return RunPlanningTrials();

        if (includeCube)
            yield return RunConfig($"bench{runCounter}_cube", "cube", null);
        foreach (int triangleTarget in meshTriangleTargets)
            yield return RunConfig($"bench{runCounter}_mesh{triangleTarget}", $"mesh{triangleTarget}",
                                   GenerateSphereMesh(triangleTarget));

        ClearPhase();
        PublishStatus($"finished run={runCounter}");
        running = false;
    }

    IEnumerator RunPlanningTrials()
    {
        if (latestJointState == null || latestJointState.name.Length == 0)
        {
            PublishStatus($"planning phase skipped: nothing received on {jointStateTopic}");
            yield break;
        }

        PublishStatus($"planning phase: {planningTrials} requests, {allowedPlanningTimeSeconds}s allowed each");
        for (int trial = 0; trial < planningTrials; trial++)
        {
            planResponseReceived = false;
            GetMotionPlanRequest request = BuildPlanRequest(trial);

            SetPhase("planning", planningPipelineId);
            ros.SendServiceMessage<GetMotionPlanResponse>(motionPlanServiceName, request, OnPlanResponse);

            float deadline = Time.realtimeSinceStartup + allowedPlanningTimeSeconds + 10f;
            while (!planResponseReceived && Time.realtimeSinceStartup < deadline)
                yield return null;
            ClearPhase();

            PublishStatus(planResponseReceived
                ? $"planning trial {trial} done error_code={lastPlanErrorCode}"
                : $"planning trial {trial} timed out");
            yield return new WaitForSeconds(1f);
        }
    }

    GetMotionPlanRequest BuildPlanRequest(int trial)
    {
        var startState = new RobotStateMsg { joint_state = latestJointState };

        // Goal: current pose with the first joint rotated by +/- the configured
        // offset (alternating per trial), so consecutive plans differ
        float offset = planningJointOffsetRad * (trial % 2 == 0 ? 1f : -1f);
        var jointConstraints = new JointConstraintMsg[latestJointState.name.Length];
        for (int i = 0; i < latestJointState.name.Length; i++)
        {
            jointConstraints[i] = new JointConstraintMsg
            {
                joint_name = latestJointState.name[i],
                position = latestJointState.position[i] + (i == 0 ? offset : 0f),
                tolerance_above = 0.01,
                tolerance_below = 0.01,
                weight = 1.0
            };
        }

        var motionPlanRequest = new MotionPlanRequestMsg
        {
            workspace_parameters = new WorkspaceParametersMsg(),
            start_state = startState,
            goal_constraints = new[]
            {
                new ConstraintsMsg
                {
                    name = "benchmark_goal",
                    joint_constraints = jointConstraints,
                    position_constraints = new PositionConstraintMsg[0],
                    orientation_constraints = new OrientationConstraintMsg[0],
                    visibility_constraints = new VisibilityConstraintMsg[0]
                }
            },
            path_constraints = new ConstraintsMsg(),
            trajectory_constraints = new TrajectoryConstraintsMsg(),
            reference_trajectories = new GenericTrajectoryMsg[0],
            pipeline_id = planningPipelineId,
            planner_id = "",
            group_name = planningGroupName,
            num_planning_attempts = 1,
            allowed_planning_time = allowedPlanningTimeSeconds,
            max_velocity_scaling_factor = 1.0,
            max_acceleration_scaling_factor = 1.0,
            cartesian_speed_limited_link = "",
            max_cartesian_speed = 0.0
        };
        return new GetMotionPlanRequest(motionPlanRequest);
    }

    void OnPlanResponse(GetMotionPlanResponse response)
    {
        lastPlanErrorCode = response.motion_plan_response.error_code.val;
        planResponseReceived = true;
    }

    IEnumerator RunConfig(string idPrefix, string configLabel, Mesh mesh)
    {
        if (mesh != null)
            PublishStatus($"config {idPrefix} verts={mesh.vertexCount} tris={mesh.triangles.Length / 3}");
        else
            PublishStatus($"config {idPrefix}");

        for (int trial = 0; trial < trialsPerConfig; trial++)
        {
            SetPhase("create", configLabel);
            GameObject benchObject = CreateObject($"{idPrefix}_{trial:D3}", mesh);
            // CollisionObjectPublisher publishes the ADD on its first Update
            yield return new WaitForSeconds(interOpDelaySeconds);

            // Walk the object around a circle: deterministic, and every step is
            // well above the publisher's 1 mm change-detection epsilon
            SetPhase("move", configLabel);
            for (int move = 0; move < movesPerTrial; move++)
            {
                float angle = 2f * Mathf.PI * (move + 1) / movesPerTrial;
                benchObject.transform.position = spawnCenter +
                    new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * moveRadius;
                yield return new WaitForSeconds(interOpDelaySeconds);
            }

            SetPhase("delete", configLabel);
            Destroy(benchObject); // OnDestroy publishes the REMOVE
            yield return new WaitForSeconds(interOpDelaySeconds);
            ClearPhase();
        }
    }

    GameObject CreateObject(string objectId, Mesh mesh)
    {
        GameObject benchObject;
        if (mesh == null)
        {
            benchObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        }
        else
        {
            benchObject = new GameObject();
            benchObject.AddComponent<MeshFilter>().mesh = mesh;
            benchObject.AddComponent<MeshRenderer>();
        }
        benchObject.name = objectId;
        if (objectMaterial != null)
            benchObject.GetComponent<MeshRenderer>().material = objectMaterial;
        benchObject.transform.position = spawnCenter;
        benchObject.transform.localScale = Vector3.one * objectScale;

        CollisionObjectPublisher publisher = benchObject.AddComponent<CollisionObjectPublisher>();
        publisher.objectId = objectId;
        publisher.isMesh = mesh != null;
        publisher.autoDetectMeshType = false;
        publisher.worldOrigin = worldOrigin;
        // High enough that every scripted move publishes promptly
        publisher.publishRateHz = Mathf.Max(20f, 2f / Mathf.Max(interOpDelaySeconds, 0.05f));
        return benchObject;
    }

    // UV sphere with lat/lon segment counts chosen to hit ~triangleTarget triangles
    static Mesh GenerateSphereMesh(int triangleTarget)
    {
        int segments = Mathf.Max(3, Mathf.CeilToInt(Mathf.Sqrt(triangleTarget / 2f)));
        int lat = segments, lon = segments;

        var vertices = new Vector3[(lat + 1) * (lon + 1)];
        for (int i = 0; i <= lat; i++)
        {
            float theta = Mathf.PI * i / lat;
            for (int j = 0; j <= lon; j++)
            {
                float phi = 2f * Mathf.PI * j / lon;
                vertices[i * (lon + 1) + j] = 0.5f * new Vector3(
                    Mathf.Sin(theta) * Mathf.Cos(phi),
                    Mathf.Cos(theta),
                    Mathf.Sin(theta) * Mathf.Sin(phi));
            }
        }

        var triangles = new int[lat * lon * 6];
        int t = 0;
        for (int i = 0; i < lat; i++)
        {
            for (int j = 0; j < lon; j++)
            {
                int a = i * (lon + 1) + j;
                int b = a + lon + 1;
                triangles[t++] = a; triangles[t++] = a + 1; triangles[t++] = b;
                triangles[t++] = a + 1; triangles[t++] = b + 1; triangles[t++] = b;
            }
        }

        var mesh = new Mesh { name = $"BenchSphere{triangleTarget}" };
        if (vertices.Length > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void PublishStatus(string status)
    {
        Debug.Log($"[Benchmark] {status}");
        ros.Publish(statusTopic, new StringMsg(status));
    }
}
