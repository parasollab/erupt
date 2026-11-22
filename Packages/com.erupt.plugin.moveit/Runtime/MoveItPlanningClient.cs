using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// MoveIt's service names and planner defaults; serialised on <see cref="MoveItPlugin"/>.
    /// Names are relative: they resolve under the namespace of the robot being planned for.
    /// What differs per robot (planning group, joint state and trajectory topics, joint names)
    /// comes from the robot's <see cref="RobotProfile"/>.
    /// </summary>
    [Serializable]
    public class MoveItPlanningSettings
    {
        [Header("Services (relative to the robot's namespace)")]
        public string motionPlanServiceName = "plan_kinematic_path";
        public string plannerQueryServiceName = "query_planner_interface";
        [Tooltip("Optional: mirror DisplayTrajectory messages onto the robot.")]
        public string displayTrajectoryTopic = "";

        [Header("Gripper")]
        [Tooltip("MoveIt's trajectory execution action. The gripper verb sends the gripper's open or closed position there, so MoveIt " +
                 "drives whichever controller the gripper has. Empty = the verb only moves the gripper shown in Unity.")]
        public string executeTrajectoryActionName = "execute_trajectory";
        [Header("Execution")]
        [Tooltip("An executed plan counts as finished when every joint the ROS robot reports is this close (rad, or m) to the end of the trajectory.")]
        [Min(1e-4f)] public float executionGoalTolerance = 0.01f;
        [Tooltip("Duration (s) of the gripper motion sent to MoveIt.")]
        [Min(0.1f)] public float gripperMoveSeconds = 1.0f;

        [Header("Planner defaults")]
        public string planningPipelineId = "ompl";
        [Tooltip("Planner to ask for until the user picks one. Empty = the pipeline's own default.")]
        public string defaultPlannerId = "";
        public int defaultNumPlanningAttempts = 10;
        public float defaultAllowedPlanningTime = 5.0f;
        public double goalTolerance = 0.01;
    }

    /// <summary>One planning pipeline and the planners it offers, from /query_planner_interface.</summary>
    public sealed class PlannerListing
    {
        public string PipelineId;
        public string[] PlannerIds = Array.Empty<string>();
    }

    /// <summary>
    /// The transport half of MoveIt planning for one robot: joint-state mirroring, planner
    /// discovery, robot-state capture, the motion-plan service call, and trajectory execution.
    /// A plan is checked against the robot's joint limits when it arrives, and one that leaves
    /// them is not published for execution unless <see cref="AllowExecutionOutsideLimits"/> is on.
    /// No UI, no MonoBehaviour. Everything goes through the robot's namespaced bus
    /// (<see cref="RobotInstance.Ros"/>) with relative names, and joint names cross the wire
    /// through the robot's name rule, so it runs unchanged against a FakeRosBus in tests.
    /// </summary>
    public sealed class MoveItPlanningClient : IDisposable
    {
        private readonly IRosBus ros;
        private readonly RobotInstance instance;
        private readonly IRobotModel robot;
        private readonly RobotNameMap nameMap;
        private readonly string jointStateTopic;
        private readonly string executeTrajectoryTopic;
        private readonly MoveItPlanningSettings settings;
        private bool warnedNoGroup;
        private bool mirroring;
        private bool servicesRegistered;
        private bool queryInFlight;
        private Action<DisplayTrajectoryMsg> displayHandler;
        private readonly LimitMismatchMonitor mismatches = new LimitMismatchMonitor();
        // The execution being followed: where its trajectory ends, and until when to wait for it.
        private sealed class FollowedExecution
        {
            public string[] RosJoints;
            public double[] Goal;
            public float Deadline;
            public float ReachedAt = -1f;
            public Action<bool, string> Settled;
        }
        private FollowedExecution following;
        private bool listeningForControl;
        // The gripper goes through MoveIt's execute_trajectory action; see CommandGripper.
        private IRosActionClient<ExecuteTrajectoryGoal, ExecuteTrajectoryResult, ExecuteTrajectoryFeedback> gripperAction;
        private bool gripperBusy;
        private bool? gripperQueued;
        // The last joint state the ROS robot reported (ROS joint names), mirrored or not.
        private readonly Dictionary<string, double> reportedJoints = new Dictionary<string, double>();

        public IReadOnlyList<PlannerListing> Planners { get; private set; } = Array.Empty<PlannerListing>();
        /// <summary>
        /// True while the Unity robot shows the ROS robot's joint states: switched on by hand
        /// ("Mirror joints"), or for the length of an execution (<see cref="Execute"/>).
        /// </summary>
        public bool IsMirroring => mirroring || following != null;
        /// <summary>True while an executed plan is being followed to its end.</summary>
        public bool IsFollowingExecution => following != null;
        /// <summary>Fires when <see cref="IsMirroring"/> changes.</summary>
        public event Action<bool> MirroringChanged;
        public bool IsConnected => ros != null;
        /// <summary>True while the ROS endpoint is reachable: requests sent now will be answered.</summary>
        public bool IsOnline => ros != null && ros.HasConnectionThread && !ros.HasConnectionError;
        public MoveItPlanningSettings Settings => settings;

        /// <summary>
        /// Show the ROS robot's joint state on the Unity robot once, when the first joint state
        /// arrives after the connection has come up (<see cref="Connect"/>,
        /// <see cref="ConnectionRestored"/>), so the user starts from where the real robot is
        /// and not from the profile's initial pose. It does not happen when something has taken
        /// the robot's pose since (the user moved it, a preview plays). Off by default here; the
        /// plugin switches it on from its checkbox before it connects the client.
        /// </summary>
        public bool AdoptsReportedState { get; set; }
        /// <summary>True from the connection coming up until the reported state has been adopted (or the user moved the robot first).</summary>
        public bool IsWaitingToAdopt => adoptPending != null;
        /// <summary>Fires when the robot has taken the joint state its ROS side reported.</summary>
        public event Action StateAdopted;
        // Unity joint names still to take from the ROS robot; null when nothing is to be adopted.
        private HashSet<string> adoptPending;
        // The joints of one robot may be reported by several publishers; the ones that have not
        // been heard this long after the first are left as they are.
        private const float AdoptWindowSeconds = 2f;
        private float adoptUntil = -1f;

        /// <summary>Fires after each successful planner query.</summary>
        public event Action<IReadOnlyList<PlannerListing>> PlannersUpdated;
        /// <summary>Joint states applied to the robot while mirroring (Unity joint names).</summary>
        public event Action<JointStateMsg> JointStateMirrored;
        /// <summary>
        /// Something the user should be told: a mirrored joint state outside the robot model's
        /// limits (once per joint, then rarely), or an execution that was refused.
        /// </summary>
        public event Action<InteractionRefusal> Refused;

        /// <summary>Publish plans that leave the robot's joint limits anyway. Off by default.</summary>
        public bool AllowExecutionOutsideLimits { get; set; }
        /// <summary>How far past a limit a plan may go before it counts as outside the limits.</summary>
        public JointLimitTolerance LimitTolerance
        {
            get => limitTolerance;
            set
            {
                limitTolerance = value;
                mismatches.Tolerance = value;
            }
        }
        private JointLimitTolerance limitTolerance = JointLimitTolerance.Default;

        /// <summary>The robot this client plans for.</summary>
        public RobotInstance Robot => instance;
        /// <summary>The SRDF planning group requests are made for (the profile's arm group).</summary>
        public string PlanningGroup => instance.Profile != null ? instance.Profile.armGroup ?? "" : "";
        /// <summary>False for a robot whose profile names no trajectory command topic: plans cannot be executed.</summary>
        public bool CanExecute => !string.IsNullOrEmpty(executeTrajectoryTopic);

        /// <param name="robot">A registered robot whose registry has the ROS bus.</param>
        public MoveItPlanningClient(RobotInstance robot, MoveItPlanningSettings settings)
        {
            instance = robot != null ? robot : throw new ArgumentNullException(nameof(robot));
            ros = robot.Ros ?? throw new InvalidOperationException(
                $"Robot '{robot.Id}' has no ROS bus yet; it must be registered and its registry given the bus.");
            this.robot = robot.Model;
            nameMap = robot.Names;
            jointStateTopic = robot.JointStatesTopic;
            executeTrajectoryTopic = robot.TrajectoryCommandTopic;
            this.settings = settings ?? new MoveItPlanningSettings();
        }

        /// <summary>Register the publisher, services and subscriptions. Idempotent.</summary>
        public void Connect()
        {
            if (servicesRegistered) return;
            if (!string.IsNullOrEmpty(executeTrajectoryTopic))
                ros.RegisterPublisher<JointTrajectoryMsg>(executeTrajectoryTopic);
            ros.Subscribe<JointStateMsg>(jointStateTopic, OnJointState);
            ros.RegisterRosService<GetMotionPlanRequest, GetMotionPlanResponse>(settings.motionPlanServiceName);
            ros.RegisterRosService<QueryPlannerInterfacesRequest, QueryPlannerInterfacesResponse>(settings.plannerQueryServiceName);
            if (!string.IsNullOrEmpty(settings.displayTrajectoryTopic))
            {
                displayHandler = OnDisplayTrajectory;
                ros.Subscribe(settings.displayTrajectoryTopic, displayHandler);
            }
            if (CanCommandGripper)
                gripperAction = ros.RegisterActionClient<ExecuteTrajectoryGoal, ExecuteTrajectoryResult, ExecuteTrajectoryFeedback>(settings.executeTrajectoryActionName);
            if (robot != null && !listeningForControl)
            {
                robot.ControlTaken += OnControlTaken;
                listeningForControl = true;
            }
            servicesRegistered = true;
            ArmAdoption();
        }

        // From now on the next joint state the ROS robot reports is shown on the Unity robot.
        private void ArmAdoption()
        {
            adoptPending = null;
            adoptUntil = -1f;
            if (!AdoptsReportedState || robot == null) return;
            adoptPending = new HashSet<string>(robot.GetJointStateNames());
            if (adoptPending.Count == 0) adoptPending = null;
        }

        // The joints a message names that have not been adopted yet are written, once each,
        // like any other state: clamped to the limits, a mismatch reported.
        private void Adopt(JointStateMsg jointState)
        {
            if (adoptUntil >= 0f && Time.realtimeSinceStartup > adoptUntil) { adoptPending = null; return; }
            if (jointState?.name == null || jointState.position == null) return;

            string[] names = ToUnityNames(jointState.name);
            var adoptedNames = new List<string>();
            var adoptedPositions = new List<double>();
            for (int i = 0; i < names.Length && i < jointState.position.Length; i++)
            {
                if (!adoptPending.Remove(names[i])) continue;
                adoptedNames.Add(names[i]);
                adoptedPositions.Add(jointState.position[i]);
            }
            if (adoptedNames.Count == 0) return;

            bool first = adoptUntil < 0f;
            if (first) adoptUntil = Time.realtimeSinceStartup + AdoptWindowSeconds;
            if (adoptPending.Count == 0) adoptPending = null;
            Mirror(robot.ApplyJointState(adoptedNames.ToArray(), adoptedPositions.ToArray()));
            if (first)
            {
                Debug.Log($"[moveit] Robot '{instance.Id}' now shows the joint state its ROS side reports.");
                StateAdopted?.Invoke();
            }
        }

        public void Dispose()
        {
            if (!servicesRegistered) return;
            ros.Unsubscribe<JointStateMsg>(jointStateTopic, OnJointState);
            if (displayHandler != null) ros.Unsubscribe(settings.displayTrajectoryTopic, displayHandler);
            if (listeningForControl)
            {
                robot.ControlTaken -= OnControlTaken;
                listeningForControl = false;
            }
            following = null;
            adoptPending = null;
            servicesRegistered = false;
        }

        // --- planner discovery -----------------------------------------------------

        /// <summary>Ask MoveIt which pipelines and planners exist. Returns false if a query is already in flight or ROS is not connected.</summary>
        public bool QueryPlanners(Action<IReadOnlyList<PlannerListing>> done = null)
        {
            // The endpoint drops a request for a service that is not up yet (move_group still
            // starting) without a word, so an unanswered query is given up after a while and
            // asked again; a late answer to an abandoned query is still welcome.
            if (queryInFlight && Time.realtimeSinceStartup - querySentAt > QueryTimeoutSeconds) queryInFlight = false;
            if (queryInFlight || !IsOnline) return false;
            Connect();
            queryInFlight = true;
            querySentAt = Time.realtimeSinceStartup;
            try
            {
                ros.SendServiceMessage<QueryPlannerInterfacesResponse>(settings.plannerQueryServiceName,
                    new QueryPlannerInterfacesRequest(), resp =>
                    {
                        queryInFlight = false;
                        // A request the endpoint could not deliver comes back as an empty
                        // message; MoveIt itself always lists at least one pipeline.
                        if (resp?.planner_interfaces == null || resp.planner_interfaces.Length == 0) return;
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

        /// <summary>
        /// The planners of a pipeline that can be asked for by name for this robot's arm group
        /// (see <see cref="PlannersForGroup"/>); <see cref="Planners"/> has the list as MoveIt sent it.
        /// </summary>
        public string[] PlannersFor(string pipelineId) =>
            PlannersForGroup(Planners.FirstOrDefault(p => p.PipelineId == pipelineId)?.PlannerIds, PlanningGroup);

        /// <summary>
        /// What a pipeline's planner list offers one planning group. OMPL lists every group's
        /// configurations as <c>group[planner]</c> next to the bare group names (each group's
        /// default); only the planners of <paramref name="group"/> are of use, under their own
        /// names, and a group name is not a planner. Pipelines that list plain planner names
        /// (Pilz: PTP, LIN, CIRC) are passed through. An empty result means "the pipeline's default".
        /// </summary>
        public static string[] PlannersForGroup(string[] plannerIds, string group)
        {
            if (plannerIds == null || plannerIds.Length == 0) return Array.Empty<string>();
            if (string.IsNullOrEmpty(group)) return plannerIds;

            // Group names: whatever stands in front of a bracket, and the group itself.
            var groups = new HashSet<string> { group };
            foreach (string id in plannerIds)
            {
                int open = id.IndexOf('[');
                if (open > 0 && id.EndsWith("]", StringComparison.Ordinal)) groups.Add(id.Substring(0, open));
            }
            // A list that names the group bare is OMPL's: its other bare entries are groups too.
            bool listsGroups = Array.IndexOf(plannerIds, group) >= 0;

            var result = new List<string>();
            foreach (string id in plannerIds)
            {
                int open = id.IndexOf('[');
                string planner;
                if (open > 0 && id.EndsWith("]", StringComparison.Ordinal))
                {
                    if (id.Substring(0, open) != group) continue;
                    planner = id.Substring(open + 1, id.Length - open - 2);
                }
                else
                {
                    if (listsGroups || groups.Contains(id)) continue;
                    planner = id;
                }
                if (planner.Length > 0 && !result.Contains(planner)) result.Add(planner);
            }
            return result.ToArray();
        }

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
                    header = new HeaderMsg { frame_id = instance.PlanningFrameId, stamp = Now() },
                    name = names,
                    position = positions,
                    velocity = zeros,
                    effort = zeros
                },
                multi_dof_joint_state = new MultiDOFJointStateMsg()
            };
        }

        /// <summary>
        /// Show the ROS robot's joint states on the Unity robot from now on (true), or stop
        /// doing so (false, which also stops following an execution). The mirror also stops by
        /// itself when something else takes the robot's pose (<see cref="IRobotModel.ControlTaken"/>):
        /// the user dragging it, a preview, a replay.
        /// </summary>
        public void SetMirroring(bool on)
        {
            bool was = IsMirroring;
            mirroring = on;
            if (!on) following = null;
            if (was != IsMirroring) MirroringChanged?.Invoke(IsMirroring);
        }

        // The user or a playback took the robot's pose: the mirror lets go, or the two would
        // write the joints in turn and neither the user's pose nor the preview would show.
        private void OnControlTaken()
        {
            // The pose is somebody's now: it is not replaced by what the ROS robot reports.
            adoptPending = null;
            if (!IsMirroring) return;
            mirroring = false;
            following = null;
            MirroringChanged?.Invoke(false);
        }

        private void OnJointState(JointStateMsg jointState)
        {
            if (jointState?.name != null && jointState.position != null)
                for (int i = 0; i < jointState.name.Length && i < jointState.position.Length; i++)
                    reportedJoints[jointState.name[i]] = jointState.position[i];
            if (robot == null) return;
            if (!IsMirroring)
            {
                if (adoptPending != null) Adopt(jointState);
                return;
            }
            // A mirror shows every state; there is nothing left to adopt.
            adoptPending = null;
            Mirror(robot.ApplyJointState(ToUnityNames(jointState.name), jointState.position));
            JointStateMirrored?.Invoke(jointState);
            // Within the tolerance the robot is still moving the last stretch: keep following
            // for a moment so the Unity robot comes to rest where the real one does.
            if (following != null && following.ReachedAt < 0f && DistanceToGoal(following, out _) <= settings.executionGoalTolerance)
                following.ReachedAt = Time.realtimeSinceStartup;
            if (following != null && following.ReachedAt >= 0f && Time.realtimeSinceStartup - following.ReachedAt >= SettleSeconds)
                FinishFollowing(true, null);
        }

        /// <summary>
        /// How long (s) the Unity robot keeps following after the ROS robot has come within the
        /// goal tolerance, so that it comes to rest where the real one does.
        /// </summary>
        public float ExecutionSettleSeconds { get; set; } = 0.5f;
        private float SettleSeconds => Mathf.Max(0f, ExecutionSettleSeconds);

        /// <summary>
        /// Give up on an execution the robot has not finished in time. Call regularly (the
        /// plugin does, every frame); cheap when nothing is being followed.
        /// </summary>
        public void Tick()
        {
            if (following == null) return;
            if (following.ReachedAt >= 0f)
            {
                // Joint states may stop arriving (a robot that publishes on change only).
                if (Time.realtimeSinceStartup - following.ReachedAt >= SettleSeconds) FinishFollowing(true, null);
                return;
            }
            if (Time.realtimeSinceStartup <= following.Deadline) return;
            double off = DistanceToGoal(following, out string joint);
            FinishFollowing(false, double.IsInfinity(off)
                ? "the robot reported no joint state for the executed joints"
                : $"the robot did not reach the end of the trajectory ({joint} is {off:F3} away)");
        }

        // How far the ROS robot is from the end of the followed trajectory: the worst joint.
        private double DistanceToGoal(FollowedExecution execution, out string worstJoint)
        {
            double worst = 0;
            worstJoint = null;
            for (int i = 0; i < execution.RosJoints.Length && i < execution.Goal.Length; i++)
            {
                double off = reportedJoints.TryGetValue(execution.RosJoints[i], out double at)
                    ? Math.Abs(at - execution.Goal[i]) : double.PositiveInfinity;
                if (off < worst) continue;
                worst = off;
                worstJoint = execution.RosJoints[i];
            }
            return worst;
        }

        private void FinishFollowing(bool reached, string problem)
        {
            FollowedExecution finished = following;
            following = null;
            if (!IsMirroring) MirroringChanged?.Invoke(false);
            finished?.Settled?.Invoke(reached, problem);
        }

        // A mirrored state is shown clamped. The real robot being outside the model's limits
        // means the model is wrong, which the user hears about once per joint, not per message.
        private void Mirror(JointWriteReport written)
        {
            if (written.AnyClamped && Refused != null) mismatches.Observe(written, robot, Refused);
        }

        private void OnDisplayTrajectory(DisplayTrajectoryMsg msg)
        {
            var traj = msg?.trajectory?.FirstOrDefault()?.joint_trajectory;
            if (traj == null || robot == null || traj.points == null || traj.points.Length == 0) return;
            Mirror(robot.ApplyJointState(ToUnityNames(traj.joint_names), traj.points[^1].positions));
        }

        // --- planning ------------------------------------------------------------------

        /// <summary>Build the request the way the legacy panel did: joint-space goal constraints with a symmetric tolerance.</summary>
        public MotionPlanRequestMsg BuildRequest(RobotStateMsg start, RobotStateMsg goal, PlanPreferences prefs)
        {
            prefs ??= new PlanPreferences();
            if (PlanningGroup.Length == 0 && !warnedNoGroup)
            {
                warnedNoGroup = true;
                Debug.LogWarning($"[MoveItPlanningClient] Robot '{instance.Id}' has no arm planning group (set one on its robot profile); MoveIt will refuse the request.");
            }
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
                group_name = PlanningGroup,
                num_planning_attempts = prefs.Attempts > 0 ? prefs.Attempts : settings.defaultNumPlanningAttempts,
                allowed_planning_time = prefs.AllowedTimeSeconds > 0 ? prefs.AllowedTimeSeconds : settings.defaultAllowedPlanningTime,
                max_velocity_scaling_factor = 1.0,
                max_acceleration_scaling_factor = 1.0,
                cartesian_speed_limited_link = "",
                max_cartesian_speed = 0.0
            };
        }

        /// <summary>
        /// Call the motion-plan service. On success the result's trajectory carries Unity joint
        /// names (ready for the player); on failure <paramref name="done"/> gets null and
        /// <paramref name="error"/> the MoveIt error text.
        /// </summary>
        public void RequestPlan(RobotStateMsg start, RobotStateMsg goal, PlanPreferences prefs,
                                Action<PlanResult> done, Action<string> error = null)
        {
            // A service request sent while nothing is connected is never answered: the caller
            // would wait for ever. Say so instead.
            if (!IsOnline)
            {
                error?.Invoke("ROS is not connected");
                done?.Invoke(null);
                return;
            }
            Connect();
            var request = BuildRequest(start, goal, prefs);
            // A request in flight when the connection drops is never answered; it is failed
            // when the connection comes back (ConnectionRestored), and a late answer is ignored.
            var pending = new PendingPlan { Done = done, Error = error };
            pendingPlans.Add(pending);
            ros.SendServiceMessage<GetMotionPlanResponse>(settings.motionPlanServiceName,
                new GetMotionPlanRequest(request), response =>
                {
                    if (!pendingPlans.Remove(pending)) return;
                    var r = response?.motion_plan_response;
                    if (r == null || r.error_code == null || r.error_code.val != MoveItErrorCodesMsg.SUCCESS)
                    {
                        string message = r?.error_code != null ? DescribeError(r.error_code) : "no response";
                        error?.Invoke(message);
                        done?.Invoke(null);
                        return;
                    }

                    JointTrajectoryMsg rosTrajectory = r.trajectory?.joint_trajectory;
                    JointTrajectoryMsg trajectory = rosTrajectory != null ? ToUnityTrajectory(rosTrajectory) : null;
                    done?.Invoke(new PlanResult
                    {
                        Trajectory = trajectory,
                        PlannerPayload = r,
                        PlannerId = request.planner_id,
                        PlanningTimeSeconds = (float)r.planning_time,
                        // Checked once, here, where the plan arrives.
                        LimitViolations = PlanningPlugin.FindLimitViolations(instance, new[] { trajectory }, limitTolerance)
                    });
                });
        }

        private const float QueryTimeoutSeconds = 4f;
        private float querySentAt;

        private sealed class PendingPlan
        {
            public Action<PlanResult> Done;
            public Action<string> Error;
        }
        private readonly List<PendingPlan> pendingPlans = new List<PendingPlan>();

        /// <summary>
        /// The connection to ROS is back after having been down. Requests that were in flight
        /// were lost with it: waiting plan requests fail now, with the reason, and the planner
        /// query may be sent again. The robot adopts the next joint state its ROS side reports
        /// (<see cref="AdoptsReportedState"/>).
        /// </summary>
        public void ConnectionRestored()
        {
            // The ROS side may have been restarted and its robot may stand somewhere else.
            if (servicesRegistered) ArmAdoption();
            queryInFlight = false;
            PendingPlan[] lost = pendingPlans.ToArray();
            pendingPlans.Clear();
            foreach (PendingPlan plan in lost)
            {
                plan.Error?.Invoke("the connection to ROS was lost while planning");
                plan.Done?.Invoke(null);
            }
        }

        /// <summary>
        /// A MoveIt error code as something a person can read: what the code means, the number,
        /// and MoveIt's own text when it sent one. move_group often answers a rejected request
        /// (start state in collision, for one) with the generic FAILURE and no text; its log has
        /// the reason.
        /// </summary>
        public static string DescribeError(MoveItErrorCodesMsg code)
        {
            if (code == null) return "no response";
            string meaning = code.val switch
            {
                MoveItErrorCodesMsg.FAILURE => "MoveIt could not plan (the start or the goal may be in collision; the move_group log has the reason)",
                MoveItErrorCodesMsg.PLANNING_FAILED => "no path found",
                MoveItErrorCodesMsg.INVALID_MOTION_PLAN => "the plan found is not valid",
                MoveItErrorCodesMsg.MOTION_PLAN_INVALIDATED_BY_ENVIRONMENT_CHANGE => "the environment changed while planning",
                MoveItErrorCodesMsg.TIMED_OUT => "planning timed out",
                MoveItErrorCodesMsg.PREEMPTED => "planning was cancelled",
                MoveItErrorCodesMsg.START_STATE_IN_COLLISION => "the start state is in collision",
                MoveItErrorCodesMsg.START_STATE_VIOLATES_PATH_CONSTRAINTS => "the start state violates the path constraints",
                MoveItErrorCodesMsg.START_STATE_INVALID => "the start state is not valid",
                MoveItErrorCodesMsg.GOAL_IN_COLLISION => "the goal is in collision",
                MoveItErrorCodesMsg.GOAL_VIOLATES_PATH_CONSTRAINTS => "the goal violates the path constraints",
                MoveItErrorCodesMsg.GOAL_CONSTRAINTS_VIOLATED => "the goal constraints are violated",
                MoveItErrorCodesMsg.GOAL_STATE_INVALID => "the goal state is not valid",
                MoveItErrorCodesMsg.INVALID_GROUP_NAME => "MoveIt does not know the planning group",
                MoveItErrorCodesMsg.INVALID_GOAL_CONSTRAINTS => "the goal constraints are not valid",
                MoveItErrorCodesMsg.INVALID_ROBOT_STATE => "the robot state is not valid",
                MoveItErrorCodesMsg.FRAME_TRANSFORM_FAILURE => "a frame could not be transformed",
                MoveItErrorCodesMsg.NO_IK_SOLUTION => "no inverse kinematics solution",
                _ => "MoveIt error"
            };
            string text = string.IsNullOrWhiteSpace(code.message) ? "" : ": " + code.message.Trim();
            return $"{meaning} ({code.val}){text}";
        }

        /// <summary>
        /// Publish a plan's trajectory (in ROS joint names) to the robot's trajectory command
        /// topic. A plan that leaves the robot's joint limits is not published unless
        /// <see cref="AllowExecutionOutsideLimits"/> is on.
        /// </summary>
        /// <remarks>
        /// The controller's command topic gives no completion signal, so the client watches the
        /// robot's joint states instead: the Unity robot follows the ROS robot until it reports
        /// the trajectory's last point (then <paramref name="settled"/> gets true) or until twice
        /// the trajectory's duration plus ten seconds have passed (false, with the reason). After
        /// that the Unity robot is the user's again; it only keeps following when "Mirror joints"
        /// was switched on by hand.
        /// </remarks>
        /// <returns>False when nothing was published.</returns>
        public bool Execute(PlanResult plan, Action<bool, string> settled = null)
        {
            if (plan == null || !plan.HasTrajectory) return false;
            if (plan.HasLimitViolations && !AllowExecutionOutsideLimits)
            {
                Debug.LogWarning($"[MoveItPlanningClient] Not executed: the plan is outside the joint limits of robot '{instance.Id}' " +
                                 $"({JointLimitViolation.Describe(plan.LimitViolations)}).");
                return false;
            }
            if (string.IsNullOrEmpty(executeTrajectoryTopic))
            {
                Debug.LogWarning($"[MoveItPlanningClient] Robot '{instance.Id}' names no trajectory command topic (set one on its robot profile); nothing was sent.");
                return false;
            }
            Connect();
            JointTrajectoryMsg rosTrajectory = plan.PlannerPayload is MotionPlanResponseMsg r && r.trajectory?.joint_trajectory != null
                ? r.trajectory.joint_trajectory
                : ToRosTrajectory(plan.Trajectory);
            ros.Publish(executeTrajectoryTopic, rosTrajectory);

            bool was = IsMirroring;
            JointTrajectoryPointMsg last = rosTrajectory.points[^1];
            float duration = last.time_from_start != null ? last.time_from_start.sec + last.time_from_start.nanosec * 1e-9f : 0f;
            // Joint states that arrived before the command say nothing about its outcome.
            foreach (string joint in rosTrajectory.joint_names) reportedJoints.Remove(joint);
            following = new FollowedExecution
            {
                RosJoints = rosTrajectory.joint_names,
                Goal = last.positions,
                Deadline = Time.realtimeSinceStartup + 2f * duration + 10f,
                Settled = settled
            };
            if (!was) MirroringChanged?.Invoke(true);
            return true;
        }

        // --- gripper ----------------------------------------------------------------------

        /// <summary>
        /// True when the gripper verb can reach the real gripper: the robot's profile describes
        /// its gripper and the settings name MoveIt's trajectory execution action.
        /// </summary>
        public bool CanCommandGripper =>
            instance.Profile != null && instance.Profile.gripper != null && instance.Profile.gripper.IsSet
            && !string.IsNullOrEmpty(settings.executeTrajectoryActionName);

        /// <summary>Fires when MoveIt has finished a gripper command: open (true) or close, and whether it succeeded.</summary>
        public event Action<bool, bool> GripperMoved;

        /// <summary>
        /// Send the robot's gripper to its open or closed position (the profile's
        /// <see cref="GripperSpec"/>) through MoveIt's <c>execute_trajectory</c> action: a
        /// two-point trajectory for the gripper's driver joint, from where the ROS robot reports
        /// it to the target. MoveIt hands it to whichever controller moves that joint (a gripper
        /// action, a trajectory controller), so nothing here depends on the gripper's kind. No
        /// planning is involved, so closing on an object is not refused as a collision.
        /// A command that cannot be sent or fails is a refusal (<see cref="Refused"/>); the
        /// gripper shown in Unity has moved either way.
        /// </summary>
        public async void CommandGripper(bool open)
        {
            if (!CanCommandGripper) return;
            if (!IsOnline)
            {
                RefuseGripper(RefusalCode.PlannerUnavailable, "ROS is not connected; only the gripper shown here moved.");
                return;
            }
            Connect();
            if (gripperAction == null || gripperAction.State != RosActionClientState.Ready)
            {
                RefuseGripper(RefusalCode.PlannerUnavailable,
                    $"MoveIt of robot '{instance.Id}' offers no '{settings.executeTrajectoryActionName}' action; only the gripper shown here moved.");
                return;
            }
            // One command at a time; the latest request made meanwhile is sent when this one is done.
            if (gripperBusy)
            {
                gripperQueued = open;
                return;
            }

            gripperBusy = true;
            try
            {
                GripperSpec spec = instance.Profile.gripper;
                string joint = nameMap.ToRos(spec.driverJoint);
                double target = open ? spec.open : spec.closed;
                // MoveIt refuses a trajectory that does not start where the robot is.
                double start = reportedJoints.TryGetValue(joint, out double reported) ? reported
                             : robot != null && robot.TryGetJointAngle(spec.driverJoint, out float shown) ? shown : target;
                var trajectory = new JointTrajectoryMsg
                {
                    header = new HeaderMsg { frame_id = instance.PlanningFrameId },
                    joint_names = new[] { joint },
                    points = new[] { GripperPoint(start, 0f), GripperPoint(target, Mathf.Max(0.1f, settings.gripperMoveSeconds)) }
                };
                var goal = new ExecuteTrajectoryGoal(new RobotTrajectoryMsg { joint_trajectory = trajectory }, Array.Empty<string>());

                var response = await gripperAction.SendGoalAsync(goal);
                if (!response.Accepted)
                {
                    GripperMoved?.Invoke(open, false);
                    RefuseGripper(RefusalCode.Unspecified, "MoveIt did not accept the gripper command (it may be executing something else).");
                    return;
                }
                var result = await response.Goal.Result;
                bool ok = result.Status == RosActionGoalStatus.Succeeded && result.Result?.error_code != null
                          && result.Result.error_code.val == MoveItErrorCodesMsg.SUCCESS;
                GripperMoved?.Invoke(open, ok);
                if (!ok)
                    RefuseGripper(RefusalCode.Unspecified,
                        $"The real gripper did not {(open ? "open" : "close")}: {DescribeError(result.Result?.error_code)}.");
            }
            catch (Exception e)
            {
                GripperMoved?.Invoke(open, false);
                RefuseGripper(RefusalCode.Unspecified, $"The gripper command failed: {e.Message}");
            }
            finally
            {
                gripperBusy = false;
                bool? next = gripperQueued;
                gripperQueued = null;
                if (next.HasValue && next.Value != open) CommandGripper(next.Value);
            }
        }

        private static JointTrajectoryPointMsg GripperPoint(double position, float seconds) => new JointTrajectoryPointMsg
        {
            positions = new[] { position },
            velocities = Array.Empty<double>(),
            accelerations = Array.Empty<double>(),
            effort = Array.Empty<double>(),
            time_from_start = new DurationMsg { sec = (int)seconds, nanosec = (uint)((seconds - (int)seconds) * 1e9f) }
        };

        private void RefuseGripper(RefusalCode code, string reason)
        {
            Transform endEffector = robot != null ? robot.EndEffector : null;
            Refused?.Invoke(InteractionRefusal.Refuse(code, reason, endEffector != null ? endEffector.position : Vector3.zero, endEffector));
        }

        // --- joint names (the robot's name rule) ------------------------------------------

        public string[] ToUnityNames(string[] rosNames) => nameMap.ToLocal(rosNames);
        public string[] ToRosNames(string[] unityNames) => nameMap.ToRos(unityNames);

        public JointTrajectoryMsg ToUnityTrajectory(JointTrajectoryMsg traj) =>
            new JointTrajectoryMsg { header = traj.header, joint_names = ToUnityNames(traj.joint_names), points = traj.points };

        public JointTrajectoryMsg ToRosTrajectory(JointTrajectoryMsg traj) =>
            new JointTrajectoryMsg { header = traj.header, joint_names = ToRosNames(traj.joint_names), points = traj.points };

        private JointConstraintMsg[] JointConstraints(RobotStateMsg state)
        {
            var js = state?.joint_state;
            if (js?.name == null || js.position == null) return Array.Empty<JointConstraintMsg>();

            // The joint state also carries the gripper, which the planning group cannot move:
            // a goal that pinned the fingers would be unreachable whenever they differ from the start.
            string[] arm = robot != null ? ToRosNames(robot.JointNames.ToArray()) : Array.Empty<string>();

            int n = Math.Min(js.name.Length, js.position.Length);
            var constraints = new List<JointConstraintMsg>(n);
            for (int i = 0; i < n; i++)
            {
                if (arm.Length > 0 && Array.IndexOf(arm, js.name[i]) < 0) continue;
                constraints.Add(new JointConstraintMsg
                {
                    joint_name = js.name[i],
                    position = js.position[i],
                    tolerance_above = settings.goalTolerance,
                    tolerance_below = settings.goalTolerance,
                    weight = 1.0
                });
            }
            return constraints.ToArray();
        }

        private static TimeMsg Now()
        {
            float t = Time.time;
            return new TimeMsg { sec = (int)t, nanosec = (uint)((t - (int)t) * 1e9) };
        }
    }
}
