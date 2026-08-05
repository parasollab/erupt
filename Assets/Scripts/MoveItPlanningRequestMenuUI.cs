using UnityEngine;
using UnityEngine.UIElements;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Trajectory;
using RosMessageTypes.Sensor;
using System.Collections.Generic;
using System.Linq;
using System;

public class MoveItPlanningRequestMenuUI : MonoBehaviour
{
    // Synthetic object_id tying together set_start_state/set_goal_state/send_planning_request/
    // planning_request_result events in the ObjectEvent log -- there's no spawned GameObject
    // behind these, just logical actions on this menu, same convention as CertifyPathMenuController's
    // "path_certification".
    private const string PlanningRequestObjectId = "planning_request";

    [Header("Robot")]
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private string jointStateTopic = "/joint_states";
    [SerializeField] private string executeTrajectoryTopic = "/joint_trajectory_controller/joint_trajectory";
    // Successful plans are republished here for the ROS-side planned_path_logger; a service
    // response is only visible to this client, so nothing else could record the path.
    [SerializeField] private string plannedPathTopic = "/study/planned_paths";

    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;
    
    [Header("MoveIt2 Configuration")]
    [SerializeField] private string planningGroupName = "ur_manipulator";
    [SerializeField] private string planningPipelineId = "ompl";
    [SerializeField] private string defaultPlannerId = "ur_manipulator";
    [SerializeField] private int defaultNumPlanningAttempts = 10;
    [SerializeField] private float defaultAllowedPlanningTime = 5.0f;
    [SerializeField] private double goalTolerance = 0.01;

    // Per-scene study restrictions, overridden on the prefab instance by
    // PlanningMenuRestrictionsSetup (Study/Planning Menu menu): Task2/3 lock the preset
    // start/goal states, Task3 additionally has no planning in its task.
    [Header("Study Restrictions")]
    [Tooltip("Allow the Set Start/Goal State buttons. Off in Task2/3 scenes so participants can't change the preset states.")]
    [SerializeField] private bool allowStartGoalEditing = true;
    [Tooltip("Allow the Plan button. Off in Task3 scenes, where planning is not part of the task.")]
    [SerializeField] private bool allowPlanning = true;
    [Tooltip("Allow the Execute Trajectory button. Off in all study task scenes -- participants only plan, never drive the real robot.")]
    [SerializeField] private bool allowExecution = true;
    [Tooltip("Allow the Mirror Joint States button. Off in all study task scenes. Executing a trajectory still auto-starts mirroring regardless of this flag.")]
    [SerializeField] private bool allowMirroring = true;

    [Header("ROS 2 Topics")]
    [SerializeField] private string motionPlanServiceName = "/plan_kinematic_path";
    [SerializeField] private string displayTrajectoryTopic = "";

    [Header("Planner Query Service")]
    [SerializeField] private bool autoQueryPlanners = true;
    [SerializeField] private string plannerQueryServiceName = "/query_planner_interface";

    [Header("Ghost Robots")]
    [SerializeField] private SpawnGhosts ghostSpawner;
    [SerializeField] private TrajectoryReplay trajectoryReplayer;

    [Header("Joint Name Remapping")]
    [Tooltip("Prefix in the incoming ROS joint_states topic (e.g. 'panda_')")]
    [SerializeField] private string rosJointNamePrefix = "panda_";
    [Tooltip("Prefix used by the Unity robot prefab joints (e.g. 'fr3_')")]
    [SerializeField] private string unityJointNamePrefix = "fr3_";

    // Robot
    private DirectArticulationIKController robotController;
    
    // UI Elements
    private VisualElement root;
    private Button setStartStateButton;
    private Button setGoalStateButton;
    private DropdownField plannerPipelineDropdown;
    private DropdownField plannerDropdown;
    private IntegerField numPlanningAttemptsField;
    private FloatField allowedPlanningTimeField;
    private Label planningResultLabel;
    private Button planningRequestButton;
    private Button stopReplayButton;
    private Button executeTrajectoryButton;
    private Button mirrorButton;
    private bool isMirroring = false;
    private bool isReplaying = false;

    // ROS Connection
    private ROSConnection ros;
    private bool isConnected = false;

    // Planning state
    private bool startSet = false;
    private bool goalSet = false;
    private RobotStateMsg currentStartState;
    private RobotStateMsg currentGoalState;
    private bool hasStartState = false;
    private bool hasGoalState = false;
    private JointTrajectoryMsg lastPlannedTrajectory;
    public JointTrajectoryMsg LastPlannedTrajectory => lastPlannedTrajectory != null ? BuildLocalTrajectory(lastPlannedTrajectory) : null;
    // Read by StudyController's advance gate: true once any plan has succeeded in this scene.
    // Unlike lastPlannedTrajectory it is never cleared by a later failure -- the component is
    // scene-local, so a scene load is the per-scene reset.
    private bool hasPlannedSuccessfully;
    public bool HasPlannedSuccessfully => hasPlannedSuccessfully;

    // Planner querying
    private bool isQueryingPlanners = false;
    private Dictionary<string, string[]> pipelineToPlanners = new Dictionary<string, string[]>();
    
    // Parsed results for planner dropdowns
    [Serializable]
    public class PlannerListing
    {
        public string pipelineId;
        public string[] plannerIds;
    }
    
    public List<PlannerListing> PlannerResults = new List<PlannerListing>();
    
    // Hardcoded joint names for the UR5e - ideally this would be dynamic
    public readonly string[] jointNames = new string[]
    { "shoulder_pan_joint", "shoulder_lift_joint", "elbow_joint", "wrist_1_joint", "wrist_2_joint", "wrist_3_joint" };

    private Dictionary<string, int> jointNameToIndex;

    public readonly Tuple<float, float>[] jointLimits = new Tuple<float, float>[]
    {
        new Tuple<float, float>(-351f, 351f),  // shoulder_pan_joint
        new Tuple<float, float>(-351f, 351f),  // shoulder_lift_joint
        new Tuple<float, float>(-171f, 171f),  // elbow_joint
        new Tuple<float, float>(-351f, 351f),  // wrist_1_joint
        new Tuple<float, float>(-351f, 351f),  // wrist_2_joint
        new Tuple<float, float>(-351f, 351f)   // wrist_3_joint
    };

    private void Awake()
    {
        if (ikController == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: ikController not assigned — drag the Robot IK Manager's DirectArticulationIKController here.");
            return;
        }

        jointNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < jointNames.Length; i++)
        {
            jointNameToIndex[jointNames[i]] = i;
        }
    }

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: No UIDocument/rootVisualElement found.");
            return;
        }

        EnsureRobotController();
        EnsureGhostReferences();

        InitializeUIElements();
        SetupEventHandlers();
        InitializeROSConnection();

        // Start planner querying immediately
        StartPlannerQuerying();
    }

    private bool EnsureRobotController()
    {
        if (robotController != null)
        {
            return true;
        }

        robotController = ikController;
        if (robotController == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: DirectArticulationIKController is not assigned.");
            return false;
        }

        return true;
    }

    private void EnsureGhostReferences()
    {
        if (ghostSpawner == null)
        {
            ghostSpawner = GetComponent<SpawnGhosts>() ??
                FindFirstObjectByType<SpawnGhosts>(FindObjectsInactive.Include);
            if (ghostSpawner != null)
            {
                Debug.LogWarning($"MoveItPlanningRequestMenuUI: ghostSpawner was not assigned; using '{ghostSpawner.name}'.");
            }
        }

        if (trajectoryReplayer == null)
        {
            trajectoryReplayer = GetComponent<TrajectoryReplay>() ??
                FindFirstObjectByType<TrajectoryReplay>(FindObjectsInactive.Include);
            if (trajectoryReplayer != null)
            {
                Debug.LogWarning($"MoveItPlanningRequestMenuUI: trajectoryReplayer was not assigned; using '{trajectoryReplayer.name}'.");
            }
        }
    }

    private void StartPlannerQuerying()
    { 
        // Register the service and start querying
        if (ros != null)
        {
            ros.RegisterRosService<GetMotionPlanRequest, GetMotionPlanResponse>(motionPlanServiceName);

            if (!autoQueryPlanners) return;
            ros.RegisterRosService<QueryPlannerInterfacesRequest, QueryPlannerInterfacesResponse>(plannerQueryServiceName);

            InvokeRepeating(nameof(TryQueryPlanners), 0.5f, 1.0f); // retry until it succeeds
        }
    }

    private void InitializeUIElements()
    {
        // Get UI elements by name
        planningResultLabel = root.Q<Label>("planningResultLabel");
        setStartStateButton = root.Q<Button>("planningRequestSetStartButton");
        setGoalStateButton = root.Q<Button>("planningRequestSetGoalStateButton");
        plannerPipelineDropdown = root.Q<DropdownField>("planningRequestPlannerPipelineIDDropDown");
        plannerDropdown = root.Q<DropdownField>("planningRequestPlannerIDDropDown");
        numPlanningAttemptsField = root.Q<IntegerField>("planningRequestNumPlanningAttemptsInput");
        allowedPlanningTimeField = root.Q<FloatField>("planningRequestAllowedPlanningTimeInput");
        planningRequestButton = root.Q<Button>("planningRequestSendButton");
        stopReplayButton = root.Q<Button>("planningRequestStopReplayButton");
        executeTrajectoryButton = root.Q<Button>("planningRequestExecuteTrajectoryButton");
        mirrorButton = root.Q<Button>("mirrorJointStateButton");

        // Validate UI elements
        if (setStartStateButton == null || setGoalStateButton == null ||
            plannerPipelineDropdown == null || plannerDropdown == null ||
            numPlanningAttemptsField == null || allowedPlanningTimeField == null ||
            planningRequestButton == null || stopReplayButton == null ||
            mirrorButton == null || executeTrajectoryButton == null ||
            planningResultLabel == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: One or more UI elements not found in UXML.");
            return;
        }
        
        // Initialize dropdowns with empty lists - will be populated by ROS
        plannerPipelineDropdown.choices = new List<string>();
        plannerPipelineDropdown.value = "";
        plannerDropdown.choices = new List<string>();
        plannerDropdown.value = "";
        
        // Set default values
        numPlanningAttemptsField.value = defaultNumPlanningAttempts;
        allowedPlanningTimeField.value = defaultAllowedPlanningTime;

        stopReplayButton.SetEnabled(false);
        executeTrajectoryButton.SetEnabled(false);
        planningRequestButton.SetEnabled(allowPlanning);
        mirrorButton.SetEnabled(allowMirroring);

        // Update button states
        UpdateButtonStates();
    }

    private void SetupEventHandlers()
    {
        setStartStateButton.clicked += OnSetStartStateClicked;
        setGoalStateButton.clicked += OnSetGoalStateClicked;

        // Setup dropdown event handlers
        plannerPipelineDropdown.RegisterValueChangedCallback(OnPipelineSelectionChanged);

        // Add planning request button
        planningRequestButton.clicked += SendPlanningRequest;

        stopReplayButton.clicked += () =>
        {
            if (isReplaying)
            {
                StopPreview();
            }
            else if (!isReplaying)
            {
                PreviewTrajectory(lastPlannedTrajectory);

            }
        };

        mirrorButton.clicked += OnMirrorButtonClicked;
        executeTrajectoryButton.clicked += ExectuteTrajectory;
    }

    private void OnPipelineSelectionChanged(ChangeEvent<string> evt)
    {
        string selectedPipeline = evt.newValue;
        planningPipelineId = selectedPipeline;

        if (pipelineToPlanners.TryGetValue(selectedPipeline, out var planners))
        {
            plannerDropdown.choices = planners.ToList();
            var pick = planners.Contains(defaultPlannerId) ? defaultPlannerId :
                       planners.Length > 0 ? planners[0] : "";
            plannerDropdown.SetValueWithoutNotify(pick);
        }
        else
        {
            plannerDropdown.choices = new List<string>();
            plannerDropdown.SetValueWithoutNotify("");
        }
    }

    private void TryQueryPlanners()
    {
        if (isQueryingPlanners) return;
        if (ros == null || !ros.HasConnectionThread)
        {
            Debug.LogWarning("[MoveIt] Waiting for ROS-TCP connection...");
            return;
        }

        isQueryingPlanners = true;
        var req = new QueryPlannerInterfacesRequest();
        try
        {
            ros.SendServiceMessage<QueryPlannerInterfacesResponse>(
                plannerQueryServiceName, req,
                OnPlannerQueryResponse
            );
        }
        catch (Exception e)
        {
            isQueryingPlanners = false;
            Debug.LogError($"[MoveIt] Service call failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // Call this whenever you update UI from a callback/thread.
    void UI(Action a)
    {
        // Ensure we run on the UI panel's schedule (main thread, next frame)
        if (root != null)
            root.schedule.Execute(() => a()).ExecuteLater(0);
        else
            a();
    }

    private void OnPlannerQueryResponse(QueryPlannerInterfacesResponse resp)
    {
        isQueryingPlanners = false;
        CancelInvoke(nameof(TryQueryPlanners));

        if (resp == null || resp.planner_interfaces == null)
        {
            Debug.LogWarning("[MoveIt] Empty response from /query_planner_interface");
            return;
        }

        PlannerResults.Clear();
        pipelineToPlanners.Clear();

        foreach (var desc in resp.planner_interfaces)
        {
            var pipeline = desc.pipeline_id ?? string.Empty;
            var planners = desc.planner_ids ?? Array.Empty<string>();

            PlannerResults.Add(new PlannerListing
            {
                pipelineId = pipeline,
                plannerIds = planners
            });

            pipelineToPlanners[pipeline] = planners;
        }

        UI(() =>
        {
            // 1) Update pipeline dropdown
            var discoveredPipelines = pipelineToPlanners.Keys.ToList();

            plannerPipelineDropdown.choices = discoveredPipelines;

            // Pick a valid pipeline
            string chosenPipeline = planningPipelineId;
            if (!discoveredPipelines.Contains(chosenPipeline))
                chosenPipeline = discoveredPipelines.Count > 0 ? discoveredPipelines[0] : "";

            // IMPORTANT: Set without notify, then manually update planners
            plannerPipelineDropdown.SetValueWithoutNotify(chosenPipeline);
            planningPipelineId = chosenPipeline;

            // 2) Update planner dropdown for the selected pipeline
            if (!string.IsNullOrEmpty(chosenPipeline) && pipelineToPlanners.TryGetValue(chosenPipeline, out var planners))
            {
                plannerDropdown.choices = planners.ToList();
                // If your default isn't in the list, pick the first one
                var chosenPlanner = planners.Contains(defaultPlannerId) ? defaultPlannerId :
                                    planners.Length > 0 ? planners[0] : "";
                plannerDropdown.SetValueWithoutNotify(chosenPlanner);
            }
            else
            {
                plannerDropdown.choices = new List<string>();
                plannerDropdown.SetValueWithoutNotify("");
            }
        });
    }

    private void InitializeROSConnection()
    {
        isConnected = false;

        ros = ROSConnection.GetOrCreateInstance();

        if (ros == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: Failed to create ROS connection.");
            return;
        }

        isConnected = true;

        ros.RegisterPublisher<JointTrajectoryMsg>(executeTrajectoryTopic);
        ros.RegisterPublisher<JointTrajectoryMsg>(plannedPathTopic);

        ros.Subscribe<JointStateMsg>(jointStateTopic, MirrorJointStates);

        if (!string.IsNullOrEmpty(displayTrajectoryTopic))
        {
            ros.Subscribe<DisplayTrajectoryMsg>(displayTrajectoryTopic, DisplayTrajectory);
        }
    }

    private void DisplayTrajectory(DisplayTrajectoryMsg trajectory)
    {
        if (isReplaying && trajectoryReplayer.HasFinishedOneLoop())
            StopPreview();
        else if (isReplaying)
            return;
        
        lastPlannedTrajectory = trajectory.trajectory.Length > 0 ? trajectory.trajectory[0].joint_trajectory : null;
        if (lastPlannedTrajectory != null)
        {
            PreviewTrajectory(lastPlannedTrajectory);
        }
    }

    private void MirrorJointStates(JointStateMsg jointState)
    {
        if (!isMirroring) return;

        if (!EnsureRobotController())
        {
            return;
        }

        var sb = new System.Text.StringBuilder("MoveItPlanningRequestMenuUI: Mirror joints —");
        for (int i = 0; i < jointState.name.Length; i++)
            sb.Append($"\n  {jointState.name[i]}: {(i < jointState.position.Length ? jointState.position[i] : double.NaN):F4} rad");
        Debug.Log(sb.ToString());

        robotController.ApplyJointState(RemapJointNames(jointState.name), jointState.position);

        string[] unityNames = robotController.GetJointStateNames();
        float[] unityPositions = robotController.GetJointStatePositions();
        var sb2 = new System.Text.StringBuilder("MoveItPlanningRequestMenuUI: Unity joint state after apply —");
        for (int i = 0; i < unityNames.Length; i++)
            sb2.Append($"\n  {unityNames[i]}: {(i < unityPositions.Length ? unityPositions[i] : float.NaN):F4} rad");
        Debug.Log(sb2.ToString());
    }

    private string[] RemapJointNames(string[] rosNames)
    {
        if (string.IsNullOrEmpty(rosJointNamePrefix) || rosJointNamePrefix == unityJointNamePrefix)
            return rosNames;

        string[] remapped = new string[rosNames.Length];
        for (int i = 0; i < rosNames.Length; i++)
        {
            remapped[i] = rosNames[i].StartsWith(rosJointNamePrefix)
                ? unityJointNamePrefix + rosNames[i].Substring(rosJointNamePrefix.Length)
                : rosNames[i];
        }
        return remapped;
    }

    private string[] RemapJointNamesToRos(string[] unityNames)
    {
        if (string.IsNullOrEmpty(unityJointNamePrefix) || rosJointNamePrefix == unityJointNamePrefix)
            return unityNames;

        string[] remapped = new string[unityNames.Length];
        for (int i = 0; i < unityNames.Length; i++)
        {
            remapped[i] = unityNames[i].StartsWith(unityJointNamePrefix)
                ? rosJointNamePrefix + unityNames[i].Substring(unityJointNamePrefix.Length)
                : unityNames[i];
        }
        return remapped;
    }

    // Button-click wrapper: the restriction only guards the participant-facing button;
    // ExectuteTrajectory still calls ToggleMirroring directly so execution can mirror.
    private void OnMirrorButtonClicked()
    {
        if (!allowMirroring)
            return;
        ToggleMirroring();
    }

    private void ToggleMirroring()
    {
        isMirroring = !isMirroring;
        mirrorButton.text = isMirroring ? "Stop Mirroring" : "Mirror Joint States";
        Debug.Log($"MoveItPlanningRequestMenuUI: Mirroring {(isMirroring ? "enabled" : "disabled")}.");
        if (isMirroring && robotController != null)
            robotController.LogJointDriveLimits();
    }

    private void OnSetStartStateClicked()
    {
        if (!allowStartGoalEditing) return;
        ObjectMetricsLogger.Instance?.LogEvent("set_start_state", PlanningRequestObjectId);
        EnsureGhostReferences();

        if (ghostSpawner == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ghostSpawner not assigned; start ghost not spawned.");
        }
        else if (!startSet)
        {
            ghostSpawner.SpawnStartGhost();
            startSet = true;
        }
        else
        {
            ghostSpawner.UpdateStartGhost();
        }

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }

        // Get current robot state (this would typically come from the robot or simulation)
        currentStartState = GetCurrentRobotState();
        hasStartState = true;

        UpdateButtonStates();
    }

    private void OnSetGoalStateClicked()
    {
        if (!allowStartGoalEditing) return;
        ObjectMetricsLogger.Instance?.LogEvent("set_goal_state", PlanningRequestObjectId);
        EnsureGhostReferences();

        if (ghostSpawner == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ghostSpawner not assigned; goal ghost not spawned.");
        }
        else if (!goalSet)
        {
            ghostSpawner.SpawnGoalGhost();
            goalSet = true;
        }
        else
        {
            ghostSpawner.UpdateGoalGhost();
        }

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }
        
        // Get goal state (this could be from user interaction, predefined poses, etc.)
        currentGoalState = GetGoalRobotState();
        hasGoalState = true;
        
        UpdateButtonStates();
    }

    public void SendPlanningRequest()
    {
        if (!allowPlanning) return;
        ObjectMetricsLogger.Instance?.LogEvent("send_planning_request", PlanningRequestObjectId);

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS 2 connection not available.");
            return;
        }
        
        if (!hasStartState || !hasGoalState)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: Both start and goal states must be set before planning.");
            return;
        }
        
        // Get the selected planner from the dropdown
        string selectedPlanner = plannerDropdown.value;
        if (autoQueryPlanners && string.IsNullOrEmpty(selectedPlanner))
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: No planner selected.");
            return;
        }

        // Disable replay button until we have a new trajectory
        StopPreview();
        if (trajectoryReplayer != null)
            trajectoryReplayer.StopReplay();
        stopReplayButton.SetEnabled(false);
        executeTrajectoryButton.SetEnabled(false);
        
        var planningRequest = CreateMotionPlanRequest();
        GetMotionPlanRequest motionPlanRequest = new GetMotionPlanRequest(planningRequest);
        ros.SendServiceMessage<GetMotionPlanResponse>(
            motionPlanServiceName, motionPlanRequest,
            OnMotionPlanResponse
        );

        Debug.Log($"MoveItPlanningRequestMenuUI: ROS 2 planning request sent with planner: {selectedPlanner}");
    }

    private MotionPlanRequestMsg CreateMotionPlanRequest()
    {
        var request = new MotionPlanRequestMsg
        {
            workspace_parameters = new WorkspaceParametersMsg(),
            start_state = currentStartState,
            goal_constraints = CreateGoalConstraints(),
            path_constraints = new ConstraintsMsg(),
            trajectory_constraints = new TrajectoryConstraintsMsg(),
            reference_trajectories = new GenericTrajectoryMsg[0],
            pipeline_id = planningPipelineId,
            planner_id = plannerDropdown.value, // Use the selected planner from dropdown
            group_name = planningGroupName,
            num_planning_attempts = numPlanningAttemptsField.value,
            allowed_planning_time = allowedPlanningTimeField.value,
            max_velocity_scaling_factor = 1.0,
            max_acceleration_scaling_factor = 1.0,
            cartesian_speed_limited_link = "",
            max_cartesian_speed = 0.0
        };
        
        return request;
    }

    private ConstraintsMsg[] CreateGoalConstraints()
    {
        // Create goal constraints based on the goal state
        // This is a simplified version - you might want to create more specific constraints
        var constraints = new ConstraintsMsg
        {
            name = "goal_constraints",
            joint_constraints = CreateJointConstraints(currentGoalState),
            position_constraints = new PositionConstraintMsg[0],
            orientation_constraints = new OrientationConstraintMsg[0],
            visibility_constraints = new VisibilityConstraintMsg[0]
        };
        
        return new ConstraintsMsg[] { constraints };
    }

    private JointConstraintMsg[] CreateJointConstraints(RobotStateMsg robotState)
    {
        if (robotState?.joint_state?.name == null || robotState.joint_state.position == null)
            return new JointConstraintMsg[0];
        
        var constraints = new List<JointConstraintMsg>();
        
        for (int i = 0; i < robotState.joint_state.name.Length; i++)
        {
            if (i < robotState.joint_state.position.Length)
            {
                var constraint = new JointConstraintMsg
                {
                    joint_name = robotState.joint_state.name[i],
                    position = robotState.joint_state.position[i],
                    tolerance_above = goalTolerance,
                    tolerance_below = goalTolerance,
                    weight = 1.0
                };
                constraints.Add(constraint);
            }
        }
        
        return constraints.ToArray();
    }

    private RobotStateMsg GetRobotStateMsgFromController()
    {
        if (!EnsureRobotController()) return new RobotStateMsg();

        string[] names = RemapJointNamesToRos(robotController.GetJointStateNames());
        float[] positionsF = robotController.GetJointStatePositions();
        double[] positions = Array.ConvertAll(positionsF, p => (double)p);
        double[] zeros = new double[names.Length];

        var sb = new System.Text.StringBuilder("[MoveIt] Robot state being sent:\n");
        for (int i = 0; i < names.Length; i++)
            sb.AppendLine($"  {names[i]}: {positions[i]:F4} rad");
        Debug.Log(sb.ToString());

        return new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    frame_id = "base_link",
                    stamp = new TimeMsg
                    {
#if ROS2
                        sec = (int)Time.time,
#else
                        sec = (uint)Time.time,
#endif
                        nanosec = (uint)((Time.time - (int)Time.time) * 1e9)
                    }
                },
                name = names,
                position = positions,
                velocity = zeros,
                effort = zeros
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
    }

    private RobotStateMsg GetCurrentRobotState() => GetRobotStateMsgFromController();

    private RobotStateMsg GetGoalRobotState() => GetRobotStateMsgFromController();

    // Populates start/goal state and ghosts from a pre-baked trajectory's first/last waypoint,
    // instead of the live robot pose — used when a scene auto-plays a trajectory at Start().
    public void SetStartAndGoalFromTrajectory(TrajectoryData trajectoryData)
    {
        if (trajectoryData == null || trajectoryData.waypoints == null || trajectoryData.waypoints.Length == 0)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: TrajectoryData has no waypoints.");
            return;
        }

        if (!EnsureRobotController())
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: robotController not available for trajectory ghosts.");
            return;
        }

        EnsureGhostReferences();

        if (ghostSpawner == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ghostSpawner not assigned.");
            return;
        }

        string[] names = trajectoryData.jointNames;
        TrajectoryData.Waypoint first = trajectoryData.waypoints[0];
        TrajectoryData.Waypoint last = trajectoryData.waypoints[trajectoryData.waypoints.Length - 1];

        if (!startSet)
        {
            ghostSpawner.SpawnStartGhostFromPose(robotController, names, first.positions);
            startSet = true;
        }
        else
        {
            ghostSpawner.UpdateStartGhostFromPose(robotController, names, first.positions);
        }
        currentStartState = BuildRobotStateMsgFromNamedPositions(names, first.positions);
        hasStartState = true;

        if (!goalSet)
        {
            ghostSpawner.SpawnGoalGhostFromPose(robotController, names, last.positions);
            goalSet = true;
        }
        else
        {
            ghostSpawner.UpdateGoalGhostFromPose(robotController, names, last.positions);
        }
        currentGoalState = BuildRobotStateMsgFromNamedPositions(names, last.positions);
        hasGoalState = true;

        UpdateButtonStates();
    }

    private RobotStateMsg BuildRobotStateMsgFromNamedPositions(string[] unityNames, double[] positions)
    {
        string[] rosNames = RemapJointNamesToRos(unityNames);
        double[] zeros = new double[rosNames.Length];

        return new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    frame_id = "base_link",
                    stamp = new TimeMsg
                    {
#if ROS2
                        sec = (int)Time.time,
#else
                        sec = (uint)Time.time,
#endif
                        nanosec = (uint)((Time.time - (int)Time.time) * 1e9)
                    }
                },
                name = rosNames,
                position = positions,
                velocity = zeros,
                effort = zeros
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
    }

    private void OnMotionPlanResponse(GetMotionPlanResponse response)
    {
        var motionPlanResponse = response.motion_plan_response;
        if (motionPlanResponse.error_code.val == 1) // SUCCESS
        {
            Debug.Log($"MoveItPlanningRequestMenuUI: Planning successful! Planning time: {motionPlanResponse.planning_time}s");
            ObjectMetricsLogger.Instance?.LogEvent("planning_request_result", PlanningRequestObjectId, details: "success");
            hasPlannedSuccessfully = true;

            // Handle the planned trajectory
            if (motionPlanResponse.trajectory?.joint_trajectory != null)
            {
                lastPlannedTrajectory = motionPlanResponse.trajectory.joint_trajectory;
                Debug.Log($"MoveItPlanningRequestMenuUI: Trajectory has {lastPlannedTrajectory.points.Length} waypoints");

                // Raw trajectory (original ROS joint names), not the remapped local copy.
                if (lastPlannedTrajectory.points.Length > 0)
                {
                    ros.Publish(plannedPathTopic, lastPlannedTrajectory);
                }

                planningResultLabel.text = $"Planning successful! Time: {motionPlanResponse.planning_time}s, Waypoints: {lastPlannedTrajectory.points.Length}";

                // You can execute the trajectory here or store it for later execution
                PreviewTrajectory(lastPlannedTrajectory);
                executeTrajectoryButton.SetEnabled(allowExecution);
            }
        }
        else
        {
            planningResultLabel.text = $"Planning failed with error: {motionPlanResponse.error_code.val} {motionPlanResponse.error_code.message}";
            lastPlannedTrajectory = null;
            stopReplayButton.SetEnabled(false);
            executeTrajectoryButton.SetEnabled(false);

            Debug.LogError($"MoveItPlanningRequestMenuUI: Planning failed with error code: {motionPlanResponse.error_code.val} - {motionPlanResponse.error_code.message}");
            ObjectMetricsLogger.Instance?.LogEvent("planning_request_result", PlanningRequestObjectId,
                details: $"failure:{motionPlanResponse.error_code.val}:{motionPlanResponse.error_code.message}");
        }
    }

    private void PreviewTrajectory(JointTrajectoryMsg trajectory)
    {
        EnsureGhostReferences();
        if (trajectoryReplayer != null)
        {
            stopReplayButton.SetEnabled(true);
            isReplaying = true;
            stopReplayButton.text = "Stop Replay";
            // Remap joint names from ROS convention (panda_) to Unity convention (fr3_)
            // so ApplyJointState can find joints in the dictionary.
            trajectoryReplayer.StartReplay(BuildLocalTrajectory(trajectory));
        }
        else
        {
            Debug.Log("MoveItPlanningRequestMenuUI: No TrajectoryReplay component assigned.");
        }
    }

    private JointTrajectoryMsg BuildLocalTrajectory(JointTrajectoryMsg traj)
    {
        return new JointTrajectoryMsg
        {
            header = traj.header,
            joint_names = RemapJointNames(traj.joint_names),
            points = traj.points
        };
    }
    
    private void StopPreview()
    {
        if (trajectoryReplayer != null && isReplaying)
        {
            trajectoryReplayer.StopReplay();
            isReplaying = false;
            stopReplayButton.text = "Start Replay";
        }
    }

    private void ExectuteTrajectory()
    {
        if (!allowExecution)
            return;

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS 2 connection not available.");
            return;
        }

        if (lastPlannedTrajectory == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: No planned trajectory to execute.");
            return;
        }

        // Stop any ongoing preview and start mirroring if not already
        StopPreview();
        if (!isMirroring)
            ToggleMirroring();

        ros.Publish(executeTrajectoryTopic, lastPlannedTrajectory);
        Debug.Log("MoveItPlanningRequestMenuUI: Published trajectory for execution.");
    }

    private void UpdateButtonStates()
    {
        // Update button visual states based on current planning state
        setStartStateButton.text = hasStartState ? "Start State ✓" : "Set Start State";
        setGoalStateButton.text = hasGoalState ? "Goal State ✓" : "Set Goal State";

        // You could also change button colors or enable/disable them
        setStartStateButton.SetEnabled(allowStartGoalEditing);
        setGoalStateButton.SetEnabled(allowStartGoalEditing);
    }

    public void ResetPlanningState()
    {
        hasStartState = false;
        hasGoalState = false;
        currentStartState = null;
        currentGoalState = null;
        UpdateButtonStates();
    }

    public void SetPlanningGroup(string groupName)
    {
        planningGroupName = groupName;
    }

    public void SetPlanningPipeline(string pipelineId)
    {
        planningPipelineId = pipelineId;
    }

    private void OnDisable()
    {
    }

    private void OnDestroy()
    {    
        // Cancel any pending invokes
        CancelInvoke(nameof(TryQueryPlanners));
    }
}
