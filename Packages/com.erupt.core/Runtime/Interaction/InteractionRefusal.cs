using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// What kind of refusal this is, for code that reacts to one (a tint on the joint, a
    /// haptic pulse) without parsing the user-facing text.
    /// </summary>
    public enum RefusalCode
    {
        /// <summary>Not a refusal.</summary>
        None = 0,
        /// <summary>A refusal that says only what its reason text says.</summary>
        Unspecified,
        /// <summary>A joint is at one of its position limits and cannot go further.</summary>
        JointLimit,
        /// <summary>The target cannot be reached, with no joint at a limit.</summary>
        OutOfReach,
        /// <summary>The real robot reported a joint outside the limits of the robot model.</summary>
        LimitMismatch,
        /// <summary>A plan, solution or demonstration leaves the robot's joint limits.</summary>
        PlanOutsideLimits,
        /// <summary>The object cannot be driven this way (not part of the controlled chain, robot not configured).</summary>
        NotControllable,
        /// <summary>A verb was invoked with nothing selected.</summary>
        NothingSelected,
        /// <summary>The verb does not exist for the selection, or nothing implements it.</summary>
        VerbUnavailable,
        /// <summary>The planner cannot take the request (not connected, or it does not plan this way).</summary>
        PlannerUnavailable,
        /// <summary>The planner took the request and found no plan (start or goal in collision, no path, timed out).</summary>
        PlanFailed,
        /// <summary>The robot's base cannot be put there now (nothing says where, or the robot is busy).</summary>
        BaseNotPlaced,
        /// <summary>A plan was not executed, or its execution did not finish (not connected, nothing to send, the robot did not arrive).</summary>
        ExecutionFailed
    }

    /// <summary>
    /// Why an interaction could not proceed. Guidelines Part 3, "Refusals carry reasons":
    /// silent no-ops are the most common source of novice confusion, so every refusal
    /// carries a user-facing string and the world point to surface it at.
    /// </summary>
    public readonly struct InteractionRefusal
    {
        public readonly bool IsRefused;
        public readonly string Reason;
        public readonly Vector3 WorldPoint;

        /// <summary>The machine-readable kind; <see cref="RefusalCode.None"/> when nothing was refused.</summary>
        public readonly RefusalCode Code;

        /// <summary>What the refusal is about (the link a joint moves, for a joint limit), or null.</summary>
        public readonly Transform Subject;

        /// <summary>
        /// The input source whose action was refused (<see cref="InteractionSample.SourceId"/>),
        /// or null when no single source caused it.
        /// </summary>
        public readonly string SourceId;

        private InteractionRefusal(bool isRefused, RefusalCode code, string reason, Vector3 worldPoint,
                                   Transform subject, string sourceId)
        {
            IsRefused = isRefused;
            Code = code;
            Reason = reason;
            WorldPoint = worldPoint;
            Subject = subject;
            SourceId = sourceId;
        }

        public static readonly InteractionRefusal None =
            new InteractionRefusal(false, RefusalCode.None, null, Vector3.zero, null, null);

        public static InteractionRefusal Refuse(string reason, Vector3 worldPoint) =>
            new InteractionRefusal(true, RefusalCode.Unspecified, reason, worldPoint, null, null);

        public static InteractionRefusal Refuse(RefusalCode code, string reason, Vector3 worldPoint, Transform subject = null) =>
            new InteractionRefusal(true, code == RefusalCode.None ? RefusalCode.Unspecified : code, reason, worldPoint, subject, null);

        /// <summary>The same refusal, attributed to the input source that caused it.</summary>
        public InteractionRefusal From(string sourceId) =>
            new InteractionRefusal(IsRefused, Code, Reason, WorldPoint, Subject, sourceId);
    }
}
