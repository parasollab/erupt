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
        /// <summary>The joints IK drives (the arm), base to tip. Gripper joints are not in this list.</summary>
        IReadOnlyList<string> JointNames { get; }

        bool TryGetJointAngle(string jointName, out float positionRadians);
        /// <summary>Every joint in the joint state: <see cref="JointNames"/>, then any gripper joints.</summary>
        string[] GetJointStateNames();
        /// <summary>Positions matching <see cref="GetJointStateNames"/>: radians, or metres for a prismatic gripper joint.</summary>
        float[] GetJointStatePositions();

        /// <summary>
        /// The robot's joint limits. Every joint write is clamped to them; plans and joint
        /// states from outside are checked against them. Never null.
        /// </summary>
        IJointLimits Limits { get; }

        /// <summary>
        /// Put the named joints at the given positions (radians, or metres for a prismatic
        /// joint). Each value is clamped to the joint's limits; the report lists the joints
        /// that were clamped and the names the robot does not have.
        /// </summary>
        JointWriteReport ApplyJointState(string[] names, double[] positions);
        /// <inheritdoc cref="ApplyJointState(string[], double[])"/>
        JointWriteReport ApplyJointState(IList<string> names, IList<float> positions);

        /// <summary>The link a joint moves (the object its limit feedback points at). False for a joint the robot does not have.</summary>
        bool TryGetJointLink(string jointName, out Transform link);

        /// <summary>
        /// Move the end effector toward a world position, inside the joint limits. A solve that
        /// falls short is refused with <see cref="RefusalCode.JointLimit"/> (the joint named, its
        /// link as the subject) when a joint is at a limit, and with
        /// <see cref="RefusalCode.OutOfReach"/> otherwise.
        /// </summary>
        InteractionRefusal TrySolveToTarget(Vector3 targetPosition);
        /// <summary>Turn one joint by a step. Refused with <see cref="RefusalCode.JointLimit"/> when the joint is at a limit and the step asks for more.</summary>
        InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians);

        /// <summary>Link transform by URDF link name; the end effector when the name is empty.</summary>
        Transform FindLinkTransform(string linkName);

        /// <summary>
        /// As <see cref="FindLinkTransform"/> but without the end-effector fallback, for
        /// callers that place things in the link's own frame and so need the exact link.
        /// </summary>
        bool TryFindLinkTransform(string linkName, out Transform link);

        void BeginInteraction();
        void EndInteraction();

        /// <summary>
        /// Raised when something takes the robot's pose into its own hands: the user (a drag, a
        /// jog, a mirrored hand, all through <see cref="BeginInteraction"/>) or a local playback
        /// (a plan or solution preview, a demonstration replay). Whoever is feeding the robot
        /// from elsewhere, such as a live mirror of the real robot's joint states, listens and
        /// lets go; otherwise the two write the same joints in turn and the user's pose or the
        /// preview never shows.
        /// </summary>
        event System.Action ControlTaken;

        /// <summary>Announce that the caller is about to drive the robot's pose (raises <see cref="ControlTaken"/>).</summary>
        void TakeControl();
    }

    /// <summary>
    /// A robot's gripper, for robots that have one. A robot model that can open and close a
    /// gripper implements this next to <see cref="IRobotModel"/>; <see cref="RobotInstance.Gripper"/>
    /// is null for a robot without one.
    /// </summary>
    public interface IRobotGripper
    {
        /// <summary>False for a robot whose description has no gripper joints.</summary>
        bool HasGripper { get; }
        /// <summary>Open a closed gripper, close an open one.</summary>
        void ToggleGripper();

        /// <summary>
        /// Raised when the gripper is told to open (true) or to close (false), whoever asked. The
        /// robot model moves its own fingers; whatever drives the real gripper (a planner plugin
        /// that can reach the robot's controllers) listens here and sends the command on.
        /// </summary>
        event System.Action<bool> GripperCommanded;
    }
}
