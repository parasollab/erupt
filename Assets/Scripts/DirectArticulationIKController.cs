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
        if (endEffector == null || joints.Count == 0)
        {
            return;
        }

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            Vector3 error = targetPosition - endEffector.position;
            if (error.sqrMagnitude <= positionTolerance * positionTolerance)
            {
                break;
            }

            for (int i = joints.Count - 1; i >= 0; i--)
            {
                ArticulationBody joint = joints[i];
                Vector3 jointPosition = joint.transform.position;
                Vector3 toEnd = endEffector.position - jointPosition;
                Vector3 toTarget = targetPosition - jointPosition;
                if (toEnd.sqrMagnitude < 0.000001f || toTarget.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                Vector3 axis = GetWorldMotionAxis(joint);
                float deltaDegrees = Vector3.SignedAngle(toEnd, toTarget, axis);
                deltaDegrees = Mathf.Clamp(deltaDegrees * solveWeight, -maxAngleStepDegrees, maxAngleStepDegrees);
                ApplyBestJointDelta(joint, deltaDegrees * Mathf.Deg2Rad, targetPosition);
            }
        }

        CaptureHeldPose();
        ZeroJointVelocities();
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

    private void ApplyBestJointDelta(ArticulationBody joint, float deltaRadians, Vector3 targetPosition)
    {
        float currentError = (targetPosition - endEffector.position).sqrMagnitude;
        float startPosition = joint.jointPosition[0];

        ApplyJointPosition(joint, ClampJointPosition(joint, startPosition + deltaRadians));
        float forwardError = (targetPosition - endEffector.position).sqrMagnitude;
        if (forwardError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, ClampJointPosition(joint, startPosition - deltaRadians));
        float reverseError = (targetPosition - endEffector.position).sqrMagnitude;
        if (reverseError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, startPosition);
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
