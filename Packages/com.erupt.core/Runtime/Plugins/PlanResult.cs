using System;
using System.Collections.Generic;
using RosMessageTypes.Trajectory;
using Erupt.Robot;

namespace Erupt.Plugins
{
    /// <summary>What a planner asks for. Planner-specific knobs go in <see cref="Extra"/>.</summary>
    [Serializable]
    public class PlanPreferences
    {
        public string PipelineId;
        public string PlannerId;
        public int Attempts = 10;
        public float AllowedTimeSeconds = 5f;
        public object Extra;
    }

    /// <summary>A plan a <see cref="PlanningPlugin"/> produced; previewable and executable.</summary>
    public sealed class PlanResult
    {
        /// <summary>Joint-space trajectory in Unity joint names, or null when the planner has none.</summary>
        public JointTrajectoryMsg Trajectory;
        /// <summary>Whatever the planner needs to execute this plan (an MTC solution, a MoveIt response).</summary>
        public object PlannerPayload;
        public string PlannerId;
        /// <summary>Id of the robot this plan is for (<see cref="RobotInstance.Id"/>); set when the plan is published.</summary>
        public string RobotId;
        public string Label;
        public float PlanningTimeSeconds;

        /// <summary>
        /// The joints this plan takes outside the robot's limits (by more than the planner's
        /// tolerance), found once when the plan arrived. Empty for a plan inside the limits.
        /// A preview of such a plan plays clamped, and executing it is refused unless the
        /// planner plugin allows execution outside limits.
        /// </summary>
        public IReadOnlyList<JointLimitViolation> LimitViolations = Array.Empty<JointLimitViolation>();

        public bool HasTrajectory => Trajectory != null && Trajectory.points != null && Trajectory.points.Length > 0;
        public bool HasLimitViolations => LimitViolations != null && LimitViolations.Count > 0;
    }

    public enum ExecutionPhase
    {
        Sending,
        Executing,
        Succeeded,
        Canceled,
        Aborted,
        Failed,
        Unavailable,
        /// <summary>Not sent: the plan leaves the robot's joint limits.</summary>
        Refused
    }

    public readonly struct ExecutionStatus
    {
        public readonly ExecutionPhase Phase;
        public readonly string Message;

        public ExecutionStatus(ExecutionPhase phase, string message = null)
        {
            Phase = phase;
            Message = message ?? phase.ToString();
        }

        public bool IsTerminal => Phase is ExecutionPhase.Succeeded or ExecutionPhase.Canceled
                                        or ExecutionPhase.Aborted or ExecutionPhase.Failed or ExecutionPhase.Unavailable
                                        or ExecutionPhase.Refused;

        public override string ToString() => Message;
    }
}
