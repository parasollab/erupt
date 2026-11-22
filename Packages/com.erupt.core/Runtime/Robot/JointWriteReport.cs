using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>A joint that was asked to go outside its limits and was put at the limit instead.</summary>
    public readonly struct ClampedJoint
    {
        public readonly string Joint;
        public readonly float Requested;
        public readonly float Applied;

        public ClampedJoint(string joint, float requested, float applied)
        {
            Joint = joint;
            Requested = requested;
            Applied = applied;
        }

        /// <summary>How far outside the limit the request was (infinite for a value that is not a number).</summary>
        public float Excess => float.IsNaN(Requested) ? float.PositiveInfinity : Mathf.Abs(Requested - Applied);
    }

    /// <summary>
    /// What a joint-state write did besides writing: which joints were clamped to their limits
    /// (requested and applied value) and which names the robot does not have. Empty for a write
    /// that went through as asked; that case allocates nothing.
    /// </summary>
    public readonly struct JointWriteReport
    {
        private readonly List<ClampedJoint> clamped;
        private readonly List<string> unknown;

        private JointWriteReport(List<ClampedJoint> clamped, List<string> unknown)
        {
            this.clamped = clamped;
            this.unknown = unknown;
        }

        /// <summary>A write that changed nothing about what was asked.</summary>
        public static JointWriteReport Clean => default;

        /// <summary>Joints whose requested value was outside their limits.</summary>
        public IReadOnlyList<ClampedJoint> Clamped => clamped ?? (IReadOnlyList<ClampedJoint>)Array.Empty<ClampedJoint>();

        /// <summary>Names in the write that are not joints of the robot; they were ignored.</summary>
        public IReadOnlyList<string> Unknown => unknown ?? (IReadOnlyList<string>)Array.Empty<string>();

        public bool AnyClamped => clamped != null && clamped.Count > 0;
        public bool AnyUnknown => unknown != null && unknown.Count > 0;
        public bool IsClean => !AnyClamped && !AnyUnknown;

        /// <summary>Whether the named joint was clamped in this write.</summary>
        public bool WasClamped(string joint)
        {
            if (clamped == null) return false;
            foreach (ClampedJoint c in clamped)
                if (c.Joint == joint) return true;
            return false;
        }

        /// <summary>Collects a report while a joint state is written.</summary>
        public struct Builder
        {
            private List<ClampedJoint> clamped;
            private List<string> unknown;

            /// <summary>Record a joint write; kept only when the applied value differs from the requested one.</summary>
            public void Wrote(string joint, float requested, float applied)
            {
                if (requested == applied) return;
                (clamped ??= new List<ClampedJoint>()).Add(new ClampedJoint(joint, requested, applied));
            }

            public void Unknown(string joint) => (unknown ??= new List<string>()).Add(joint);

            public JointWriteReport Build() => new JointWriteReport(clamped, unknown);
        }
    }
}
