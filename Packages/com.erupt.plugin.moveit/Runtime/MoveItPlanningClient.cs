using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;

/// <summary>Topics, services and planner defaults; serialised on <see cref="MoveItPlugin"/>.</summary>
[Serializable]
public class MoveItPlanningSettings
{
    [Header("Topics and services")]
    public string jointStateTopic = "/joint_states";
    public string executeTrajectoryTopic = "/joint_trajectory_controller/joint_trajectory";
    public string motionPlanServiceName = "/plan_kinematic_path";
    public string plannerQueryServiceName = "/query_planner_interface";
    [Tooltip("Optional: mirror DisplayTrajectory messages onto the robot.")]
    public string displayTrajectoryTopic = "";

    [Header("Planner defaults")]
    public string planningGroupName = "ur_manipulator";
    public string planningPipelineId = "ompl";
    public string defaultPlannerId = "ur_manipulator";
    public int defaultNumPlanningAttempts = 10;
    public float defaultAllowedPlanningTime = 5.0f;
    public double goalTolerance = 0.01;

    [Header("Joint name remapping")]
    [Tooltip("Prefix in the ROS joint names (e.g. 'panda_')")]
    public string rosJointNamePrefix = "panda_";
    [Tooltip("Prefix used by the Unity robot joints (e.g. 'fr3_')")]
    public string unityJointNamePrefix = "fr3_";
}

/// <summary>One planning pipeline and the planners it offers, from /query_planner_interface.</summary>
public sealed class PlannerListing
{
    public string PipelineId;
    public string[] PlannerIds = Array.Empty<string>();
}

/// <summary>
/// The transport half of MoveIt planning: joint-state mirroring, planner discovery,
/// robot-state capture, the motion-plan service call, and trajectory execution. No UI,
/// no MonoBehaviour: constructed with an <see cref="IRosBus"/> and an <see cref="IRobotModel"/>
/// so it runs unchanged against a FakeRosBus in tests.
/// </summary>
public sealed class MoveItPlanningClient : IDisposable
{
    private readonly IRosBus ros;
    private readonly IRobotModel robot;
    private readonly MoveItPlanningSettings settings;
    private bool mirroring;
    private bool servicesRegistered;
    private bool queryInFlight;
    private Action<DisplayTrajectoryMsg> displayHandler;

    public IReadOnlyList<PlannerListing> Planners { get; private set; } = Array.Empty<PlannerListing>();
    public bool IsMirroring => mirroring;
    public bool IsConnected => ros != null;
    public MoveItPlanningSettings Settings => settings;

    /// <summary>Fires after each successful planner query.</summary>
    public event Action<IReadOnlyList<PlannerListing>> PlannersUpdated;
    /// <summary>Joint states applied to the robot while mirroring (Unity joint names).</summary>
    public event Action<JointStateMsg> JointStateMirrored;

    public MoveItPlanningClient(IRosBus ros, IRobotModel robot, MoveItPlanningSettings settings)
    {
        this.ros = ros ?? throw new ArgumentNullException(nameof(ros));
        this.robot = robot;
        this.settings = settings ?? new MoveItPlanningSettings();
    }

    /// <summary>Register the publisher, services and subscriptions. Idempotent.</summary>
    public void Connect()
    {
        if (servicesRegistered) return;
        ros.RegisterPublisher<JointTrajectoryMsg>(settings.executeTrajectoryTopic);
        ros.Subscribe<JointStateMsg>(settings.jointStateTopic, OnJointState);
        ros.RegisterRosService<GetMotionPlanRequest, GetMotionPlanResponse>(settings.motionPlanServiceName);
        ros.RegisterRosService<QueryPlannerInterfacesRequest, QueryPlannerInterfacesResponse>(settings.plannerQueryServiceName);
        if (!string.IsNullOrEmpty(settings.displayTrajectoryTopic))
        {
            displayHandler = OnDisplayTrajectory;
            ros.Subscribe(settings.displayTrajectoryTopic, displayHandler);
        }
        servicesRegistered = true;
    }

    public void Dispose()
    {
        if (!servicesRegistered) return;
        ros.Unsubscribe<JointStateMsg>(settings.jointStateTopic, OnJointState);
        if (displayHandler != null) ros.Unsubscribe(settings.displayTrajectoryTopic, displayHandler);
        servicesRegistered = false;
    }

    // --- planner discovery -----------------------------------------------------

    /// <summary>Ask MoveIt which pipelines and planners exist. Returns false if a query is already in flight or ROS is not connected.</summary>
    public bool QueryPlanners(Action<IReadOnlyList<PlannerListing>> done = null)
    {
        if (queryInFlight || !ros.HasConnectionThread) return false;
        Connect();
        queryInFlight = true;
        try
        {
            ros.SendServiceMessage<QueryPlannerInterfacesResponse>(settings.plannerQueryServiceName,
                new QueryPlannerInterfacesRequest(), resp =>
                {
                    queryInFlight = false;
                    if (resp?.planner_interfaces == null) return;
                    Planners = resp.planner_interfaces
                        .Select(d => new PlannerListing { PipelineId = d.pipeline_id ?? "", PlannerIds = d.planner_ids ?? Array.Empty<string>() })
                        .ToList();
                    PlannersUpdated?.Invoke(Planners);
                    done?.Invoke(Planners);
                });
            return true;
        }
        catch (Exception e)
        {
            queryInFlight = false;
            Debug.LogWarning($"[MoveItPlanningClient] Planner query failed: {e.Message}");
            return false;
        }
    }

    public string[] PlannersFor(string pipelineId) =>
        Planners.FirstOrDefault(p => p.PipelineId == pipelineId)?.PlannerIds ?? Array.Empty<string>();

    // --- robot state -------------------------------------------------------------

    /// <summary>The robot's current joint state as MoveIt expects it (ROS joint names).</summary>
    public RobotStateMsg CaptureRobotState()
    {
        if (robot == null) return new RobotStateMsg();

        string[] names = ToRosNames(robot.GetJointStateNames());
        double[] positions = Array.ConvertAll(robot.GetJointStatePositions(), p => (double)p);
        double[] zeros = new double[names.Length];

        return new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg { frame_id = "base_link", stamp = Now() },
                name = names,
                position = positions,
                velocity = zeros,
                effort = zeros
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
    }

    public void SetMirroring(bool on) => mirroring = on;

    private void OnJointState(JointStateMsg jointState)
    {
        if (!mirroring || robot == null) return;
        robot.ApplyJointState(ToUnityNames(jointState.name), jointState.position);
        JointStateMirrored?.Invoke(jointState);
    }

    private void OnDisplayTrajectory(DisplayTrajectoryMsg msg)
    {
        var traj = msg?.trajectory?.FirstOrDefault()?.joint_trajectory;
        if (traj == null || robot == null || traj.points == null || traj.points.Length == 0) return;
        robot.ApplyJointState(ToUnityNames(traj.joint_names), traj.points[^1].positions);
    }

    // --- planning ------------------------------------------------------------------

    /// <summary>Build the request the way the legacy panel did: joint-space goal constraints with a symmetric tolerance.</summary>
    public MotionPlanRequestMsg BuildRequest(RobotStateMsg start, RobotStateMsg goal, PlanPreferences prefs)
    {
        prefs ??= new PlanPreferences();
        return new MotionPlanRequestMsg
        {
            workspace_parameters = new WorkspaceParametersMsg(),
            start_state = start,
            goal_constraints = new[]
            {
                new ConstraintsMsg
                {
                    name = "goal_constraints",
                    joint_constraints = JointConstraints(goal),
                    position_constraints = Array.Empty<PositionConstraintMsg>(),
                    orientation_constraints = Array.Empty<OrientationConstraintMsg>(),
                    visibility_constraints = Array.Empty<VisibilityConstraintMsg>()
                }
            },
            path_constraints = new ConstraintsMsg(),
            trajectory_constraints = new TrajectoryConstraintsMsg(),
            reference_trajectories = Array.Empty<GenericTrajectoryMsg>(),
            pipeline_id = string.IsNullOrEmpty(prefs.PipelineId) ? settings.planningPipelineId : prefs.PipelineId,
            planner_id = string.IsNullOrEmpty(prefs.PlannerId) ? settings.defaultPlannerId : prefs.PlannerId,
            group_name = settings.planningGroupName,
            num_planning_attempts = prefs.Attempts > 0 ? prefs.Attempts : settings.defaultNumPlanningAttempts,
            allowed_planning_time = prefs.AllowedTimeSeconds > 0 ? prefs.AllowedTimeSeconds : settings.defaultAllowedPlanningTime,
            max_velocity_scaling_factor = 1.0,
            max_acceleration_scaling_factor = 1.0,
            cartesian_speed_limited_link = "",
            max_cartesian_speed = 0.0
        };
    }

    /// <summary>
    /// Call /plan_kinematic_path. On success the result's trajectory carries Unity joint
    /// names (ready for the player); on failure <paramref name="done"/> gets null and
    /// <paramref name="error"/> the MoveIt error text.
    /// </summary>
    public void RequestPlan(RobotStateMsg start, RobotStateMsg goal, PlanPreferences prefs,
                            Action<PlanResult> done, Action<string> error = null)
    {
        Connect();
        var request = BuildRequest(start, goal, prefs);
        ros.SendServiceMessage<GetMotionPlanResponse>(settings.motionPlanServiceName,
            new GetMotionPlanRequest(request), response =>
            {
                var r = response?.motion_plan_response;
                if (r == null || r.error_code == null || r.error_code.val != MoveItErrorCodesMsg.SUCCESS)
                {
                    string message = r?.error_code != null ? $"{r.error_code.val} {r.error_code.message}" : "no response";
                    error?.Invoke(message);
                    done?.Invoke(null);
                    return;
                }

                JointTrajectoryMsg rosTrajectory = r.trajectory?.joint_trajectory;
                done?.Invoke(new PlanResult
                {
                    Trajectory = rosTrajectory != null ? ToUnityTrajectory(rosTrajectory) : null,
                    PlannerPayload = r,
                    PlannerId = request.planner_id,
                    PlanningTimeSeconds = (float)r.planning_time
                });
            });
    }

    /// <summary>Publish a plan's trajectory (in ROS joint names) to the controller topic.</summary>
    public bool Execute(PlanResult plan)
    {
        if (plan == null || !plan.HasTrajectory) return false;
        Connect();
        JointTrajectoryMsg rosTrajectory = plan.PlannerPayload is MotionPlanResponseMsg r && r.trajectory?.joint_trajectory != null
            ? r.trajectory.joint_trajectory
            : ToRosTrajectory(plan.Trajectory);
        ros.Publish(settings.executeTrajectoryTopic, rosTrajectory);
        return true;
    }

    // --- joint name remapping --------------------------------------------------------

    public string[] ToUnityNames(string[] rosNames) => Remap(rosNames, settings.rosJointNamePrefix, settings.unityJointNamePrefix);
    public string[] ToRosNames(string[] unityNames) => Remap(unityNames, settings.unityJointNamePrefix, settings.rosJointNamePrefix);

    public JointTrajectoryMsg ToUnityTrajectory(JointTrajectoryMsg traj) =>
        new JointTrajectoryMsg { header = traj.header, joint_names = ToUnityNames(traj.joint_names), points = traj.points };

    public JointTrajectoryMsg ToRosTrajectory(JointTrajectoryMsg traj) =>
        new JointTrajectoryMsg { header = traj.header, joint_names = ToRosNames(traj.joint_names), points = traj.points };

    private static string[] Remap(string[] names, string from, string to)
    {
        if (names == null) return Array.Empty<string>();
        if (string.IsNullOrEmpty(from) || from == to) return names;
        var remapped = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
            remapped[i] = names[i].StartsWith(from, StringComparison.Ordinal) ? to + names[i].Substring(from.Length) : names[i];
        return remapped;
    }

    private JointConstraintMsg[] JointConstraints(RobotStateMsg state)
    {
        var js = state?.joint_state;
        if (js?.name == null || js.position == null) return Array.Empty<JointConstraintMsg>();
        int n = Math.Min(js.name.Length, js.position.Length);
        var constraints = new JointConstraintMsg[n];
        for (int i = 0; i < n; i++)
            constraints[i] = new JointConstraintMsg
            {
                joint_name = js.name[i],
                position = js.position[i],
                tolerance_above = settings.goalTolerance,
                tolerance_below = settings.goalTolerance,
                weight = 1.0
            };
        return constraints;
    }

    private static TimeMsg Now()
    {
        float t = Time.time;
        return new TimeMsg { sec = (int)t, nanosec = (uint)((t - (int)t) * 1e9) };
    }
}
