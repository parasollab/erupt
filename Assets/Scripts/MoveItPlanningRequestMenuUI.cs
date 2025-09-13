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
    [Header("Robot")]
    [SerializeField] private GameObject robot;
    [SerializeField] private string jointStateTopic = "/joint_states";
    [SerializeField] private string executeTrajectoryTopic = "/joint_trajectory_controller/joint_trajectory";

    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;
    
    [Header("MoveIt2 Configuration")]
    [SerializeField] private string planningGroupName = "ur_manipulator";
    [SerializeField] private string planningPipelineId = "ompl";
    [SerializeField] private string defaultPlannerId = "ur_manipulator";
    [SerializeField] private int defaultNumPlanningAttempts = 10;
    [SerializeField] private float defaultAllowedPlanningTime = 5.0f;
    [SerializeField] private double goalTolerance = 0.01;
    
    [Header("ROS 2 Topics")]
    [SerializeField] private string motionPlanServiceName = "/plan_kinematic_path";

    [Header("Planner Query Service")]
    [SerializeField] private bool autoQueryPlanners = true;
    [SerializeField] private string plannerQueryServiceName = "/query_planner_interface";

    [Header("Ghost Robots")]
    [SerializeField] private SpawnGhosts ghostSpawner;
    [SerializeField] private TrajectoryReplay trajectoryReplayer;

    // Robot
    private RobotManager robotManager;
    
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
        if (robot == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: Robot GameObject not assigned.");
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

        // Get the robot manager
        robotManager = robot.GetComponent<RobotManager>();

        InitializeUIElements();
        SetupEventHandlers();
        InitializeROSConnection();

        // Start planner querying immediately
        StartPlannerQuerying();
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

        mirrorButton.clicked += ToggleMirroring;
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

        ros.Subscribe<JointStateMsg>(jointStateTopic, MirrorJointStates);
    }

    private void MirrorJointStates(JointStateMsg jointState)
    {
        if (!isMirroring) return;

        if (robotManager == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: RobotManager not assigned.");
            return;
        }

        // Read joint states and map to robot joints
        float[] jointAngles = new float[robotManager.GetJointNames().Count];
        for (int i = 0; i < jointState.name.Length; i++)
        {
            string jointName = jointState.name[i];
            int jointIndex = jointNameToIndex[jointName];
            jointAngles[jointIndex] = -((float)jointState.position[i] * Mathf.Rad2Deg);
        }

        // Apply mirrored joint angles back to the robot
        robotManager.SetJointAngles(jointAngles);
    }

    private void ToggleMirroring()
    {
        isMirroring = !isMirroring;
        mirrorButton.text = isMirroring ? "Stop Mirroring" : "Mirror Joint States";
        Debug.Log($"MoveItPlanningRequestMenuUI: Mirroring {(isMirroring ? "enabled" : "disabled")}.");
    }

    private void OnSetStartStateClicked()
    {
        if (!startSet)
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
        if (!goalSet)
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

    private double[] ConvertJointAnglesWithinRange(double[] angles)
    {
        // Assumes angles are in degrees, convert to radians and adjust within joint limits
        double[] adjustedAngles = new double[angles.Length];
        
        for (int i = 0; i < angles.Length; i++)
        {
            float minLimit = jointLimits[i].Item1 * Mathf.Deg2Rad;
            float maxLimit = jointLimits[i].Item2 * Mathf.Deg2Rad;
            double angle = angles[i] * Mathf.Deg2Rad;

            // Normalize angle to be within -π to π
            angle = (angle + Math.PI) % (2 * Math.PI);
            if (angle < 0)
                angle += 2 * Math.PI;
            angle -= Math.PI;

            // Adjust angle to be within joint limits
            if (angle < minLimit)
                angle += 2 * Math.PI;
            else if (angle > maxLimit)
                angle -= 2 * Math.PI;

            // Final check to ensure it's within limits
            if (angle < minLimit || angle > maxLimit)
            {
                Debug.LogWarning($"MoveItPlanningRequestMenuUI: Joint {i} angle {angle * Mathf.Rad2Deg}° is out of limits ({minLimit * Mathf.Rad2Deg}°, {maxLimit * Mathf.Rad2Deg}°). Clamping.");
                angle = Math.Max(minLimit, Math.Min(maxLimit, angle));
            }

            adjustedAngles[i] = angle;
        }
        
        return adjustedAngles;
    }

    private RobotStateMsg GetCurrentRobotState()
    {
        // This method should get the current robot state from your robot or simulation
        // For now, we'll create a placeholder state
        // Convert joint angles from degrees to radians
        var jointAnglesDegrees = robotManager.GetJointAngles();
        var jointAnglesRadians = ConvertJointAnglesWithinRange(jointAnglesDegrees.Select(x => (double)x * -1).ToArray());

        var robotState = new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    frame_id = "base_link",
                    stamp = new TimeMsg
                    {
                        sec = (int)Time.time,
                        nanosec = (uint)((Time.time - (int)Time.time) * 1e9)
                    }
                },
                name = jointNames,
                position = jointAnglesRadians,
                velocity = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
                effort = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };

        return robotState;
    }

    private RobotStateMsg GetGoalRobotState()
    {
        // This method should get the goal robot state from user interaction or predefined poses
        // For now, we'll create a placeholder state with different joint values
        var jointAnglesDegrees = robotManager.GetJointAngles();
        var jointAnglesRadians = ConvertJointAnglesWithinRange(jointAnglesDegrees.Select(x => (double)x * -1).ToArray());

        var robotState = new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
            header = new HeaderMsg
            {
                frame_id = "base_link",
                stamp = new TimeMsg { 
                sec = (int)Time.time,
                nanosec = (uint)((Time.time - (int)Time.time) * 1e9)
                }
            },
            name = jointNames,
            position = jointAnglesRadians,
            velocity = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
            effort = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
        
        return robotState;
    }

    private void OnMotionPlanResponse(GetMotionPlanResponse response)
    {
        var motionPlanResponse = response.motion_plan_response;
        if (motionPlanResponse.error_code.val == 1) // SUCCESS
        {
            Debug.Log($"MoveItPlanningRequestMenuUI: Planning successful! Planning time: {motionPlanResponse.planning_time}s");

            // Handle the planned trajectory
            if (motionPlanResponse.trajectory?.joint_trajectory != null)
            {
                lastPlannedTrajectory = motionPlanResponse.trajectory.joint_trajectory;
                Debug.Log($"MoveItPlanningRequestMenuUI: Trajectory has {lastPlannedTrajectory.points.Length} waypoints");

                planningResultLabel.text = $"Planning successful! Time: {motionPlanResponse.planning_time}s, Waypoints: {lastPlannedTrajectory.points.Length}";

                // You can execute the trajectory here or store it for later execution
                PreviewTrajectory(lastPlannedTrajectory);
                executeTrajectoryButton.SetEnabled(true);
            }
        }
        else
        {
            planningResultLabel.text = $"Planning failed with error: {motionPlanResponse.error_code.val} {motionPlanResponse.error_code.message}";
            lastPlannedTrajectory = null;
            stopReplayButton.SetEnabled(false);
            executeTrajectoryButton.SetEnabled(false);

            Debug.LogError($"MoveItPlanningRequestMenuUI: Planning failed with error code: {motionPlanResponse.error_code.val} - {motionPlanResponse.error_code.message}");
        }
    }

    private void PreviewTrajectory(JointTrajectoryMsg trajectory)
    {
        // Use the TrajectoryReplay component to visualize the trajectory
        if (trajectoryReplayer != null)
        {
            stopReplayButton.SetEnabled(true);
            isReplaying = true;
            stopReplayButton.text = "Stop Replay";
            StartCoroutine(trajectoryReplayer.StartReplay(trajectory));
        }
        else
        {
            Debug.Log("MoveItPlanningRequestMenuUI: No TrajectoryReplay component assigned.");
        }
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
        setStartStateButton.SetEnabled(true);
        setGoalStateButton.SetEnabled(true);
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
