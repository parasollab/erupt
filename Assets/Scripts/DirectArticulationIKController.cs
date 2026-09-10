using Erupt.Interaction;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class DirectArticulationIKController : MonoBehaviour
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
    [SerializeField] private JointInitialPosition[] initialPose;

    private readonly HashSet<string> warnedMissingJoints = new HashSet<string>();

    private static readonly string[] DefaultInitialJointNames =
    {
        "fr3_finger_joint1", "fr3_finger_joint2",
        "fr3_joint1", "fr3_joint2", "fr3_joint3",
        "fr3_joint4", "fr3_joint5", "fr3_joint6", "fr3_joint7"
    };

    private static readonly float[] DefaultInitialJointPositions =
    {
        0.0f, 0.0f, 0.0f, -0.785f, 0.0f, -2.356f, 0.0f, 1.571f, 0.785f
    };

    private readonly List<ArticulationBody> joints = new List<ArticulationBody>();
    private readonly List<string> jointNames = new List<string>();
    private readonly Dictionary<string, ArticulationBody> jointByName = new Dictionary<string, ArticulationBody>();
    private readonly List<ArticulationBody> allBodies = new List<ArticulationBody>();
    private readonly List<float> heldJointPositions = new List<float>();
    private bool isInteracting;

    public Transform EndEffector => endEffector;
    public IReadOnlyList<string> JointNames => jointNames;
    public IReadOnlyList<ArticulationBody> Joints => joints;

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

        if (linkByName.TryGetValue(linkName, out var cached) && cached) return cached;

        if (robotRoot != null)
        {
            foreach (Transform t in robotRoot.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(t.name, linkName, StringComparison.OrdinalIgnoreCase))
                {
                    linkByName[linkName] = t;
                    return t;
                }
            }
        }

        Debug.LogWarning($"[IK] FindLinkTransform: no link named '{linkName}' under robot root; using end effector.");
        linkByName[linkName] = endEffector;
        return endEffector;
    }

    private void Awake()
    {
        if (robotRoot != null && endEffector != null)
            Configure(robotRoot, endEffector);
    }

    public void Configure(Transform robotRoot, Transform toolTransform)
    {
        endEffector = toolTransform;
        StabilizeRobot(robotRoot);
        BuildJointChain(robotRoot);
        ApplyInitialPose();
        CaptureHeldPose();
        ZeroJointVelocities();
    }

    public void BeginInteraction()
    {
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
            return InteractionRefusal.Refuse("That joint cannot be used as an IK target.", targetPosition);
        }

        firstJointIndex = Mathf.Clamp(firstJointIndex, 0, selectedIndex);
        if (selectedIndex == firstJointIndex)
            return InteractionRefusal.Refuse(
                "No movable joint exists before this target in the active chain segment.",
                targetPosition);

        SolvePointToTarget(
            selectedJoint.transform,
            selectedJoint.anchorPosition,
            firstJointIndex,
            selectedIndex,
            targetPosition);

        float residual = Vector3.Distance(GetJointPivot(selectedJoint), targetPosition);
        return residual > positionTolerance
            ? InteractionRefusal.Refuse(
                $"{selectedJoint.name} target is out of reach by {residual * 100f:F0} cm.",
                targetPosition)
            : InteractionRefusal.None;
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
                "That robot link is not part of the controlled revolute chain.",
                targetPosition);

        firstJointIndex = Mathf.Clamp(firstJointIndex, 0, effectorIndex);
        if (firstJointIndex == effectorIndex)
            return InteractionRefusal.Refuse(
                "No movable joint remains in this active chain segment.",
                targetPosition);

        SolvePointToTarget(
            body.transform, localGrabPoint, firstJointIndex, effectorIndex, targetPosition);

        Vector3 achieved = body.transform.TransformPoint(localGrabPoint);
        float residual = Vector3.Distance(achieved, targetPosition);
        return residual > positionTolerance
            ? InteractionRefusal.Refuse(
                $"{body.name} grab point is out of reach by {residual * 100f:F0} cm.",
                targetPosition)
            : InteractionRefusal.None;
    }

    private void SolvePointToTarget(
        Transform effectorFrame,
        Vector3 localEffectorPoint,
        int firstJointIndex,
        int jointCount,
        Vector3 targetPosition)
    {
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
                    joint,
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
            return InteractionRefusal.Refuse("Robot is not configured for interaction.", targetPosition);
        }

        SolvePointToTarget(
            endEffector,
            Vector3.zero,
            Mathf.Clamp(firstJointIndex, 0, joints.Count),
            joints.Count,
            targetPosition);

        float residual = Vector3.Distance(endEffector.position, targetPosition);
        if (residual > positionTolerance)
        {
            return InteractionRefusal.Refuse(
                $"Target is out of reach by {residual * 100f:F0} cm.", targetPosition);
        }

        return InteractionRefusal.None;
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

        ApplyJointPosition(joint, ClampJointPosition(joint, joint.jointPosition[0] + deltaRadians));
        CaptureHeldPose();
        ZeroJointVelocities();
    }

    /// <summary>
    /// NudgeJoint, but reporting when the requested angle was clamped away. The clamp in
    /// ClampJointPosition is otherwise silent, which Guidelines Part 3 calls out as the
    /// most common source of novice confusion.
    /// </summary>
    public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians)
    {
        if (!CanControlJoint(joint))
        {
            return InteractionRefusal.Refuse("That joint cannot be driven.", transform.position);
        }

        float requested = joint.jointPosition[0] + deltaRadians;
        float clamped = ClampJointPosition(joint, requested);

        NudgeJoint(joint, deltaRadians);

        if (!Mathf.Approximately(requested, clamped))
        {
            return InteractionRefusal.Refuse(
                $"{joint.name} is at its {(requested > clamped ? "upper" : "lower")} limit.",
                joint.transform.position);
        }

        return InteractionRefusal.None;
    }

    public string[] GetJointStateNames()
    {
        return jointNames.ToArray();
    }

    public void LogJointDriveLimits()
    {
        Debug.Log($"[IK] LogJointDriveLimits: {joints.Count} joints tracked.");
        for (int i = 0; i < joints.Count; i++)
        {
            ArticulationBody j = joints[i];
            ArticulationDrive d = j.xDrive;
            float rosLower = -d.upperLimit * Mathf.Deg2Rad;
            float rosUpper = -d.lowerLimit * Mathf.Deg2Rad;
            Debug.Log($"[IK] {jointNames[i]} ({j.name}): Unity [{d.lowerLimit:F2}, {d.upperLimit:F2}] deg | ROS [{rosLower:F4}, {rosUpper:F4}] rad | stiffness={d.stiffness} twistLock={j.twistLock}");
        }
    }

    public float[] GetJointStatePositions()
    {
        float[] positions = new float[joints.Count];
        for (int i = 0; i < joints.Count; i++)
        {
            positions[i] = ClampedRosPosition(joints[i]);
        }

        return positions;
    }

    public bool TryGetJointAngle(string jointName, out float positionRadians)
    {
        positionRadians = 0f;
        if (!jointByName.TryGetValue(jointName, out ArticulationBody joint))
        {
            return false;
        }

        positionRadians = ClampedRosPosition(joint);
        return true;
    }

    // Reads the current joint position as a ROS-convention radian value.
    private static float ClampedRosPosition(ArticulationBody joint)
    {
        float rosPos = joint.jointPosition[0];
        if (joint.jointType != ArticulationJointType.RevoluteJoint ||
            joint.twistLock != ArticulationDofLock.LimitedMotion)
            return rosPos;

        ArticulationDrive drive = joint.xDrive;
        float rosLower = -drive.upperLimit * Mathf.Deg2Rad;
        float rosUpper = -drive.lowerLimit * Mathf.Deg2Rad;
        if (rosPos < rosLower - 0.001f || rosPos > rosUpper + 0.001f)
            Debug.LogWarning($"[IK] {joint.name} ROS pos {rosPos:F4} is outside URDF bounds [{rosLower:F4}, {rosUpper:F4}]");
        return rosPos;
    }

    public void ApplyJointState(string[] names, double[] positions)
    {
        if (names == null || positions == null)
        {
            return;
        }

        int count = Mathf.Min(names.Length, positions.Length);
        for (int i = 0; i < count; i++)
        {
            ApplyNamedJointPosition(names[i], (float)positions[i]);
        }

        CaptureHeldPose();
        ZeroJointVelocities();
    }

    public void ApplyJointState(IList<string> names, IList<float> positions)
    {
        if (names == null || positions == null)
        {
            return;
        }

        int count = Mathf.Min(names.Count, positions.Count);
        for (int i = 0; i < count; i++)
        {
            ApplyNamedJointPosition(names[i], positions[i]);
        }

        CaptureHeldPose();
        ZeroJointVelocities();
    }

    private void FixedUpdate()
    {
        if (!isInteracting)
        {
            ApplyHeldPose();
        }
    }

    private void StabilizeRobot(Transform robotRoot)
    {
        allBodies.Clear();
        if (robotRoot == null)
        {
            return;
        }

        allBodies.AddRange(robotRoot.GetComponentsInChildren<ArticulationBody>());
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

            if (body.jointType == ArticulationJointType.RevoluteJoint)
                body.twistLock = ArticulationDofLock.FreeMotion;
        }
    }

    private void BuildJointChain(Transform robotRoot)
    {
        joints.Clear();
        jointNames.Clear();
        jointByName.Clear();
        if (robotRoot == null)
            return;

        foreach (MonoBehaviour component in robotRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!IsUrdfJoint(component))
                continue;

            ArticulationBody body = component.GetComponent<ArticulationBody>();
            if (body == null || body.jointType != ArticulationJointType.RevoluteJoint)
                continue;

            bool gotName = TryGetUrdfJointName(component, out string name);
            string resolvedName = !string.IsNullOrWhiteSpace(name) ? name : component.name;
            if (!gotName || string.IsNullOrWhiteSpace(name))
                Debug.LogWarning($"[IK] BuildJointChain: could not read joint name from UrdfJoint on '{component.name}', falling back to GameObject name '{resolvedName}'");
            AddJoint(body, resolvedName);
        }

        var sb = new System.Text.StringBuilder("[IK] BuildJointChain discovered joints:");
        for (int i = 0; i < jointNames.Count; i++)
            sb.Append($"\n  [{i}] name='{jointNames[i]}' go='{joints[i].name}'");
        Debug.Log(sb.ToString());

        // Fallback for robots not imported via the URDF importer.
        if (joints.Count == 0)
        {
            foreach (ArticulationBody body in robotRoot.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (body.jointType == ArticulationJointType.RevoluteJoint)
                    AddJoint(body, body.name);
            }
        }
    }

    private void AddJoint(ArticulationBody joint, string jointName)
    {
        joints.Add(joint);
        jointNames.Add(jointName);
        jointByName[jointName] = joint;
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

    private void ApplyBestJointDelta(
        ArticulationBody joint,
        Transform effectorFrame,
        Vector3 localEffectorPoint,
        float deltaRadians,
        Vector3 targetPosition)
    {
        float currentError =
            (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
        float startPosition = joint.jointPosition[0];

        ApplyJointPosition(joint, ClampJointPosition(joint, startPosition + deltaRadians));
        float forwardError =
            (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
        if (forwardError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, ClampJointPosition(joint, startPosition - deltaRadians));
        float reverseError =
            (targetPosition - effectorFrame.TransformPoint(localEffectorPoint)).sqrMagnitude;
        if (reverseError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, startPosition);
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

    private static float ClampJointPosition(ArticulationBody joint, float positionRadians)
    {
        ArticulationDrive drive = joint.xDrive;
        return Mathf.Clamp(positionRadians, drive.lowerLimit * Mathf.Deg2Rad, drive.upperLimit * Mathf.Deg2Rad);
    }

    private static void ApplyJointPosition(ArticulationBody joint, float positionRadians)
    {
        joint.jointPosition = new ArticulationReducedSpace(positionRadians);
        SetDriveTarget(joint, positionRadians);
        SetZeroJointVelocity(joint);
        joint.PublishTransform();
        Physics.SyncTransforms();
    }

    private void ApplyInitialPose()
    {
        if (initialPose != null && initialPose.Length > 0)
        {
            foreach (JointInitialPosition jp in initialPose)
                ApplyNamedJointPosition(jp.name, jp.positionRadians);
        }
        else
        {
            for (int i = 0; i < DefaultInitialJointNames.Length; i++)
                ApplyNamedJointPosition(DefaultInitialJointNames[i], DefaultInitialJointPositions[i]);
        }
    }

    private void ApplyNamedJointPosition(string jointName, float positionRadians)
    {
        if (string.IsNullOrWhiteSpace(jointName) || !jointByName.TryGetValue(jointName, out ArticulationBody joint))
        {
            if (!string.IsNullOrWhiteSpace(jointName) && warnedMissingJoints.Add(jointName))
                Debug.LogWarning($"[IK] ApplyNamedJointPosition: joint '{jointName}' not found (known: {string.Join(", ", jointNames)})");
            return;
        }

        ApplyJointPosition(joint, positionRadians);
    }

    private static void SetDriveTarget(ArticulationBody joint, float positionRadians)
    {
        ArticulationDrive drive = joint.xDrive;
        drive.target = joint.jointType == ArticulationJointType.RevoluteJoint
            ? positionRadians * Mathf.Rad2Deg
            : positionRadians;
        joint.xDrive = drive;
    }


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
