using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// The single place a joint position is written to an articulation. Every write goes
    /// through the joint's <see cref="JointLimit"/>: the robot controller and the ghost robots
    /// both call this, so nothing can put a joint outside its limits.
    /// </summary>
    /// <remarks>
    /// Joints are teleported, and PhysX does not apply its own joint limits to a teleport, so
    /// this clamp is what enforces them. <c>Erupt.Boundary.Tests</c> and CI check that no
    /// other runtime file assigns <c>jointPosition</c>.
    /// </remarks>
    public static class JointWriter
    {
        /// <summary>
        /// Put a one-degree-of-freedom joint at <paramref name="position"/> (radians, or metres
        /// for a prismatic joint), clamped to <paramref name="limit"/>. A value that is not a
        /// finite number is not written and the joint stays where it is.
        /// </summary>
        /// <param name="sync">Publish the transform change to physics queries now. Pass false when writing many joints and sync once afterwards.</param>
        /// <returns>The position the joint was put at.</returns>
        public static float ApplyJointPosition(ArticulationBody joint, float position, in JointLimit limit, bool sync = true)
        {
            if (float.IsNaN(position) || float.IsInfinity(position))
                return joint.dofCount > 0 ? joint.jointPosition[0] : 0f;

            float applied = limit.Clamp(position);
            joint.jointPosition = new ArticulationReducedSpace(applied);

            ArticulationDrive drive = joint.xDrive;
            drive.target = joint.jointType == ArticulationJointType.RevoluteJoint ? applied * Mathf.Rad2Deg : applied;
            joint.xDrive = drive;

            if (joint.dofCount == 1) joint.jointVelocity = new ArticulationReducedSpace(0f);
            joint.PublishTransform();
            if (sync) Physics.SyncTransforms();
            return applied;
        }
    }
}
