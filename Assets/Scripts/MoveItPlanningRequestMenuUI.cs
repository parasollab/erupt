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

public class MoveItPlanningRequestMenuUI : MonoBehaviour
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;
    
    [Header("MoveIt Configuration")]
    [SerializeField] private string planningGroupName = "manipulator";
    [SerializeField] private string planningPipelineId = "ompl";
    [SerializeField] private string defaultPlannerId = "RRTConnect";
    [SerializeField] private int defaultNumPlanningAttempts = 10;
    [SerializeField] private float defaultAllowedPlanningTime = 5.0f;
    
    [Header("ROS Topics")]
    [SerializeField] private string motionPlanRequestTopic = "/move_group/plan";
    [SerializeField] private string motionPlanResponseTopic = "/move_group/result";
    
    // UI Elements
    private VisualElement root;
    private Button setStartStateButton;
    private Button setGoalStateButton;
    private DropdownField plannerDropdown;
    private IntegerField numPlanningAttemptsField;
    private FloatField allowedPlanningTimeField;
    private Label planningRequestMenuLabel;
    
    // ROS Connection
    private ROSConnection ros;
    private bool isConnected = false;
    
    // Planning state
    private RobotStateMsg currentStartState;
    private RobotStateMsg currentGoalState;
    private bool hasStartState = false;
    private bool hasGoalState = false;
    
    // Available planners
    private readonly List<string> availablePlanners = new List<string>
    {
        "RRTConnect",
        "RRT",
        "RRTStar",
        "PRM",
        "PRMStar",
        "BKPIECE",
        "KPIECE",
        "BiTRRT",
        "FMT",
        "STRIDE",
        "SPARS",
        "SPARStwo"
    };

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
        
        InitializeUIElements();
        SetupEventHandlers();
        InitializeROSConnection();
    }

    private void InitializeUIElements()
    {
        // Get UI elements by name
        planningRequestMenuLabel = root.Q<Label>("planningRequestMenuLabel");
        setStartStateButton = root.Q<Button>("planningRequestSetStartButton");
        setGoalStateButton = root.Q<Button>("planningRequestSetGoalStateButton");
        plannerDropdown = root.Q<DropdownField>("planningRequestPlannerIDDropDown");
        numPlanningAttemptsField = root.Q<IntegerField>("planningRequestNumPlanningAttemptsInput");
        allowedPlanningTimeField = root.Q<FloatField>("planningRequestAllowedPlanningTimeInput");
        
        // Validate UI elements
        if (setStartStateButton == null || setGoalStateButton == null || 
            plannerDropdown == null || numPlanningAttemptsField == null || 
            allowedPlanningTimeField == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: One or more UI elements not found in UXML.");
            return;
        }
        
        // Initialize dropdown with available planners
        plannerDropdown.choices = availablePlanners;
        plannerDropdown.value = defaultPlannerId;
        
        // Set default values
        numPlanningAttemptsField.value = defaultNumPlanningAttempts;
        allowedPlanningTimeField.value = defaultAllowedPlanningTime;
        
        // Update button states
        UpdateButtonStates();
    }

    private void SetupEventHandlers()
    {
        setStartStateButton.clicked += OnSetStartStateClicked;
        setGoalStateButton.clicked += OnSetGoalStateClicked;
        
        // Add planning request button (if you want to add one)
        // planningRequestButton.clicked += OnPlanningRequestClicked;
    }

    private void InitializeROSConnection()
    {
        try
        {
            ros = ROSConnection.GetOrCreateInstance();
            isConnected = true;
            
            // Register for motion plan response
            ros.Subscribe<MotionPlanResponseMsg>(motionPlanResponseTopic, OnMotionPlanResponse);
            
            Debug.Log("MoveItPlanningRequestMenuUI: ROS connection established successfully.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"MoveItPlanningRequestMenuUI: Failed to establish ROS connection: {e.Message}");
            isConnected = false;
        }
    }

    private void OnSetStartStateClicked()
    {
        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }
        
        // Get current robot state (this would typically come from the robot or simulation)
        currentStartState = GetCurrentRobotState();
        hasStartState = true;
        
        Debug.Log("MoveItPlanningRequestMenuUI: Start state set successfully.");
        UpdateButtonStates();
    }

    private void OnSetGoalStateClicked()
    {
        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }
        
        // Get goal state (this could be from user interaction, predefined poses, etc.)
        currentGoalState = GetGoalRobotState();
        hasGoalState = true;
        
        Debug.Log("MoveItPlanningRequestMenuUI: Goal state set successfully.");
        UpdateButtonStates();
    }

    public void SendPlanningRequest()
    {
        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }
        
        if (!hasStartState || !hasGoalState)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: Both start and goal states must be set before planning.");
            return;
        }
        
        var planningRequest = new MotionPlanRequestMsg
        {
            workspace_parameters = new WorkspaceParametersMsg(),
            start_state = currentStartState,
            goal_constraints = CreateGoalConstraints(),
            path_constraints = new ConstraintsMsg(),
            trajectory_constraints = new TrajectoryConstraintsMsg(),
            reference_trajectories = new GenericTrajectoryMsg[0],
            pipeline_id = planningPipelineId,
            planner_id = plannerDropdown.value,
            group_name = planningGroupName,
            num_planning_attempts = numPlanningAttemptsField.value,
            allowed_planning_time = allowedPlanningTimeField.value,
            max_velocity_scaling_factor = 1.0,
            max_acceleration_scaling_factor = 1.0,
            cartesian_speed_limited_link = "",
            max_cartesian_speed = 0.0
        };
        ros.Publish(motionPlanRequestTopic, planningRequest);
        
        Debug.Log($"MoveItPlanningRequestMenuUI: Planning request sent with planner: {plannerDropdown.value}");
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
                    tolerance_above = 0.1,
                    tolerance_below = 0.1,
                    weight = 1.0
                };
                constraints.Add(constraint);
            }
        }
        
        return constraints.ToArray();
    }

    private RobotStateMsg GetCurrentRobotState()
    {
        // This method should get the current robot state from your robot or simulation
        // For now, we'll create a placeholder state
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
                name = new string[] { "joint1", "joint2", "joint3", "joint4", "joint5", "joint6" },
                position = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
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
                name = new string[] { "joint1", "joint2", "joint3", "joint4", "joint5", "joint6" },
                position = new double[] { 1.57, 0.0, 0.0, 0.0, 0.0, 0.0 },
                velocity = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
                effort = new double[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
        
        return robotState;
    }

    private void OnMotionPlanResponse(MotionPlanResponseMsg response)
    {
        if (response.error_code.val == 1) // SUCCESS
        {
            Debug.Log($"MoveItPlanningRequestMenuUI: Planning successful! Planning time: {response.planning_time}s");
            
            // Handle the planned trajectory
            if (response.trajectory?.joint_trajectory != null)
            {
                var trajectory = response.trajectory.joint_trajectory;
                Debug.Log($"MoveItPlanningRequestMenuUI: Trajectory has {trajectory.points.Length} waypoints");
                
                // You can execute the trajectory here or store it for later execution
                ExecuteTrajectory(trajectory);
            }
        }
        else
        {
            Debug.LogError($"MoveItPlanningRequestMenuUI: Planning failed with error code: {response.error_code.val} - {response.error_code.message}");
        }
    }

    private void ExecuteTrajectory(JointTrajectoryMsg trajectory)
    {
        // This method should execute the planned trajectory on your robot
        // Implementation depends on your robot control system
        Debug.Log("MoveItPlanningRequestMenuUI: Trajectory execution not implemented yet.");
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
        Debug.Log("MoveItPlanningRequestMenuUI: Planning state reset.");
    }

    public void SetPlanningGroup(string groupName)
    {
        planningGroupName = groupName;
        Debug.Log($"MoveItPlanningRequestMenuUI: Planning group set to: {planningGroupName}");
    }

    public void SetPlanningPipeline(string pipelineId)
    {
        planningPipelineId = pipelineId;
        Debug.Log($"MoveItPlanningRequestMenuUI: Planning pipeline set to: {planningPipelineId}");
    }

    private void OnDisable()
    {
        if (ros != null && isConnected)
        {
            ros.Unsubscribe(motionPlanResponseTopic);
        }
    }

    private void OnDestroy()
    {
        if (ros != null && isConnected)
        {
            ros.Unsubscribe(motionPlanResponseTopic);
        }
    }
}
