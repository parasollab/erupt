using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// Turns clamped writes of mirrored joint states into refusals. A real robot that reports a
    /// joint outside the model's limits means the model (or the profile) is wrong; the mirrored
    /// robot stays clamped and the user is told once per joint, then at most once per
    /// <see cref="RepeatSeconds"/> while it lasts, however many states arrive.
    /// </summary>
    public sealed class LimitMismatchMonitor
    {
        private readonly Dictionary<string, double> reportedAt = new Dictionary<string, double>();
        private readonly Func<double> clock;

        /// <summary>Seconds before the same joint is reported again.</summary>
        public float RepeatSeconds { get; set; } = 10f;

        /// <summary>A reported value this close to the limit is taken as sensor noise.</summary>
        public JointLimitTolerance Tolerance { get; set; } = JointLimitTolerance.Default;

        /// <param name="clock">Seconds; unscaled game time when null.</param>
        public LimitMismatchMonitor(Func<double> clock = null)
        {
            this.clock = clock ?? (() => Time.unscaledTimeAsDouble);
        }

        /// <summary>
        /// Look at what a mirrored joint-state write clamped and raise a
        /// <see cref="RefusalCode.LimitMismatch"/> refusal for each joint that is due one.
        /// </summary>
        /// <param name="robot">The robot written to; gives the limits and the link to point at.</param>
        /// <returns>How many refusals were raised.</returns>
        public int Observe(JointWriteReport report, IRobotModel robot, Action<InteractionRefusal> raise)
        {
            if (!report.AnyClamped || raise == null) return 0;

            int raised = 0;
            double now = clock();
            foreach (ClampedJoint clamped in report.Clamped)
            {
                JointLimit limit = default;
                robot?.Limits?.TryGet(clamped.Joint, out limit);
                if (clamped.Excess <= Tolerance.For(limit.Kind)) continue;
                if (reportedAt.TryGetValue(clamped.Joint, out double last) && now - last < RepeatSeconds) continue;
                reportedAt[clamped.Joint] = now;

                Transform link = null;
                robot?.TryGetJointLink(clamped.Joint, out link);
                string unit = limit.Kind == JointKind.Prismatic ? "m" : "rad";
                raise(InteractionRefusal.Refuse(
                    RefusalCode.LimitMismatch,
                    $"The robot reports {clamped.Joint} at {clamped.Requested:0.###} {unit}, outside the limits of its model " +
                    $"[{limit.Lower:0.###}, {limit.Upper:0.###}]. Check the robot profile.",
                    link != null ? link.position : Vector3.zero,
                    link));
                raised++;
            }
            return raised;
        }

        /// <summary>Forget what was reported, so the next mismatch is raised at once.</summary>
        public void Reset() => reportedAt.Clear();
    }
}
