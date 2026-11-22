using System;
using System.Linq;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using RosMessageTypes.Moveit;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// What the MoveIt plugin keeps for one robot: the planning client on that robot's
    /// namespaced bus, the captured start and goal states, whether a preview is playing on its
    /// trajectory player, its ghosts, and its planning-scene endpoint. Created when the robot
    /// joins the registry and disposed when it leaves.
    /// </summary>
    public sealed class MoveItRobotSession : IDisposable
    {
        private readonly MoveItPlugin plugin;
        private RobotStateMsg startState;
        private RobotStateMsg goalState;
        private bool plannersKnown;

        public RobotInstance Robot { get; }
        public MoveItPlanningClient Client { get; }
        /// <summary>This robot's planning scene: where Unity's environment is sent for it and heard back from.</summary>
        public PlanningSceneEndpoint Scene { get; internal set; }
        /// <summary>Mirrors what this robot's MoveIt attaches to its links; null when the scene has no attach mirror.</summary>
        public AttachedCollisionObjectListener AttachListener { get; internal set; }
        // The object holding this robot's own copy of the attach mirror; null when it uses one that is in the scene.
        internal GameObject attachHost;

        public bool HasStart => startState != null;
        public bool HasGoal => goalState != null;
        public bool IsPreviewing { get; private set; }
        /// <summary>The plan whose preview is playing; null while none is.</summary>
        public PlanResult PreviewedPlan { get; private set; }
        /// <summary>Raised when a preview starts or stops.</summary>
        public event System.Action PreviewChanged;
        // Where the running preview plays: on the robot's blue copy, or on the robot itself
        // (a robot with no ghost prefab).
        private bool previewOnGhost;
        public RobotStateMsg StartState => startState;
        public RobotStateMsg GoalState => goalState;

        private JointTrajectoryPlayer Player => Robot != null ? Robot.TrajectoryPlayer : null;
        private RobotGhosts Ghosts => Robot != null ? Robot.Ghosts : null;

        internal MoveItRobotSession(MoveItPlugin plugin, RobotInstance robot, MoveItPlanningSettings settings)
        {
            this.plugin = plugin;
            Robot = robot;
            Client = new MoveItPlanningClient(robot, settings);
            // The gripper verb belongs to core and moves the robot model's fingers; the real
            // gripper is reached through this robot's MoveIt.
            gripper = robot.Gripper;
            if (gripper != null) gripper.GripperCommanded += Client.CommandGripper;
        }

        private readonly IRobotGripper gripper;

        public void Dispose()
        {
            if (gripper != null) gripper.GripperCommanded -= Client.CommandGripper;
            StopPreview();
            Client.Dispose();
            // The ghosts stand in the scene on their own and would outlive the robot.
            RobotGhosts ghosts = Ghosts;
            if (ghosts != null) ghosts.ClearGhosts();
        }

        // How long MoveIt may take to answer the first planner query before it counts as having
        // come up late (after the app, or after the endpoint).
        private const float LateSeconds = 3f;
        private float askingSince = -1f;

        /// <summary>
        /// Ask MoveIt for its planners until it has answered once. An answer that was long in
        /// coming means this robot's move_group came up after the app could first have reached
        /// it: whatever the app sent to its planning scene before that went nowhere, so the
        /// plugin is told (<see cref="MoveItPlugin.MoveItCameUpLate"/>) and sends it again.
        /// </summary>
        internal void TryQueryPlanners()
        {
            if (plannersKnown) return;
            if (!Client.IsOnline) return;
            if (askingSince < 0f) askingSince = Time.realtimeSinceStartup;
            Client.QueryPlanners(_ =>
            {
                if (plannersKnown) return;
                plannersKnown = true;
                bool late = askingSince >= 0f && Time.realtimeSinceStartup - askingSince > LateSeconds;
                askingSince = -1f;
                if (late) plugin.MoveItCameUpLate(this);
            });
        }

        /// <summary>The connection to ROS is back: what was in flight is lost, and the planners are asked for again.</summary>
        internal void ConnectionRestored()
        {
            plannersKnown = false;
            askingSince = -1f;
            Client.ConnectionRestored();
        }

        /// <summary>Capture the robot's current pose as the plan's start state.</summary>
        public void SetStartFromRobot()
        {
            startState = Client.CaptureRobotState();
            RobotGhosts ghosts = Ghosts;
            if (ghosts != null) { if (ghosts.StartGhost == null) ghosts.SpawnStartGhost(); else ghosts.UpdateStartGhost(); }
        }

        /// <summary>Capture the robot's current pose as the goal (the user has posed it with the IK handle).</summary>
        public void SetGoalFromRobot()
        {
            goalState = Client.CaptureRobotState();
            RobotGhosts ghosts = Ghosts;
            if (ghosts != null) { if (ghosts.GoalGhost == null) ghosts.SpawnGoalGhost(); else ghosts.UpdateGoalGhost(); }
        }

        public void ResetPlanningState()
        {
            // The preview shows a plan between a start and a goal that are about to go.
            StopPreview();
            startState = null;
            goalState = null;
            RobotGhosts ghosts = Ghosts;
            if (ghosts != null) ghosts.ClearGhosts();
        }

        /// <summary>
        /// Play a plan, in a loop until <see cref="StopPreview"/>, on a blue copy of the robot;
        /// the robot itself stays where it is. A robot with no ghost prefab has no copy: the
        /// plan then plays on the robot, which gets its pose back when the preview stops.
        /// False when the robot can show neither.
        /// </summary>
        public bool Preview(PlanResult plan)
        {
            if (plan == null || !plan.HasTrajectory) return false;
            StopPreview();

            RobotGhosts ghosts = Ghosts;
            if (ghosts != null && ghosts.PlayPreview(plan.Trajectory))
            {
                previewOnGhost = true;
            }
            else
            {
                JointTrajectoryPlayer player = Player;
                if (player == null) return false;
                previewOnGhost = false;
                player.RestartReplay(plan.Trajectory);
            }
            IsPreviewing = true;
            PreviewedPlan = plan;
            PreviewChanged?.Invoke();
            return true;
        }

        public void StopPreview()
        {
            if (!IsPreviewing) return;
            IsPreviewing = false;
            PreviewedPlan = null;
            if (previewOnGhost)
            {
                RobotGhosts ghosts = Ghosts;
                if (ghosts != null) ghosts.StopPreview();
            }
            else
            {
                JointTrajectoryPlayer player = Player;
                if (player != null) player.StopReplay();
            }
            PreviewChanged?.Invoke();
        }

        // A handle closer than this to the goal's end effector is lifted clear of it.
        private const float HandleClearance = 0.15f;

        /// <summary>
        /// Where a plan's handle goes: on the way from the start to the goal, halfway between
        /// the end effectors of the two ghosts. Not at the goal itself: the robot stands there
        /// while the plan is previewed on its copy, and the handle would sit inside the robot's
        /// own end-effector handle, where it cannot be pointed at. When start and goal are too
        /// close for that, or there is no start ghost, it floats above the goal's end effector.
        /// It follows the goal ghost; the real end effector when there is no ghost.
        /// </summary>
        internal void HandleAnchor(Vector3 fallback, out Vector3 position, out Transform anchor)
        {
            Transform ee = Robot != null ? Robot.Model?.EndEffector : null;
            anchor = null;
            RobotGhosts ghosts = Ghosts;
            Transform start = null;
            if (ghosts != null && ee != null)
            {
                anchor = EndEffectorOf(ghosts.GoalGhost, ee.name);
                start = EndEffectorOf(ghosts.StartGhost, ee.name);
            }
            Vector3 goal = anchor != null ? anchor.position : ee != null ? ee.position : fallback;
            if (anchor == null && ee == null) { position = goal; return; }

            position = start != null ? Vector3.Lerp(start.position, goal, 0.5f) : goal;
            if (Vector3.Distance(position, goal) < HandleClearance)
                position = goal + Vector3.up * HandleClearance;
        }

        private static Transform EndEffectorOf(GameObject ghost, string endEffectorName)
        {
            if (ghost == null) return null;
            Transform match = ghost.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == endEffectorName);
            return match != null ? match : ghost.transform;
        }
    }
}
