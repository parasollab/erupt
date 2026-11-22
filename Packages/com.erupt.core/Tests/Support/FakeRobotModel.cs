using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Robot;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// In-memory robot: fixed joint names, settable positions, and a record of every
    /// joint state and IK target it was handed. Lets plugin tests run without a URDF.
    /// Give it joint limits with <see cref="WithLimits"/>: writes are then clamped and
    /// reported as on the real controller.
    /// </summary>
    public sealed class FakeRobotModel : IRobotModel, IRobotGripper
    {
        private readonly string[] names;
        // names followed by any gripper joints: what the joint state carries.
        private string[] stateNames;

        public float[] Positions;
        public readonly List<(string[] names, double[] positions)> Applied = new();
        public readonly List<Vector3> SolveTargets = new();
        public int BeginCalls, EndCalls;
        public bool RefuseSolve;

        public FakeRobotModel(params string[] jointNames)
        {
            names = jointNames.Length == 0 ? new[] { "j1", "j2" } : jointNames;
            stateNames = names;
            Positions = new float[names.Length];
        }

        /// <summary>
        /// Adds gripper joints: part of the joint state (after the arm joints) but not of
        /// <see cref="JointNames"/>, as on the real controller.
        /// </summary>
        public FakeRobotModel WithGripper(params string[] gripperJointNames)
        {
            stateNames = new string[names.Length + gripperJointNames.Length];
            names.CopyTo(stateNames, 0);
            gripperJointNames.CopyTo(stateNames, names.Length);
            Array.Resize(ref Positions, stateNames.Length);
            HasGripper = gripperJointNames.Length > 0;
            return this;
        }

        /// <summary>True once <see cref="WithGripper"/> gave the robot gripper joints.</summary>
        public bool HasGripper { get; private set; }
        /// <summary>How often the gripper was toggled.</summary>
        public int GripperToggles;
        /// <summary>Whether the fake gripper is open; it starts open and every toggle flips it.</summary>
        public bool GripperOpen { get; private set; } = true;
        public event System.Action<bool> GripperCommanded;

        public void ToggleGripper()
        {
            GripperToggles++;
            GripperOpen = !GripperOpen;
            GripperCommanded?.Invoke(GripperOpen);
        }

        /// <summary>Joint limits; none unless <see cref="WithLimits"/> set some.</summary>
        public IJointLimits Limits { get; set; } = JointLimitModel.Unlimited;

        /// <summary>Gives the robot joint limits; writes outside them are clamped.</summary>
        public FakeRobotModel WithLimits(params JointLimit[] limits)
        {
            Limits = new JointLimitModel(limits);
            return this;
        }

        /// <summary>The link each joint moves, by joint name, for refusals that point at a joint.</summary>
        public readonly Dictionary<string, Transform> JointLinks = new();

        public bool TryGetJointLink(string jointName, out Transform link) =>
            JointLinks.TryGetValue(jointName ?? "", out link) && link != null;

        public Transform Root { get; set; }
        public Transform EndEffector { get; set; }
        /// <summary>Links by URDF name; a name not listed here is searched under <see cref="Root"/>.</summary>
        public readonly Dictionary<string, Transform> Links = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> JointNames => names;

        public bool TryGetJointAngle(string jointName, out float positionRadians)
        {
            int i = Array.IndexOf(stateNames, jointName);
            positionRadians = i >= 0 ? Positions[i] : 0f;
            return i >= 0;
        }

        public string[] GetJointStateNames() => (string[])stateNames.Clone();
        public float[] GetJointStatePositions() => (float[])Positions.Clone();

        /// <summary><see cref="Applied"/> records the request as it came in; <see cref="Positions"/> holds what the limits let through.</summary>
        public JointWriteReport ApplyJointState(string[] n, double[] p)
        {
            Applied.Add((n, p));
            var report = new JointWriteReport.Builder();
            for (int i = 0; i < Math.Min(n.Length, p.Length); i++)
            {
                int j = Array.IndexOf(stateNames, n[i]);
                if (j < 0)
                {
                    report.Unknown(n[i]);
                    continue;
                }
                float requested = (float)p[i];
                Positions[j] = Limits.Clamp(n[i], requested);
                report.Wrote(n[i], requested, Positions[j]);
            }
            return report.Build();
        }

        public JointWriteReport ApplyJointState(IList<string> n, IList<float> p)
        {
            var nn = new string[n.Count];
            var pp = new double[p.Count];
            n.CopyTo(nn, 0);
            for (int i = 0; i < p.Count; i++) pp[i] = p[i];
            return ApplyJointState(nn, pp);
        }

        public InteractionRefusal TrySolveToTarget(Vector3 targetPosition)
        {
            SolveTargets.Add(targetPosition);
            return RefuseSolve ? InteractionRefusal.Refuse("out of reach", targetPosition) : InteractionRefusal.None;
        }

        public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians) => InteractionRefusal.None;
        public Transform FindLinkTransform(string linkName) =>
            TryFindLinkTransform(linkName, out var link) ? link : EndEffector;

        public bool TryFindLinkTransform(string linkName, out Transform link)
        {
            link = null;
            if (string.IsNullOrEmpty(linkName)) return false;
            if (Links.TryGetValue(linkName, out link) && link != null) return true;
            if (Root == null) return false;
            foreach (Transform t in Root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, linkName, StringComparison.OrdinalIgnoreCase)) { link = t; return true; }
            return false;
        }
        public event System.Action ControlTaken;
        /// <summary>How often control of the pose was taken (interactions and playbacks).</summary>
        public int ControlTakenCount;
        public void TakeControl()
        {
            ControlTakenCount++;
            ControlTaken?.Invoke();
        }

        public void BeginInteraction()
        {
            BeginCalls++;
            TakeControl();
        }
        public void EndInteraction() => EndCalls++;
    }
}
