using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Robot
{
    public enum JointKind { Revolute, Continuous, Prismatic }

    /// <summary>What a joint is for: the arm's IK chain, the gripper, or neither (not driven).</summary>
    public enum JointRole { Arm, Gripper, Passive }

    public enum GripperKind { None, Prismatic, Revolute }

    /// <summary>A joint that follows another: <c>position = leader * multiplier + offset</c>.</summary>
    [Serializable]
    public class MimicSpec
    {
        [Tooltip("Joint this one follows. Empty = the joint moves on its own.")]
        public string leader = "";
        public float multiplier = 1f;
        public float offset;

        public bool IsSet => !string.IsNullOrEmpty(leader);
        public float Follow(float leaderPosition) => leaderPosition * multiplier + offset;
        /// <summary>The leader position that puts this joint at <paramref name="position"/>.</summary>
        public float LeaderFor(float position) => Mathf.Approximately(multiplier, 0f) ? 0f : (position - offset) / multiplier;
    }

    /// <summary>One movable joint of a robot model. Positions are radians, or metres for a prismatic joint.</summary>
    [Serializable]
    public class JointSpec
    {
        public string name = "";
        [Tooltip("Name of the link GameObject the joint's ArticulationBody sits on.")]
        public string childLink = "";
        public JointKind kind = JointKind.Revolute;
        public JointRole role = JointRole.Arm;

        [Header("Limits")]
        [Tooltip("Lower limit as imported from the URDF (radians or metres).")]
        public float urdfLower;
        [Tooltip("Upper limit as imported from the URDF (radians or metres).")]
        public float urdfUpper;
        [Tooltip("Use Lower / Upper instead of the URDF limits.")]
        public bool overrideLimits;
        public float lower;
        public float upper;
        [Tooltip("Velocity limit (rad/s or m/s). 0 = not limited.")]
        public float maxVelocity;

        public MimicSpec mimic = new MimicSpec();

        /// <summary>False for a continuous joint, and for a joint imported without a limit range.</summary>
        public bool HasLimits => kind != JointKind.Continuous && Upper > Lower;
        public float Lower => overrideLimits ? lower : urdfLower;
        public float Upper => overrideLimits ? upper : urdfUpper;
        public bool IsMimic => mimic != null && mimic.IsSet;

        /// <summary>Whether <paramref name="position"/> is inside the limits, with a small tolerance for rounding.</summary>
        public bool Allows(float position, float tolerance = 1e-4f) =>
            !HasLimits || (position >= Lower - tolerance && position <= Upper + tolerance);
    }

    [Serializable]
    public class JointValue
    {
        public string joint = "";
        [Tooltip("Radians, or metres for a prismatic joint.")]
        public float value;
    }

    /// <summary>A named joint configuration, e.g. the SRDF group states <c>ready</c> or <c>open</c>.</summary>
    [Serializable]
    public class NamedPose
    {
        public string name = "";
        public JointValue[] joints = Array.Empty<JointValue>();
    }

    /// <summary>
    /// How the gripper opens and closes: one driver joint moving between two positions, with the
    /// other finger joints following it through their <see cref="JointSpec.mimic"/>.
    /// </summary>
    [Serializable]
    public class GripperSpec
    {
        public GripperKind kind = GripperKind.None;
        [Tooltip("The joint that is commanded; the other finger joints mimic it.")]
        public string driverJoint = "";
        [Tooltip("Driver joint position when fully open (radians or metres).")]
        public float open;
        [Tooltip("Driver joint position when fully closed (radians or metres).")]
        public float closed;
        [Tooltip("Gap between the fingertips when fully open, in metres.")]
        public float openWidthMetres;
        [Tooltip("Rate (m/s) at which the gap between the fingertips changes when opening or closing.")]
        public float speed = 0.1f;

        public bool IsSet => kind != GripperKind.None && !string.IsNullOrEmpty(driverJoint);

        /// <summary>Driver joint position for a fingertip gap in metres (clamped to the open/closed range).</summary>
        public float DriverAt(float widthMetres)
        {
            float t = openWidthMetres > 0f ? Mathf.Clamp01(widthMetres / openWidthMetres) : 0f;
            return Mathf.Lerp(closed, open, t);
        }

        /// <summary>Fingertip gap in metres for a driver joint position (clamped to the open/closed range).</summary>
        public float WidthAt(float driverPosition)
        {
            if (Mathf.Approximately(open, closed)) return 0f;
            return Mathf.Clamp01((driverPosition - closed) / (open - closed)) * openWidthMetres;
        }
    }

    /// <summary>One explicit pair in a <see cref="RobotNameMap"/>.</summary>
    [Serializable]
    public class NamePair
    {
        [Tooltip("Name in the Unity robot.")]
        public string local = "";
        [Tooltip("Name on the ROS side.")]
        public string ros = "";
    }

    /// <summary>
    /// Translates joint and link names between the Unity robot ("local") and the ROS robot:
    /// explicit pairs first, then a prefix swap (a prefix may be empty on one side: the ROS
    /// robot then adds its prefix to every name). Empty = the names are the same on both sides.
    /// </summary>
    [Serializable]
    public class RobotNameMap
    {
        /// <summary>A map that changes nothing.</summary>
        public static readonly RobotNameMap Identity = new RobotNameMap();

        [Tooltip("Prefix of the Unity robot's joint and link names, e.g. \"fr3_\".")]
        public string localPrefix = "";
        [Tooltip("Prefix the ROS robot uses instead, e.g. \"panda_\".")]
        public string rosPrefix = "";
        [Tooltip("Names that do not follow the prefix rule.")]
        public NamePair[] pairs = Array.Empty<NamePair>();

        public bool IsIdentity => (pairs == null || pairs.Length == 0) && (localPrefix ?? "") == (rosPrefix ?? "");

        /// <summary>The ROS name of a Unity joint or link.</summary>
        public string ToRos(string localName)
        {
            if (string.IsNullOrEmpty(localName)) return localName;
            if (pairs != null)
                foreach (NamePair pair in pairs)
                    if (pair != null && pair.local == localName) return pair.ros;
            return SwapPrefix(localName, localPrefix, rosPrefix);
        }

        /// <summary>The Unity name of a ROS joint or link.</summary>
        public string ToLocal(string rosName)
        {
            if (string.IsNullOrEmpty(rosName)) return rosName;
            if (pairs != null)
                foreach (NamePair pair in pairs)
                    if (pair != null && pair.ros == rosName) return pair.local;
            return SwapPrefix(rosName, rosPrefix, localPrefix);
        }

        /// <summary>The ROS names of Unity joints or links (the same array when nothing changes).</summary>
        public string[] ToRos(string[] localNames) => Map(localNames, toRos: true);

        /// <summary>The Unity names of ROS joints or links (the same array when nothing changes).</summary>
        public string[] ToLocal(string[] rosNames) => Map(rosNames, toRos: false);

        private string[] Map(string[] names, bool toRos)
        {
            if (names == null) return Array.Empty<string>();
            if (IsIdentity) return names;
            var mapped = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                mapped[i] = toRos ? ToRos(names[i]) : ToLocal(names[i]);
            return mapped;
        }

        private static string SwapPrefix(string name, string from, string to)
        {
            from ??= "";
            to ??= "";
            // An empty prefix on one side means the other side's prefix is added to every name.
            if (from == to || !name.StartsWith(from, StringComparison.Ordinal)) return name;
            return to + name.Substring(from.Length);
        }
    }

    /// <summary>
    /// The description of a robot model: links, planning groups, joints with their roles and
    /// limits, named poses, the gripper, the robot's own ROS names and how its joint and link
    /// names translate to the ROS side. Every <see cref="RobotInstance"/> of the model shares it.
    /// </summary>
    [CreateAssetMenu(fileName = "RobotProfile", menuName = "ERUPT/Robot Profile")]
    public class RobotProfile : ScriptableObject
    {
        public const string DefaultPlanningFrame = "world";
        public const string DefaultJointStatesTopic = "joint_states";

        [Header("Model")]
        public string displayName = "";
        [Tooltip("Root link of the robot, the one attached to the planning frame.")]
        public string baseLink = "";
        [Tooltip("Link the end-effector handle follows and IK solves for.")]
        public string endEffectorLink = "";
        [Tooltip("ROS frame the robot plans in.")]
        public string planningFrame = DefaultPlanningFrame;

        [Header("Planning groups (SRDF)")]
        public string armGroup = "";
        [Tooltip("Optional.")]
        public string gripperGroup = "";
        [Tooltip("SRDF end effector name.")]
        public string endEffectorName = "";

        [Header("Joints")]
        public JointSpec[] joints = Array.Empty<JointSpec>();

        [Header("Joint limits")]
        [Tooltip("Keep every revolute joint this far (radians) inside both of its position limits.")]
        [Min(0f)] public float limitMarginRadians;
        [Tooltip("Keep every prismatic joint this far (metres) inside both of its position limits.")]
        [Min(0f)] public float limitMarginMetres;
        [Tooltip("Hold motion the user drives directly (dragging a handle, jogging a joint) to each joint's Max Velocity. " +
                 "Trajectory previews and mirrored joint states are not slowed down.")]
        public bool limitUserVelocity;

        [Header("Named poses")]
        public NamedPose[] poses = Array.Empty<NamedPose>();
        [Tooltip("The pose the robot starts in. Empty = the pose it was imported in.")]
        public string initialPose = "";

        [Header("Gripper")]
        public GripperSpec gripper = new GripperSpec();

        [Header("ROS names (relative to the robot's namespace)")]
        [Tooltip("What the ROS side calls this robot model, e.g. \"ur5e\" (ROBOT=ur5e in the ERUPT ROS workspace). " +
                 "It says which ROS robot the profile was made for; nothing is sent under this name.")]
        public string rosRobotName = "";
        public string jointStatesTopic = DefaultJointStatesTopic;
        [Tooltip("Where planned trajectories are published for execution: <controller>/joint_trajectory.")]
        public string trajectoryCommandTopic = "";

        [Header("ROS <-> Unity names")]
        public RobotNameMap names = new RobotNameMap();

        [Header("Previews")]
        [Tooltip("Copy of the robot shown translucent as the start and goal previews.")]
        public GameObject ghostPrefab;

        /// <summary>The name rule; never null.</summary>
        public RobotNameMap Names => names ?? RobotNameMap.Identity;

        /// <summary>The ROS name of a Unity joint or link (unchanged when no rule applies).</summary>
        public string ToRos(string localName) => Names.ToRos(localName);

        /// <summary>The Unity name of a ROS joint or link (unchanged when no rule applies).</summary>
        public string ToLocal(string rosName) => Names.ToLocal(rosName);

        public bool TryGetJoint(string jointName, out JointSpec joint)
        {
            joint = null;
            if (joints == null || string.IsNullOrEmpty(jointName)) return false;
            foreach (JointSpec candidate in joints)
                if (candidate != null && candidate.name == jointName) { joint = candidate; return true; }
            return false;
        }

        public bool TryGetPose(string poseName, out NamedPose pose)
        {
            pose = null;
            if (poses == null || string.IsNullOrEmpty(poseName)) return false;
            foreach (NamedPose candidate in poses)
                if (candidate != null && candidate.name == poseName) { pose = candidate; return true; }
            return false;
        }

        /// <summary>
        /// The joint limits this profile describes (overrides, margins and velocity limits
        /// applied). A new model each call: build it once per robot.
        /// </summary>
        public JointLimitModel BuildLimits() => JointLimitModel.FromProfile(this);

        /// <summary>The pose named by <see cref="initialPose"/>, or null.</summary>
        public NamedPose InitialPose => TryGetPose(initialPose, out NamedPose pose) ? pose : null;

        /// <summary>Joint names with a role, in profile order.</summary>
        public List<string> JointNames(JointRole role)
        {
            var result = new List<string>();
            if (joints == null) return result;
            foreach (JointSpec joint in joints)
                if (joint != null && joint.role == role) result.Add(joint.name);
            return result;
        }

        /// <summary>
        /// Everything wrong with the profile, one sentence per problem; empty when it is usable.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            var seen = new HashSet<string>();

            foreach (JointSpec joint in joints ?? Array.Empty<JointSpec>())
            {
                if (joint == null || string.IsNullOrEmpty(joint.name))
                {
                    problems.Add("A joint has no name.");
                    continue;
                }
                if (!seen.Add(joint.name))
                    problems.Add($"Joint '{joint.name}' is listed more than once.");
                if (string.IsNullOrEmpty(joint.childLink))
                    problems.Add($"Joint '{joint.name}' has no child link.");
                if (joint.kind != JointKind.Continuous && joint.Lower > joint.Upper)
                    problems.Add($"Joint '{joint.name}' has its lower limit ({joint.Lower}) above its upper limit ({joint.Upper}).");
            }

            foreach (JointSpec joint in joints ?? Array.Empty<JointSpec>())
            {
                if (joint == null || !joint.IsMimic) continue;
                if (joint.mimic.leader == joint.name)
                    problems.Add($"Joint '{joint.name}' mimics itself.");
                else if (!seen.Contains(joint.mimic.leader))
                    problems.Add($"Joint '{joint.name}' mimics '{joint.mimic.leader}', which is not a joint of this robot.");
            }

            var poseNames = new HashSet<string>();
            foreach (NamedPose pose in poses ?? Array.Empty<NamedPose>())
            {
                if (pose == null || string.IsNullOrEmpty(pose.name))
                {
                    problems.Add("A pose has no name.");
                    continue;
                }
                if (!poseNames.Add(pose.name))
                    problems.Add($"Pose '{pose.name}' is listed more than once.");
                foreach (JointValue value in pose.joints ?? Array.Empty<JointValue>())
                {
                    if (value == null) continue;
                    if (!TryGetJoint(value.joint, out JointSpec joint))
                        problems.Add($"Pose '{pose.name}' names '{value.joint}', which is not a joint of this robot.");
                    else if (!joint.Allows(value.value))
                        problems.Add($"Pose '{pose.name}' puts '{value.joint}' at {value.value}, outside its limits [{joint.Lower}, {joint.Upper}].");
                }
            }

            if (!string.IsNullOrEmpty(initialPose) && !poseNames.Contains(initialPose))
                problems.Add($"The initial pose '{initialPose}' is not one of the named poses.");

            if (gripper != null && gripper.kind != GripperKind.None)
            {
                if (!TryGetJoint(gripper.driverJoint, out JointSpec driver))
                    problems.Add($"The gripper driver joint '{gripper.driverJoint}' is not a joint of this robot.");
                else
                {
                    if (driver.role != JointRole.Gripper)
                        problems.Add($"The gripper driver joint '{driver.name}' has the role {driver.role}; it must be a Gripper joint.");
                    bool prismaticDriver = driver.kind == JointKind.Prismatic;
                    if (prismaticDriver != (gripper.kind == GripperKind.Prismatic))
                        problems.Add($"The gripper is {gripper.kind} but its driver joint '{driver.name}' is {driver.kind}.");
                    if (!driver.Allows(gripper.open))
                        problems.Add($"The gripper's open position {gripper.open} is outside the limits of '{driver.name}' [{driver.Lower}, {driver.Upper}].");
                    if (!driver.Allows(gripper.closed))
                        problems.Add($"The gripper's closed position {gripper.closed} is outside the limits of '{driver.name}' [{driver.Lower}, {driver.Upper}].");
                }
                if (gripper.openWidthMetres <= 0f)
                    problems.Add("The gripper has no open width.");
            }

            return problems;
        }

        private void OnValidate()
        {
            foreach (string problem in Validate())
                Debug.LogWarning($"[RobotProfile] {name}: {problem}", this);
        }
    }
}
