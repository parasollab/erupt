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
    /// </summary>
    public sealed class FakeRobotModel : IRobotModel
    {
        private readonly string[] names;

        public float[] Positions;
        public readonly List<(string[] names, double[] positions)> Applied = new();
        public readonly List<Vector3> SolveTargets = new();
        public int BeginCalls, EndCalls;
        public bool RefuseSolve;

        public FakeRobotModel(params string[] jointNames)
        {
            names = jointNames.Length == 0 ? new[] { "j1", "j2" } : jointNames;
            Positions = new float[names.Length];
        }

        public Transform Root { get; set; }
        public Transform EndEffector { get; set; }
        public IReadOnlyList<string> JointNames => names;

        public bool TryGetJointAngle(string jointName, out float positionRadians)
        {
            int i = Array.IndexOf(names, jointName);
            positionRadians = i >= 0 ? Positions[i] : 0f;
            return i >= 0;
        }

        public string[] GetJointStateNames() => (string[])names.Clone();
        public float[] GetJointStatePositions() => (float[])Positions.Clone();

        public void ApplyJointState(string[] n, double[] p)
        {
            Applied.Add((n, p));
            for (int i = 0; i < Math.Min(n.Length, p.Length); i++)
            {
                int j = Array.IndexOf(names, n[i]);
                if (j >= 0) Positions[j] = (float)p[i];
            }
        }

        public void ApplyJointState(IList<string> n, IList<float> p)
        {
            var nn = new string[n.Count];
            var pp = new double[p.Count];
            n.CopyTo(nn, 0);
            for (int i = 0; i < p.Count; i++) pp[i] = p[i];
            ApplyJointState(nn, pp);
        }

        public InteractionRefusal TrySolveToTarget(Vector3 targetPosition)
        {
            SolveTargets.Add(targetPosition);
            return RefuseSolve ? InteractionRefusal.Refuse("out of reach", targetPosition) : InteractionRefusal.None;
        }

        public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians) => InteractionRefusal.None;
        public Transform FindLinkTransform(string linkName) => EndEffector;
        public void BeginInteraction() => BeginCalls++;
        public void EndInteraction() => EndCalls++;
    }
}
