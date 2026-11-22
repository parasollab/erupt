using Erupt.Interaction;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Erupt.Robot
{
    public class DirectArticulationIKController : MonoBehaviour, Erupt.Robot.IRobotModel, IRobotGripper
    {
        [System.Serializable]
        public struct JointInitialPosition
        {
            public string name;
            public float positionRadians;
        }

        [SerializeField] private Transform robotRoot;
        [SerializeField] private Transform endEffector;
        [SerializeField] private int maxIterations = 12;
        [SerializeField] private float positionTolerance = 0.008f;
        [SerializeField] private float maxAngleStepDegrees = 4f;
        [SerializeField] private float solveWeight = 0.85f;
        [Tooltip("Pose applied on start instead of the robot profile's initial pose. Empty = use the profile's.")]
        [SerializeField] private JointInitialPosition[] initialPose;
        [Tooltip("Rate (m/s) at which OpenGripper, CloseGripper and ToggleGripper change the gap between the fingertips. " +
                 "A robot profile that describes the gripper supplies its own speed.")]
        [SerializeField] private float gripperMaxSpeed = 0.1f;
        [Tooltip("Keep the robot's colliders out of physics contacts (they stay raycastable for selection). " +
                 "The robot is driven kinematically, so a contact with a carried object or a table can only " +
                 "shove the arm, and the next joint write would then hold that drifted pose.")]
        [SerializeField] private bool ignorePhysicsContacts = true;

        private readonly HashSet<string> warnedMissingJoints = new HashSet<string>();

        // The model description of the robot being driven: joint roles, the initial pose and the
        // gripper. Null for a robot without one; roles are then guessed from the joint types.
        private RobotProfile profile;

        private readonly List<ArticulationBody> joints = new List<ArticulationBody>();
        private readonly List<string> jointNames = new List<string>();
        private readonly Dictionary<string, ArticulationBody> jointByName = new Dictionary<string, ArticulationBody>();
        private readonly List<ArticulationBody> allBodies = new List<ArticulationBody>();
        private readonly List<float> heldJointPositions = new List<float>();
        private bool isInteracting;

        // The robot's joint limits: from the profile, or for a robot without one from the
        // drive limits it was imported with. Every joint write is clamped to them.
        private JointLimitModel limits = JointLimitModel.Unlimited;
        private readonly Dictionary<ArticulationBody, JointLimit> limitByBody = new Dictionary<ArticulationBody, JointLimit>();
        // Position limits as imported, read once per body before StabilizeRobot frees the joint
        // locks (a free lock is how a continuous joint is told from a limited one afterwards).
        private readonly Dictionary<ArticulationBody, JointLimit> importedLimits = new Dictionary<ArticulationBody, JointLimit>();

        // User-driven motion under a velocity limit: where each joint stood when it was first
        // moved in the current frame, so that several solves in one frame share one budget.
        private int userMotionFrame = -1;
        private readonly Dictionary<ArticulationBody, float> userMotionStart = new Dictionary<ArticulationBody, float>();
        // Per IK joint, from the last solve: the solver wanted it past a position limit.
        private readonly List<bool> pinnedAtLimit = new List<bool>();
        // The last solve stopped a joint short because of the velocity limit.
        private bool solveRateLimited;

        // Gripper joints. They are outside the IK chain: each keeps a commanded position (metres
        // for a prismatic finger, radians for a revolute one) that is re-applied every physics
        // step, so the fingers cannot move on their own.
        private readonly List<ArticulationBody> gripperJoints = new List<ArticulationBody>();
        private readonly List<string> gripperJointNames = new List<string>();
        private readonly List<float> gripperJointPositions = new List<float>();
        // Per gripper joint: how it follows another gripper joint (from the profile), or null.
        private readonly List<MimicSpec> gripperMimics = new List<MimicSpec>();
        private readonly List<int> gripperMimicLeaders = new List<int>();
        // Index of the profile's gripper driver joint among the gripper joints, or -1 when the
        // profile does not describe the gripper (the width is then split over the finger joints).
        private int gripperDriver = -1;
        private float? gripperWidthGoal;

        /// <summary>The robot profile in use, or null.</summary>
        public RobotProfile Profile => profile;

        public Transform Root => robotRoot;
        public Transform EndEffector => endEffector;
        public IReadOnlyList<string> JointNames => jointNames;
        public IReadOnlyList<ArticulationBody> Joints => joints;

        /// <summary>The robot's joint limits; every joint write is clamped to them.</summary>
        public IJointLimits Limits => limits;

        /// <summary>The limits of the joint on a body. False for a body this controller does not drive.</summary>
        public bool TryGetLimit(ArticulationBody body, out JointLimit limit)
        {
            if (body != null && limitByBody.TryGetValue(body, out limit)) return true;
            limit = default;
            return false;
        }

        public bool TryGetJointLink(string jointName, out Transform link)
        {
            link = null;
            if (string.IsNullOrEmpty(jointName)) return false;
            int gripperIndex = gripperJointNames.IndexOf(jointName);
            if (gripperIndex >= 0) link = gripperJoints[gripperIndex].transform;
            else if (jointByName.TryGetValue(jointName, out ArticulationBody joint)) link = joint.transform;
            return link != null;
        }

        public bool HasGripper => gripperJoints.Count > 0;

        /// <summary>Commanded gap between the fingertips in metres.</summary>
        public float GripperWidth
        {
            get
            {
                if (gripperDriver >= 0)
                    return profile.gripper.WidthAt(gripperJointPositions[gripperDriver]);

                // Without a gripper description: the finger joints' travel added up.
                float width = 0f;
                foreach (float travel in gripperJointPositions)
                    width += travel;
                return width;
            }
        }

        /// <summary>Widest gap between the fingertips, in metres.</summary>
        public float GripperOpenWidth => GripperLimitWidth(open: true);

        /// <summary>
        /// Sets the gap between the fingertips (metres) at once, clamped to what the gripper
        /// allows. The fingers stay symmetric.
        /// </summary>
        public void SetGripperWidth(float width)
        {
            gripperWidthGoal = null;
            ApplyGripperWidth(width);
        }

        /// <summary>Opens the gripper at its speed (the profile's, or <see cref="gripperMaxSpeed"/>).</summary>
        public void OpenGripper()
        {
            if (!HasGripper) return;
            gripperWidthGoal = GripperLimitWidth(open: true);
            GripperCommanded?.Invoke(true);
        }

        /// <inheritdoc/>
        public event System.Action<bool> GripperCommanded;

        /// <summary>Closes the gripper at its speed (the profile's, or <see cref="gripperMaxSpeed"/>).</summary>
        public void CloseGripper()
        {
            if (!HasGripper) return;
            gripperWidthGoal = GripperLimitWidth(open: false);
            GripperCommanded?.Invoke(false);
        }

        /// <summary>Closes a gripper that is (or is on its way to being) more than half open; opens it otherwise.</summary>
        public void ToggleGripper()
        {
            float open = GripperLimitWidth(open: true);
            float closed = GripperLimitWidth(open: false);
            if ((gripperWidthGoal ?? GripperWidth) > (open + closed) * 0.5f) CloseGripper();
            else OpenGripper();
        }

        public int GetJointIndex(ArticulationBody joint)
        {
            return joint != null ? joints.IndexOf(joint) : -1;
        }

        /// <summary>
        /// Returns the exclusive end of the controlled joint range that can move a point
        /// on this body. A link point includes the body's own revolute joint.
        /// </summary>
        public int GetBodyPointEffectorIndex(ArticulationBody body)
        {
            int lastJointIndex = GetLastAffectingJointIndex(body);
            return lastJointIndex >= 0 ? lastJointIndex + 1 : -1;
        }

        public bool CanControlBodyPoint(ArticulationBody body)
        {
            return GetBodyPointEffectorIndex(body) > 0;
        }

        private readonly Dictionary<string, Transform> linkByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a URDF link name (e.g. "fr3_hand") to its Transform in the imported robot.
        /// The URDF importer names link GameObjects after their link names. Falls back to the
        /// end effector (with a warning) so attach visuals degrade gracefully on a name mismatch.
        /// </summary>
        public Transform FindLinkTransform(string linkName)
        {
            if (string.IsNullOrEmpty(linkName)) return endEffector;
            if (TryFindLinkTransform(linkName, out var link)) return link;

            Debug.LogWarning($"[IK] FindLinkTransform: no link named '{linkName}' under robot root; using end effector.");
            return endEffector;
        }

        /// <summary>
        /// As <see cref="FindLinkTransform"/> but without the end-effector fallback, for callers
        /// that place things in the link's own frame and so need the exact link.
        /// </summary>
        public bool TryFindLinkTransform(string linkName, out Transform link)
        {
            link = null;
            if (string.IsNullOrEmpty(linkName)) return false;

            if (linkByName.TryGetValue(linkName, out var cached) && cached) { link = cached; return true; }
            linkByName.Remove(linkName);

            if (robotRoot != null)
            {
                foreach (Transform t in robotRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (string.Equals(t.name, linkName, StringComparison.OrdinalIgnoreCase))
                    {
                        linkByName[linkName] = t;
                        link = t;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Forget resolved link transforms, e.g. after the robot hierarchy changed.</summary>
        public void ClearLinkCache() => linkByName.Clear();

        private void Awake()
        {
            if (robotRoot == null) return;
            RobotProfile found = FindProfile(robotRoot);
            // A profile names the end-effector link, so the reference may be left empty.
            if (endEffector == null && found != null && !string.IsNullOrEmpty(found.endEffectorLink))
                TryFindLinkTransform(found.endEffectorLink, out endEffector);
            if (endEffector != null)
                Configure(robotRoot, endEffector, found);
        }

        /// <summary>
        /// Drive the robot under <paramref name="root"/>, with the profile of the
        /// <see cref="RobotInstance"/> it belongs to (none = joint roles are guessed).
        /// </summary>
        public void Configure(Transform root, Transform toolTransform) =>
            Configure(root, toolTransform, FindProfile(root));

        public void Configure(Transform root, Transform toolTransform, RobotProfile robotProfile)
        {
            robotRoot = root;
            endEffector = toolTransform;
            profile = robotProfile;
            linkByName.Clear();
            CaptureImportedLimits(robotRoot);
            StabilizeRobot(robotRoot);
            BuildJointChain(robotRoot);
            ApplyInitialPose();
            CaptureHeldPose();
            ZeroJointVelocities();
        }

        private static RobotProfile FindProfile(Transform root)
        {
            RobotInstance instance = root != null ? root.GetComponentInParent<RobotInstance>(true) : null;
            return instance != null ? instance.Profile : null;
        }

        /// <inheritdoc/>
        public event System.Action ControlTaken;

        /// <inheritdoc/>
        public void TakeControl() => ControlTaken?.Invoke();

        public void BeginInteraction()
        {
            TakeControl();
            isInteracting = true;
            CaptureHeldPose();
            ZeroJointVelocities();
        }

        public void EndInteraction()
        {
            isInteracting = false;
            CaptureHeldPose();
            ZeroJointVelocities();
        }

        public void SolveToTarget(Vector3 targetPosition)
        {
            SolvePointToTarget(endEffector, Vector3.zero, 0, joints.Count, targetPosition);
        }

        /// <summary>
        /// Treats a selected joint handle as the temporary effector. Only joints between
        /// the articulation root and the selected joint are changed; the selected joint's
        /// own rotation cannot move its pivot.
        /// </summary>
        public InteractionRefusal TrySolveJointToTarget(
            ArticulationBody selectedJoint,
            Vector3 targetPosition,
            int firstJointIndex = 0)
        {
            int selectedIndex = joints.IndexOf(selectedJoint);
            if (selectedIndex < 0)
            {
                return InteractionRefusal.Refuse(RefusalCode.NotControllable, "That joint cannot be used as an IK target.", targetPosition);
            }

            firstJointIndex = Mathf.Clamp(firstJointIndex, 0, selectedIndex);
            if (selectedIndex == firstJointIndex)
                return InteractionRefusal.Refuse(
                    RefusalCode.NotControllable,
                    "No movable joint exists before this target in the active chain segment.",
                    targetPosition);

            SolvePointToTarget(
                selectedJoint.transform,
                selectedJoint.anchorPosition,
                firstJointIndex,
                selectedIndex,
                targetPosition);

            float residual = Vector3.Distance(GetJointPivot(selectedJoint), targetPosition);
            return ReachRefusal(residual, $"{selectedJoint.name} target", targetPosition, selectedJoint.transform);
        }

        /// <summary>
        /// Drives a material point captured in an articulation body's local frame. This is
        /// the link-grab equivalent of TrySolveJointToTarget.
        /// </summary>
        public InteractionRefusal TrySolveBodyPointToTarget(
            ArticulationBody body,
            Vector3 localGrabPoint,
            Vector3 targetPosition,
            int firstJointIndex = 0)
        {
            int effectorIndex = GetBodyPointEffectorIndex(body);
            if (effectorIndex < 0)
                return InteractionRefusal.Refuse(
                    RefusalCode.NotControllable,
                    "That robot link is not part of the controlled revolute chain.",
                    targetPosition);

            firstJointIndex = Mathf.Clamp(firstJointIndex, 0, effectorIndex);
            if (firstJointIndex == effectorIndex)
                return InteractionRefusal.Refuse(
                    RefusalCode.NotControllable,
                    "No movable joint remains in this active chain segment.",
                    targetPosition);

            SolvePointToTarget(
                body.transform, localGrabPoint, firstJointIndex, effectorIndex, targetPosition);

            Vector3 achieved = body.transform.TransformPoint(localGrabPoint);
            float residual = Vector3.Distance(achieved, targetPosition);
            return ReachRefusal(residual, $"{body.name} grab point", targetPosition, body.transform);
        }

        private void SolvePointToTarget(
            Transform effectorFrame,
            Vector3 localEffectorPoint,
            int firstJointIndex,
            int jointCount,
            Vector3 targetPosition)
        {
            solveRateLimited = false;
            pinnedAtLimit.Clear();
            for (int i = 0; i < joints.Count; i++) pinnedAtLimit.Add(false);

            if (effectorFrame == null || jointCount <= 0)
                return;

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                Vector3 effectorPosition = effectorFrame.TransformPoint(localEffectorPoint);
                Vector3 error = targetPosition - effectorPosition;
                if (error.sqrMagnitude <= positionTolerance * positionTolerance)
                {
                    break;
                }

                for (int i = Mathf.Min(jointCount, joints.Count) - 1;
                     i >= Mathf.Max(0, firstJointIndex);
                     i--)
                {
                    ArticulationBody joint = joints[i];
                    Vector3 jointPosition = GetJointPivot(joint);
                    Vector3 toEnd = effectorPosition - jointPosition;
                    Vector3 toTarget = targetPosition - jointPosition;
                    if (toEnd.sqrMagnitude < 0.000001f || toTarget.sqrMagnitude < 0.000001f)
                    {
                        continue;
                    }

                    Vector3 axis = GetWorldMotionAxis(joint);
                    float deltaDegrees = Vector3.SignedAngle(toEnd, toTarget, axis);
                    deltaDegrees = Mathf.Clamp(deltaDegrees * solveWeight, -maxAngleStepDegrees, maxAngleStepDegrees);
                    ApplyBestJointDelta(
                        i,
                        effectorFrame,
                        localEffectorPoint,
                        deltaDegrees * Mathf.Deg2Rad,
                        targetPosition);
                    effectorPosition = effectorFrame.TransformPoint(localEffectorPoint);
                }
            }

            CaptureHeldPose();
            ZeroJointVelocities();
        }

        /// <summary>
        /// SolveToTarget, but reporting why it could not reach the target. Guidelines Part 3:
        /// interaction refusals carry a user-facing reason instead of failing silently.
        /// </summary>
        public InteractionRefusal TrySolveToTarget(Vector3 targetPosition)
        {
            return TrySolveToTarget(targetPosition, 0);
        }

        public InteractionRefusal TrySolveToTarget(Vector3 targetPosition, int firstJointIndex)
        {
            if (endEffector == null || joints.Count == 0)
            {
                return InteractionRefusal.Refuse(RefusalCode.NotControllable, "Robot is not configured for interaction.", targetPosition);
            }

            SolvePointToTarget(
                endEffector,
                Vector3.zero,
                Mathf.Clamp(firstJointIndex, 0, joints.Count),
                joints.Count,
                targetPosition);

            float residual = Vector3.Distance(endEffector.position, targetPosition);
            return ReachRefusal(residual, "Target", targetPosition, endEffector);
        }

        // Why the last solve fell short of its target, if it did. A joint the solver wanted
        // past a position limit makes it a joint-limit refusal that names the joint and points
        // at its link; otherwise the target is out of reach, which is about the thing being
        // dragged. A solve that only ran out of velocity budget is not a refusal: the robot is
        // still on its way.
        private InteractionRefusal ReachRefusal(float residual, string what, Vector3 targetPosition, Transform dragged)
        {
            if (residual <= positionTolerance || solveRateLimited)
                return InteractionRefusal.None;

            for (int i = 0; i < pinnedAtLimit.Count && i < joints.Count; i++)
            {
                if (!pinnedAtLimit[i]) continue;
                return LimitRefusal(joints[i], jointNames[i], GetJointPivot(joints[i]));
            }

            return InteractionRefusal.Refuse(
                RefusalCode.OutOfReach, $"{what} is out of reach by {residual * 100f:F0} cm.", targetPosition, dragged);
        }

        private InteractionRefusal LimitRefusal(ArticulationBody joint, string jointName, Vector3 worldPoint)
        {
            TryGetLimit(joint, out JointLimit limit);
            bool upper = limit.IsAtUpper(joint.jointPosition[0]);
            return InteractionRefusal.Refuse(
                RefusalCode.JointLimit, $"{jointName} is at its {(upper ? "upper" : "lower")} limit.", worldPoint, joint.transform);
        }

        public bool CanControlJoint(ArticulationBody joint)
        {
            return joint != null && joints.Contains(joint);
        }

        public void NudgeJoint(ArticulationBody joint, float deltaRadians)
        {
            if (!CanControlJoint(joint))
            {
                return;
            }

            ApplyUserJointPosition(joint, joint.jointPosition[0] + deltaRadians, out _);
            CaptureHeldPose();
            ZeroJointVelocities();
        }

        /// <summary>
        /// NudgeJoint, but reporting when the joint is at a position limit and the nudge asked
        /// for more: a silent clamp is what Guidelines Part 3 calls the most common source of
        /// novice confusion. A nudge that is only slowed by the velocity limit is not refused.
        /// </summary>
        public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians)
        {
            if (!CanControlJoint(joint))
            {
                return InteractionRefusal.Refuse(RefusalCode.NotControllable, "That joint cannot be driven.", transform.position);
            }

            ApplyUserJointPosition(joint, joint.jointPosition[0] + deltaRadians, out bool pastLimit);
            CaptureHeldPose();
            ZeroJointVelocities();

            return pastLimit
                ? LimitRefusal(joint, jointNames[joints.IndexOf(joint)], joint.transform.position)
                : InteractionRefusal.None;
        }

        /// <summary>The IK chain's joints followed by the gripper's finger joints.</summary>
        public string[] GetJointStateNames()
        {
            var names = new List<string>(jointNames);
            names.AddRange(gripperJointNames);
            return names.ToArray();
        }

        /// <summary>Logs each driven joint's limits (as the limit model has them) and its current position.</summary>
        public void LogJointLimits()
        {
            var sb = new System.Text.StringBuilder($"[IK] Joint limits of '{(robotRoot != null ? robotRoot.name : name)}':");
            void Append(string jointName, ArticulationBody joint)
            {
                TryGetLimit(joint, out JointLimit limit);
                string unit = limit.Kind == JointKind.Prismatic ? "m" : "rad";
                string range = limit.HasLimits ? $"[{limit.Lower:F4}, {limit.Upper:F4}] {unit}" : "no position limits";
                string velocity = limit.MaxVelocity > 0f ? $"{limit.MaxVelocity:F3} {unit}/s" : "no velocity limit";
                sb.Append($"\n  {jointName} ({joint.name}): {range}, {velocity}, at {joint.jointPosition[0]:F4}");
            }
            for (int i = 0; i < joints.Count; i++) Append(jointNames[i], joints[i]);
            for (int i = 0; i < gripperJoints.Count; i++) Append(gripperJointNames[i], gripperJoints[i]);
            Debug.Log(sb.ToString());
        }

        public float[] GetJointStatePositions()
        {
            float[] positions = new float[joints.Count + gripperJoints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                positions[i] = joints[i].jointPosition[0];
            }

            gripperJointPositions.CopyTo(positions, joints.Count);
            return positions;
        }

        public bool TryGetJointAngle(string jointName, out float positionRadians)
        {
            positionRadians = 0f;
            int gripperIndex = gripperJointNames.IndexOf(jointName);
            if (gripperIndex >= 0)
            {
                positionRadians = gripperJointPositions[gripperIndex];
                return true;
            }

            if (!jointByName.TryGetValue(jointName, out ArticulationBody joint))
            {
                return false;
            }

            positionRadians = joint.jointPosition[0];
            return true;
        }

        public JointWriteReport ApplyJointState(string[] names, double[] positions)
        {
            if (names == null || positions == null)
            {
                return JointWriteReport.Clean;
            }

            var report = new JointWriteReport.Builder();
            int count = Mathf.Min(names.Length, positions.Length);
            for (int i = 0; i < count; i++)
            {
                ApplyNamedJointPosition(names[i], (float)positions[i], ref report);
            }

            MirrorUnnamedFingers(names, count);
            CaptureHeldPose();
            ZeroJointVelocities();
            return report.Build();
        }

        public JointWriteReport ApplyJointState(IList<string> names, IList<float> positions)
        {
            if (names == null || positions == null)
            {
                return JointWriteReport.Clean;
            }

            var report = new JointWriteReport.Builder();
            int count = Mathf.Min(names.Count, positions.Count);
            for (int i = 0; i < count; i++)
            {
                ApplyNamedJointPosition(names[i], positions[i], ref report);
            }

            MirrorUnnamedFingers(names, count);
            CaptureHeldPose();
            ZeroJointVelocities();
            return report.Build();
        }

        // A joint state may name only some of the gripper joints: MoveIt trajectories carry the
        // active finger and leave out the ones that mimic it. The joints it does not name follow
        // the ones it does, so the hand stays symmetric.
        private void MirrorUnnamedFingers(IList<string> names, int count)
        {
            if (gripperJoints.Count < 2) return;

            var named = new bool[gripperJointNames.Count];
            int namedCount = 0;
            for (int g = 0; g < gripperJointNames.Count; g++)
            {
                for (int i = 0; i < count; i++)
                    if (names[i] == gripperJointNames[g]) { named[g] = true; namedCount++; break; }
            }
            if (namedCount == 0 || namedCount == gripperJoints.Count) return;

            if (gripperDriver < 0)
            {
                // No gripper description: every unnamed finger copies the first named one.
                int source = Array.IndexOf(named, true);
                for (int g = 0; g < gripperJointNames.Count; g++)
                    if (!named[g]) SetGripperJointPosition(g, gripperJointPositions[source]);
                return;
            }

            // A state that names a follower but not its leader moves the leader to match.
            for (int g = 0; g < gripperJointNames.Count; g++)
            {
                int leader = gripperMimicLeaders[g];
                if (!named[g] || leader < 0 || named[leader]) continue;
                SetGripperJointPosition(leader, gripperMimics[g].LeaderFor(gripperJointPositions[g]));
                named[leader] = true;
            }
            FollowMimics(named);
        }

        // Puts every follower whose leader is a gripper joint at its mimic position. Joints
        // flagged in skip (they were set explicitly) are left alone.
        private void FollowMimics(bool[] skip = null)
        {
            for (int g = 0; g < gripperJoints.Count; g++)
            {
                int leader = gripperMimicLeaders[g];
                if (leader < 0 || (skip != null && skip[g])) continue;
                SetGripperJointPosition(g, gripperMimics[g].Follow(gripperJointPositions[leader]));
            }
        }

        private void Update()
        {
            if (gripperWidthGoal == null)
                return;

            float goal = gripperWidthGoal.Value;
            float width = Mathf.MoveTowards(GripperWidth, goal, GripperSpeed * Time.deltaTime);
            ApplyGripperWidth(width);
            if (Mathf.Approximately(width, goal))
                gripperWidthGoal = null;
        }

        private void FixedUpdate()
        {
            if (!isInteracting)
            {
                ApplyHeldPose();
            }

            // IK never drives the fingers, so they are held during an interaction as well.
            for (int i = 0; i < gripperJoints.Count; i++)
                ApplyJointPosition(gripperJoints[i], gripperJointPositions[i]);
        }

        private void StabilizeRobot(Transform root)
        {
            allBodies.Clear();
            if (root == null)
            {
                return;
            }

            allBodies.AddRange(root.GetComponentsInChildren<ArticulationBody>());
            if (ignorePhysicsContacts)
            {
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                    collider.excludeLayers = ~0;
            }
            foreach (ArticulationBody body in allBodies)
            {
                body.useGravity = false;
                SetZeroJointVelocity(body);
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;

                if (body.isRoot)
                {
                    body.immovable = true;
                }

                // The PhysX joint limits are switched off on purpose. The arm is teleported every
                // physics step, PhysX does not apply limits to a teleport, and a limited lock on
                // a zero-stiffness drive lets the solver push the joint between steps. The joint
                // limits are enforced in software instead: every write goes through JointWriter,
                // which clamps to the limit model.
                if (body.jointType == ArticulationJointType.RevoluteJoint)
                    body.twistLock = ArticulationDofLock.FreeMotion;
            }
        }

        // Reads each joint's position limits off its drive, once, while the joint locks are
        // still as imported. Used only for a robot without a profile.
        private void CaptureImportedLimits(Transform root)
        {
            if (root == null) return;
            foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (importedLimits.ContainsKey(body)) continue;

                ArticulationDrive drive = body.xDrive;
                if (body.jointType == ArticulationJointType.RevoluteJoint)
                {
                    // Drive limits are degrees. A free twist lock is a continuous joint, whatever its drive says.
                    bool limited = body.twistLock != ArticulationDofLock.FreeMotion && drive.upperLimit > drive.lowerLimit;
                    importedLimits[body] = limited
                        ? new JointLimit("", JointKind.Revolute, drive.lowerLimit * Mathf.Deg2Rad, drive.upperLimit * Mathf.Deg2Rad)
                        : JointLimit.Unlimited("", JointKind.Continuous);
                }
                else if (body.jointType == ArticulationJointType.PrismaticJoint)
                {
                    importedLimits[body] = new JointLimit("", JointKind.Prismatic, drive.lowerLimit, drive.upperLimit);
                }
            }
        }

        // The limits of a driven joint under its name: the profile's, or without a profile the imported ones.
        private JointLimit ResolveLimit(ArticulationBody body, string jointName)
        {
            if (profile != null)
                return limits.TryGet(jointName, out JointLimit described) ? described : JointLimit.Unlimited(jointName);

            importedLimits.TryGetValue(body, out JointLimit imported);
            return new JointLimit(jointName, imported.Kind, imported.Lower, imported.Upper, imported.MaxVelocity);
        }

        private void BuildJointChain(Transform root)
        {
            joints.Clear();
            jointNames.Clear();
            jointByName.Clear();
            gripperJoints.Clear();
            gripperJointNames.Clear();
            gripperJointPositions.Clear();
            gripperMimics.Clear();
            gripperMimicLeaders.Clear();
            gripperDriver = -1;
            gripperWidthGoal = null;
            limitByBody.Clear();
            userMotionStart.Clear();
            pinnedAtLimit.Clear();
            limits = profile != null ? profile.BuildLimits() : JointLimitModel.Unlimited;
            if (root == null)
                return;

            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!IsUrdfJoint(component))
                    continue;

                ArticulationBody body = component.GetComponent<ArticulationBody>();
                if (body == null)
                    continue;

                bool revolute = body.jointType == ArticulationJointType.RevoluteJoint;
                bool prismatic = body.jointType == ArticulationJointType.PrismaticJoint;
                if (!revolute && !prismatic)
                    continue;

                // Without a profile, a prismatic joint the end effector does not hang off is a
                // gripper finger, one it does hang off (a lift axis) stays outside this
                // controller, and every revolute joint belongs to the arm.
                bool guessedFinger = prismatic && (endEffector == null || !endEffector.IsChildOf(body.transform));
                if (profile == null && !revolute && !guessedFinger)
                    continue;

                bool gotName = TryGetUrdfJointName(component, out string name);
                string resolvedName = !string.IsNullOrWhiteSpace(name) ? name : component.name;
                if (!gotName || string.IsNullOrWhiteSpace(name))
                    Debug.LogWarning($"[IK] BuildJointChain: could not read joint name from UrdfJoint on '{component.name}', falling back to GameObject name '{resolvedName}'");

                JointRole role;
                if (profile == null)
                    role = guessedFinger ? JointRole.Gripper : JointRole.Arm;
                else if (profile.TryGetJoint(resolvedName, out JointSpec spec))
                    role = spec.role;
                else
                {
                    Debug.LogWarning($"[IK] BuildJointChain: joint '{resolvedName}' is not in the profile '{profile.name}'; it is not driven.");
                    role = JointRole.Passive;
                }

                if (role == JointRole.Gripper)
                {
                    JointLimit limit = ResolveLimit(body, resolvedName);
                    limitByBody[body] = limit;
                    gripperJoints.Add(body);
                    gripperJointNames.Add(resolvedName);
                    gripperJointPositions.Add(limit.Clamp(body.jointPosition[0]));
                }
                else if (role == JointRole.Arm && revolute)
                {
                    // IK turns joints; a prismatic arm joint (a lift axis) is left where it is.
                    AddJoint(body, resolvedName);
                }
            }

            ResolveGripperDescription();

            var sb = new System.Text.StringBuilder("[IK] BuildJointChain discovered joints:");
            for (int i = 0; i < jointNames.Count; i++)
                sb.Append($"\n  [{i}] name='{jointNames[i]}' go='{joints[i].name}'");
            for (int i = 0; i < gripperJointNames.Count; i++)
                sb.Append($"\n  [gripper {i}] name='{gripperJointNames[i]}' go='{gripperJoints[i].name}'");
            Debug.Log(sb.ToString());

            // Fallback for robots not imported via the URDF importer.
            if (joints.Count == 0 && profile == null)
            {
                foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
                {
                    if (body.jointType == ArticulationJointType.RevoluteJoint)
                        AddJoint(body, body.name);
                }
            }

            // Without a profile the limit model is the imported limits of the joints found.
            if (profile == null)
                limits = new JointLimitModel(limitByBody.Values);
        }

        // Reads from the profile which gripper joint is commanded and which joints follow which.
        private void ResolveGripperDescription()
        {
            for (int g = 0; g < gripperJointNames.Count; g++)
            {
                MimicSpec mimic = null;
                int leader = -1;
                if (profile != null && profile.TryGetJoint(gripperJointNames[g], out JointSpec spec) && spec.IsMimic)
                {
                    leader = gripperJointNames.IndexOf(spec.mimic.leader);
                    if (leader >= 0 && leader != g) mimic = spec.mimic;
                    else leader = -1;
                }
                gripperMimics.Add(mimic);
                gripperMimicLeaders.Add(leader);
            }

            if (profile == null || profile.gripper == null || !profile.gripper.IsSet) return;
            gripperDriver = gripperJointNames.IndexOf(profile.gripper.driverJoint);
            if (gripperDriver < 0 && gripperJoints.Count > 0)
                Debug.LogWarning($"[IK] The gripper driver joint '{profile.gripper.driverJoint}' of profile '{profile.name}' " +
                                 "is not one of the robot's gripper joints; the gripper width is split over the finger joints instead.");
        }

        private void AddJoint(ArticulationBody joint, string jointName)
        {
            joints.Add(joint);
            jointNames.Add(jointName);
            jointByName[jointName] = joint;
            limitByBody[joint] = ResolveLimit(joint, jointName);
        }

        private static bool IsUrdfJoint(MonoBehaviour component)
        {
            if (component == null) return false;
            Type t = component.GetType();
            while (t != null)
            {
                if (t.FullName == "Unity.Robotics.UrdfImporter.UrdfJoint") return true;
                t = t.BaseType;
            }
            return false;
        }

        private static bool TryGetUrdfJointName(MonoBehaviour component, out string jointName)
        {
            jointName = null;
            if (!IsUrdfJoint(component))
                return false;

            FieldInfo field = component.GetType().GetField("jointName", BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                jointName = field.GetValue(component) as string;
                return true;
            }

            PropertyInfo property = component.GetType().GetProperty("jointName", BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
            {
                jointName = property.GetValue(component) as string;
                return true;
            }

            return false;
        }

        private void CaptureHeldPose()
        {
            // A joint has no position while its articulation is being taken apart (the robot
            // is being destroyed, e.g. a scene unloading during a preview): nothing to hold then.
            foreach (ArticulationBody joint in joints)
                if (joint == null || joint.dofCount == 0) return;

            heldJointPositions.Clear();
            foreach (ArticulationBody joint in joints)
            {
                heldJointPositions.Add(joint.jointPosition[0]);
            }
        }

        private void ApplyHeldPose()
        {
            if (heldJointPositions.Count != joints.Count)
            {
                CaptureHeldPose();
            }

            for (int i = 0; i < joints.Count; i++)
            {
                ApplyJointPosition(joints[i], heldJointPositions[i]);
            }

            ZeroJointVelocities();
        }

        private static Vector3 GetWorldMotionAxis(ArticulationBody joint)
        {
            Vector3 localAxis = joint.anchorRotation * Vector3.right;
            Vector3 worldAxis = joint.transform.TransformDirection(localAxis);
            return worldAxis.sqrMagnitude > 0.000001f ? worldAxis.normalized : joint.transform.right;
        }

        // Tries the step the solver asked for, then the opposite one, and keeps whichever does
        // not move the effector away from the target. Records whether the joint ended against a
        // position limit it was asked to pass.
        private void ApplyBestJointDelta(
            int jointIndex,
            Transform effectorFrame,
            Vector3 localEffectorPoint,
            float deltaRadians,
            Vector3 targetPosition)
        {
            ArticulationBody joint = joints[jointIndex];
            float currentError =
                (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
            float startPosition = joint.jointPosition[0];

            ApplyUserJointPosition(joint, startPosition + deltaRadians, out bool pinned);
            float forwardError =
                (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
            if (forwardError <= currentError)
            {
                pinnedAtLimit[jointIndex] = pinned;
                return;
            }

            ApplyUserJointPosition(joint, startPosition - deltaRadians, out pinned);
            float reverseError =
                (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
            if (reverseError <= currentError)
            {
                pinnedAtLimit[jointIndex] = pinned;
                return;
            }

            ApplyJointPosition(joint, startPosition);
            pinnedAtLimit[jointIndex] = false;
        }

        // A joint write for motion the user drives (IK, jogging). The position limit is
        // applied where every write is (ApplyJointPosition); a result that differs from the
        // request means the request was past a limit. With the profile's velocity limit on, the
        // joint may end at most MaxVelocity x deltaTime from where it stood at the start of
        // the frame; a write cut short that way sets solveRateLimited.
        private void ApplyUserJointPosition(ArticulationBody joint, float requested, out bool pastPositionLimit)
        {
            TryGetLimit(joint, out JointLimit limit);
            if (limits.LimitUserVelocity && limit.MaxVelocity > 0f)
            {
                if (userMotionFrame != Time.frameCount)
                {
                    userMotionFrame = Time.frameCount;
                    userMotionStart.Clear();
                }
                if (!userMotionStart.TryGetValue(joint, out float frameStart))
                    userMotionStart[joint] = frameStart = joint.jointPosition[0];

                float allowed = limit.Clamp(requested);
                float stepped = limit.StepToward(frameStart, requested, Time.deltaTime);
                bool rateLimited = Mathf.Abs(stepped - allowed) > 1e-6f;
                if (rateLimited) solveRateLimited = true;
                ApplyJointPosition(joint, stepped);
                // Still short of the limit, the joint is not pinned there yet.
                pastPositionLimit = !rateLimited && Mathf.Abs(requested - allowed) > 1e-6f;
                return;
            }

            float applied = ApplyJointPosition(joint, requested);
            pastPositionLimit = Mathf.Abs(applied - requested) > 1e-6f;
        }

        private int GetLastAffectingJointIndex(ArticulationBody body)
        {
            if (body == null)
                return -1;

            Transform bodyTransform = body.transform;
            for (int i = joints.Count - 1; i >= 0; i--)
            {
                Transform jointTransform = joints[i].transform;
                if (bodyTransform == jointTransform || bodyTransform.IsChildOf(jointTransform))
                    return i;
            }

            return -1;
        }

        private static Vector3 GetJointPivot(ArticulationBody joint)
        {
            return joint.transform.TransformPoint(joint.anchorPosition);
        }

        // The only way this controller moves a joint. JointWriter clamps the value to the
        // joint's limits, so every path (IK, jogging, joint states, the initial pose, the held
        // pose, the gripper and its mimic followers) is limited here.
        private float ApplyJointPosition(ArticulationBody joint, float position)
        {
            TryGetLimit(joint, out JointLimit limit);
            return JointWriter.ApplyJointPosition(joint, position, limit);
        }

        // The serialised pose when one is set, otherwise the profile's initial pose; without
        // either the robot stays as it was imported.
        private void ApplyInitialPose()
        {
            var report = new JointWriteReport.Builder();
            if (initialPose != null && initialPose.Length > 0)
            {
                foreach (JointInitialPosition jp in initialPose)
                    ApplyNamedJointPosition(jp.name, jp.positionRadians, ref report);
            }
            else
            {
                NamedPose pose = profile != null ? profile.InitialPose : null;
                if (pose?.joints == null) return;
                foreach (JointValue value in pose.joints)
                    if (value != null) ApplyNamedJointPosition(value.joint, value.value, ref report);
            }

            JointWriteReport written = report.Build();
            if (!written.AnyClamped) return;
            var clamped = new List<string>();
            foreach (ClampedJoint c in written.Clamped)
                clamped.Add($"{c.Joint} ({c.Requested:F4} -> {c.Applied:F4})");
            Debug.LogWarning($"[IK] The initial pose is outside the joint limits and was clamped: {string.Join(", ", clamped)}", this);
        }

        private void ApplyNamedJointPosition(string jointName, float position, ref JointWriteReport.Builder report)
        {
            int gripperIndex = string.IsNullOrWhiteSpace(jointName) ? -1 : gripperJointNames.IndexOf(jointName);
            if (gripperIndex >= 0)
            {
                // An explicit finger position (a joint state, a trajectory) overrides an open/close in progress.
                gripperWidthGoal = null;
                SetGripperJointPosition(gripperIndex, position);
                report.Wrote(jointName, position, gripperJointPositions[gripperIndex]);
                return;
            }

            if (string.IsNullOrWhiteSpace(jointName) || !jointByName.TryGetValue(jointName, out ArticulationBody joint))
            {
                if (string.IsNullOrWhiteSpace(jointName)) return;
                report.Unknown(jointName);
                if (warnedMissingJoints.Add(jointName))
                    Debug.LogWarning($"[IK] ApplyNamedJointPosition: joint '{jointName}' not found (known: {string.Join(", ", GetJointStateNames())})");
                return;
            }

            report.Wrote(jointName, position, ApplyJointPosition(joint, position));
        }

        private void ApplyGripperWidth(float width)
        {
            if (gripperDriver >= 0)
            {
                // The described gripper: command the driver joint, the others follow it.
                SetGripperJointPosition(gripperDriver, profile.gripper.DriverAt(width));
                FollowMimics();
                return;
            }

            // Every finger gets the same share, which is what keeps a two-finger hand centred.
            for (int i = 0; i < gripperJoints.Count; i++)
                SetGripperJointPosition(i, width / gripperJoints.Count);
        }

        // Sets one gripper joint; what is remembered as its commanded position is what the
        // limits let through.
        private void SetGripperJointPosition(int index, float position)
        {
            gripperJointPositions[index] = ApplyJointPosition(gripperJoints[index], position);
        }

        private float GripperLimitWidth(bool open)
        {
            if (gripperDriver >= 0)
                return open ? profile.gripper.openWidthMetres : 0f;

            // Without a gripper description the width is the finger joints' travel added up.
            float width = 0f;
            foreach (ArticulationBody joint in gripperJoints)
            {
                if (!TryGetLimit(joint, out JointLimit limit) || !limit.HasLimits) continue;
                width += open ? limit.Upper : limit.Lower;
            }
            return width;
        }

        private float GripperSpeed =>
            gripperDriver >= 0 && profile.gripper.speed > 0f ? profile.gripper.speed : gripperMaxSpeed;

        private void ZeroJointVelocities()
        {
            foreach (ArticulationBody body in allBodies)
            {
                if (body is null) continue;
                SetZeroJointVelocity(body);
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            foreach (ArticulationBody joint in joints)
            {
                SetZeroJointVelocity(joint);
            }
        }

        private static void SetZeroJointVelocity(ArticulationBody body)
        {
            switch (body.dofCount)
            {
                case 1:
                    body.jointVelocity = new ArticulationReducedSpace(0f);
                    break;
                case 2:
                    body.jointVelocity = new ArticulationReducedSpace(0f, 0f);
                    break;
                case 3:
                    body.jointVelocity = new ArticulationReducedSpace(0f, 0f, 0f);
                    break;
            }
        }
    }
}
