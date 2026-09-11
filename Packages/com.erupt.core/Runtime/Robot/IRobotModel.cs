using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// The robot as plugins see it: joints, end effector, IK, and the joint-state
    /// surface planners read and write. Extracted from <see cref="DirectArticulationIKController"/>'s
    /// public API so a plugin never depends on the concrete controller.
    /// </summary>
    public interface IRobotModel
    {
        /// <summary>Root of the articulated hierarchy.</summary>
        Transform Root { get; }
        Transform EndEffector { get; }
        IReadOnlyList<string> JointNames { get; }

        bool TryGetJointAngle(string jointName, out float positionRadians);
        string[] GetJointStateNames();
        float[] GetJointStatePositions();
        void ApplyJointState(string[] names, double[] positions);
        void ApplyJointState(IList<string> names, IList<float> positions);

        InteractionRefusal TrySolveToTarget(Vector3 targetPosition);
        InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians);

        /// <summary>Link transform by URDF link name; the end effector when the name is empty.</summary>
        Transform FindLinkTransform(string linkName);

        void BeginInteraction();
        void EndInteraction();
    }
}
