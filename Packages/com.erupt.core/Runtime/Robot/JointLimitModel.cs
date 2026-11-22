using System;
using System.Collections.Generic;
using RosMessageTypes.Trajectory;
using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>How far past a limit a value may be before it counts as a violation: one amount for angles, one for lengths.</summary>
    [Serializable]
    public struct JointLimitTolerance
    {
        public const float DefaultRadians = 1e-3f;
        public const float DefaultMetres = 1e-4f;

        [Tooltip("For revolute joints, in radians.")]
        public float radians;
        [Tooltip("For prismatic joints, in metres.")]
        public float metres;

        public JointLimitTolerance(float radians, float metres)
        {
            this.radians = radians;
            this.metres = metres;
        }

        /// <summary>1e-3 rad and 1e-4 m: planner rounding passes, a real excursion does not.</summary>
        public static JointLimitTolerance Default => new JointLimitTolerance(DefaultRadians, DefaultMetres);

        public float For(JointKind kind) => kind == JointKind.Prismatic ? metres : radians;
    }

    /// <summary>
    /// The limits of one joint: positions in radians, or metres for a prismatic joint. The
    /// default value is a joint without limits.
    /// </summary>
    public readonly struct JointLimit
    {
        public readonly string Name;
        public readonly JointKind Kind;
        /// <summary>False for a continuous joint and for one without a limit range: its position is never clamped.</summary>
        public readonly bool HasLimits;
        public readonly float Lower;
        public readonly float Upper;
        /// <summary>rad/s or m/s; 0 = not limited.</summary>
        public readonly float MaxVelocity;

        /// <param name="lower">Lower limit before the margin.</param>
        /// <param name="upper">Upper limit before the margin.</param>
        /// <param name="margin">Taken off both ends. A margin wider than half the range leaves the middle of it.</param>
        public JointLimit(string name, JointKind kind, float lower, float upper, float maxVelocity = 0f, float margin = 0f)
        {
            Name = name ?? "";
            Kind = kind;
            MaxVelocity = Mathf.Max(0f, maxVelocity);
            HasLimits = kind != JointKind.Continuous && upper > lower;
            if (!HasLimits)
            {
                Lower = 0f;
                Upper = 0f;
                return;
            }

            margin = Mathf.Max(0f, margin);
            if (2f * margin >= upper - lower)
            {
                Lower = Upper = (lower + upper) * 0.5f;
            }
            else
            {
                Lower = lower + margin;
                Upper = upper - margin;
            }
        }

        /// <summary>A joint whose position is never clamped.</summary>
        public static JointLimit Unlimited(string name, JointKind kind = JointKind.Continuous, float maxVelocity = 0f) =>
            new JointLimit(name, kind, 0f, 0f, maxVelocity);

        /// <summary>The position inside the limits; unchanged for a joint without limits (a continuous joint is not wrapped).</summary>
        public float Clamp(float position) => HasLimits ? Mathf.Clamp(position, Lower, Upper) : position;

        /// <summary>How far outside the limits a position is: positive above the upper limit, negative below the lower one, 0 inside.</summary>
        public double Excess(double position)
        {
            if (!HasLimits) return 0.0;
            // Not a number is outside any range.
            if (double.IsNaN(position)) return double.PositiveInfinity;
            if (position > Upper) return position - Upper;
            if (position < Lower) return position - Lower;
            return 0.0;
        }

        /// <summary>Whether the position is at (or past) the upper limit, within <paramref name="epsilon"/>.</summary>
        public bool IsAtUpper(float position, float epsilon = 1e-4f) => HasLimits && position >= Upper - epsilon;

        /// <summary>Whether the position is at (or past) the lower limit, within <paramref name="epsilon"/>.</summary>
        public bool IsAtLower(float position, float epsilon = 1e-4f) => HasLimits && position <= Lower + epsilon;

        /// <summary>
        /// Where the joint may be after <paramref name="deltaTime"/> seconds on its way from
        /// <paramref name="current"/> to <paramref name="target"/>: at most
        /// <see cref="MaxVelocity"/> × <paramref name="deltaTime"/> away from where it is, and
        /// inside the position limits. A joint without a velocity limit goes straight to the
        /// (clamped) target.
        /// </summary>
        public float StepToward(float current, float target, float deltaTime)
        {
            target = Clamp(target);
            if (MaxVelocity <= 0f) return target;
            return Clamp(Mathf.MoveTowards(current, target, MaxVelocity * Mathf.Max(0f, deltaTime)));
        }
    }

    /// <summary>
    /// One joint leaving its limits in a joint state or a trajectory: the worst point, and how
    /// many points are outside.
    /// </summary>
    public readonly struct JointLimitViolation
    {
        public readonly string Joint;
        public readonly JointKind Kind;
        /// <summary>Which trajectory, when several were checked as one (the steps of a solution); 0 otherwise.</summary>
        public readonly int Segment;
        /// <summary>Index of the worst point; 0 for a single joint state.</summary>
        public readonly int Point;
        /// <summary>The value at the worst point.</summary>
        public readonly double Value;
        /// <summary>The limit that value is past.</summary>
        public readonly float Bound;
        /// <summary>How far past the limit the worst point is (positive).</summary>
        public readonly float Excess;
        /// <summary>How many points are past the limit by more than the tolerance.</summary>
        public readonly int Count;

        public JointLimitViolation(string joint, JointKind kind, int segment, int point, double value, float bound, float excess, int count)
        {
            Joint = joint;
            Kind = kind;
            Segment = segment;
            Point = point;
            Value = value;
            Bound = bound;
            Excess = excess;
            Count = count;
        }

        public bool AboveUpper => Value > Bound;
        public string Unit => Kind == JointKind.Prismatic ? "m" : "rad";

        /// <summary>The same violation, as part of trajectory number <paramref name="segment"/>.</summary>
        public JointLimitViolation InSegment(int segment) =>
            new JointLimitViolation(Joint, Kind, segment, Point, Value, Bound, Excess, Count);

        /// <summary>The same violation, counting <paramref name="count"/> points.</summary>
        public JointLimitViolation WithCount(int count) =>
            new JointLimitViolation(Joint, Kind, Segment, Point, Value, Bound, Excess, count);

        /// <summary>For the user: "fr3_joint4 goes 0.2 rad past its upper limit".</summary>
        public override string ToString() =>
            $"{Joint} goes {Excess:0.###} {Unit} past its {(AboveUpper ? "upper" : "lower")} limit";

        /// <summary>The violation with the largest excess, or null for an empty list.</summary>
        public static JointLimitViolation? Worst(IReadOnlyList<JointLimitViolation> violations)
        {
            if (violations == null || violations.Count == 0) return null;
            JointLimitViolation worst = violations[0];
            for (int i = 1; i < violations.Count; i++)
                if (violations[i].Excess > worst.Excess) worst = violations[i];
            return worst;
        }

        /// <summary>
        /// Add <paramref name="more"/> to <paramref name="into"/>, keeping one entry per joint:
        /// the worse of the two, counting the points of both.
        /// </summary>
        public static void Merge(List<JointLimitViolation> into, IReadOnlyList<JointLimitViolation> more)
        {
            if (into == null || more == null) return;
            foreach (JointLimitViolation violation in more)
            {
                int existing = into.FindIndex(v => v.Joint == violation.Joint);
                if (existing < 0)
                {
                    into.Add(violation);
                    continue;
                }
                int count = into[existing].Count + violation.Count;
                JointLimitViolation worse = violation.Excess > into[existing].Excess ? violation : into[existing];
                into[existing] = worse.WithCount(count);
            }
        }

        /// <summary>For the user: the worst violation, and how many other joints are out as well.</summary>
        public static string Describe(IReadOnlyList<JointLimitViolation> violations)
        {
            JointLimitViolation? worst = Worst(violations);
            if (worst == null) return "";
            return violations.Count == 1
                ? worst.Value.ToString()
                : $"{worst.Value} ({violations.Count - 1} more joint{(violations.Count == 2 ? "" : "s")} outside limits)";
        }
    }

    /// <summary>The joint limits of one robot, as everything that moves or checks its joints sees them.</summary>
    public interface IJointLimits
    {
        /// <summary>Every joint the model knows, limited or not.</summary>
        IReadOnlyList<JointLimit> Joints { get; }

        /// <summary>
        /// Whether motion the user drives directly (IK dragging, jogging) is held to each joint's
        /// <see cref="JointLimit.MaxVelocity"/>. Trajectory playback and state mirroring never are.
        /// </summary>
        bool LimitUserVelocity { get; }

        bool TryGet(string jointName, out JointLimit limit);

        /// <summary>The position inside the joint's limits. Unchanged for a joint without limits and for a joint the model does not know.</summary>
        float Clamp(string jointName, float position);

        /// <summary><see cref="JointLimit.StepToward"/> for a joint by name; the target itself for a joint the model does not know.</summary>
        float StepToward(string jointName, float current, float target, float deltaTime);

        /// <summary>
        /// The joints of one joint state that are outside their limits by more than the
        /// tolerance. Joints the model does not know are skipped.
        /// </summary>
        List<JointLimitViolation> Validate(IList<string> names, IList<double> positions, JointLimitTolerance tolerance);

        /// <summary>
        /// The joints that leave their limits by more than the tolerance anywhere in a
        /// trajectory: one entry per joint, describing its worst point.
        /// </summary>
        List<JointLimitViolation> Validate(JointTrajectoryMsg trajectory, JointLimitTolerance tolerance);
    }

    /// <summary>
    /// The one description of a robot's joint limits: per joint whether it has position limits,
    /// the limits after overrides and the safety margin, the velocity limit and the kind. Built
    /// from a <see cref="RobotProfile"/>, or from a list of joints for a robot without one.
    /// Every joint write goes through it (<see cref="JointWriter"/>), and incoming joint states
    /// and trajectories are checked against it.
    /// </summary>
    public sealed class JointLimitModel : IJointLimits
    {
        /// <summary>A model that knows no joint: nothing is clamped.</summary>
        public static readonly JointLimitModel Unlimited = new JointLimitModel(Array.Empty<JointLimit>());

        private readonly List<JointLimit> joints = new List<JointLimit>();
        private readonly Dictionary<string, int> indexByName = new Dictionary<string, int>();

        public IReadOnlyList<JointLimit> Joints => joints;
        public bool LimitUserVelocity { get; }

        /// <param name="joints">A name listed twice keeps its first entry.</param>
        public JointLimitModel(IEnumerable<JointLimit> joints, bool limitUserVelocity = false)
        {
            LimitUserVelocity = limitUserVelocity;
            if (joints == null) return;
            foreach (JointLimit joint in joints)
            {
                if (string.IsNullOrEmpty(joint.Name) || indexByName.ContainsKey(joint.Name)) continue;
                indexByName[joint.Name] = this.joints.Count;
                this.joints.Add(joint);
            }
        }

        /// <summary>
        /// The limits a profile describes: each joint's URDF limits or its overrides, shrunk by
        /// the profile's margin (radians for revolute joints, metres for prismatic ones), with
        /// its velocity limit. A null profile gives <see cref="Unlimited"/>.
        /// </summary>
        public static JointLimitModel FromProfile(RobotProfile profile)
        {
            if (profile == null || profile.joints == null) return Unlimited;
            var limits = new List<JointLimit>(profile.joints.Length);
            foreach (JointSpec joint in profile.joints)
            {
                if (joint == null || string.IsNullOrEmpty(joint.name)) continue;
                float margin = joint.kind == JointKind.Prismatic ? profile.limitMarginMetres : profile.limitMarginRadians;
                limits.Add(new JointLimit(joint.name, joint.kind, joint.Lower, joint.Upper, joint.maxVelocity, margin));
            }
            return new JointLimitModel(limits, profile.limitUserVelocity);
        }

        public bool TryGet(string jointName, out JointLimit limit)
        {
            if (!string.IsNullOrEmpty(jointName) && indexByName.TryGetValue(jointName, out int index))
            {
                limit = joints[index];
                return true;
            }
            limit = default;
            return false;
        }

        public float Clamp(string jointName, float position) =>
            TryGet(jointName, out JointLimit limit) ? limit.Clamp(position) : position;

        public float StepToward(string jointName, float current, float target, float deltaTime) =>
            TryGet(jointName, out JointLimit limit) ? limit.StepToward(current, target, deltaTime) : target;

        public List<JointLimitViolation> Validate(IList<string> names, IList<double> positions, JointLimitTolerance tolerance)
        {
            var violations = new List<JointLimitViolation>();
            if (names == null || positions == null) return violations;
            int count = Math.Min(names.Count, positions.Count);
            for (int i = 0; i < count; i++)
            {
                if (!TryGet(names[i], out JointLimit limit)) continue;
                double excess = limit.Excess(positions[i]);
                if (Math.Abs(excess) <= tolerance.For(limit.Kind)) continue;
                violations.Add(new JointLimitViolation(limit.Name, limit.Kind, 0, 0, positions[i],
                    excess > 0 ? limit.Upper : limit.Lower, (float)Math.Abs(excess), 1));
            }
            return violations;
        }

        public List<JointLimitViolation> Validate(JointTrajectoryMsg trajectory, JointLimitTolerance tolerance)
        {
            var violations = new List<JointLimitViolation>();
            string[] names = trajectory?.joint_names;
            JointTrajectoryPointMsg[] points = trajectory?.points;
            if (names == null || points == null) return violations;

            for (int j = 0; j < names.Length; j++)
            {
                if (!TryGet(names[j], out JointLimit limit) || !limit.HasLimits) continue;

                int outside = 0, worstPoint = -1;
                double worstExcess = 0.0, worstValue = 0.0;
                for (int p = 0; p < points.Length; p++)
                {
                    double[] positions = points[p]?.positions;
                    if (positions == null || j >= positions.Length) continue;
                    double excess = limit.Excess(positions[j]);
                    if (Math.Abs(excess) <= tolerance.For(limit.Kind)) continue;
                    outside++;
                    if (Math.Abs(excess) > Math.Abs(worstExcess))
                    {
                        worstExcess = excess;
                        worstValue = positions[j];
                        worstPoint = p;
                    }
                }

                if (outside > 0)
                    violations.Add(new JointLimitViolation(limit.Name, limit.Kind, 0, worstPoint, worstValue,
                        worstExcess > 0 ? limit.Upper : limit.Lower, (float)Math.Abs(worstExcess), outside));
            }
            return violations;
        }
    }
}
