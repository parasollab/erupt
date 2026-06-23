using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class DirectArticulationIKController : MonoBehaviour
{
    [SerializeField] private Transform robotRoot;
    [SerializeField] private Transform endEffector;
    [SerializeField] private int maxIterations = 12;
    [SerializeField] private float positionTolerance = 0.008f;
    [SerializeField] private float maxAngleStepDegrees = 4f;
    [SerializeField] private float solveWeight = 0.85f;

    private readonly string[] ur5eLinkNames =
    {
        "shoulder_link",
        "upper_arm_link",
        "forearm_link",
        "wrist_1_link",
        "wrist_2_link",
        "wrist_3_link"
    };

    private readonly string[] ur5eJointNames =
    {
        "shoulder_pan_joint",
        "shoulder_lift_joint",
        "elbow_joint",
        "wrist_1_joint",
        "wrist_2_joint",
        "wrist_3_joint"
    };

    private readonly List<ArticulationBody> joints = new List<ArticulationBody>();
    private readonly List<string> jointNames = new List<string>();
    private readonly Dictionary<string, ArticulationBody> jointByName = new Dictionary<string, ArticulationBody>();
    private readonly List<ArticulationBody> allBodies = new List<ArticulationBody>();
    private readonly List<float> heldJointPositions = new List<float>();
    private bool isInteracting;

    public Transform EndEffector => endEffector;
    public IReadOnlyList<string> JointNames => jointNames;

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

        ApplyJointPosition(joint, joint.jointPosition[0] + deltaRadians);
        CaptureHeldPose();
        ZeroJointVelocities();
    }

    public string[] GetJointStateNames()
    {
        return jointNames.ToArray();
    }

    public float[] GetJointStatePositions()
    {
        float[] positions = new float[joints.Count];
        for (int i = 0; i < joints.Count; i++)
        {
            positions[i] = UnityToRosJointPosition(joints[i], joints[i].jointPosition[0]);
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

        positionRadians = UnityToRosJointPosition(joint, joint.jointPosition[0]);
        return true;
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
        }
    }

    private void BuildJointChain(Transform robotRoot)
    {
        joints.Clear();
        jointNames.Clear();
        jointByName.Clear();
        if (robotRoot == null)
        {
            return;
        }

        for (int i = 0; i < ur5eLinkNames.Length; i++)
        {
            Transform jointTransform = FindJointTransform(robotRoot, ur5eJointNames[i], ur5eLinkNames[i]);
            if (jointTransform == null)
            {
                continue;
            }

            ArticulationBody articulationBody = jointTransform.GetComponent<ArticulationBody>();
            if (articulationBody != null && articulationBody.jointType == ArticulationJointType.RevoluteJoint)
            {
                AddJoint(articulationBody, ResolveJointName(articulationBody, ur5eJointNames[i]));
            }
        }
    }

    private Transform FindJointTransform(Transform robotRoot, string jointName, string linkName)
    {
        foreach (MonoBehaviour component in robotRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (TryGetUrdfJointName(component, out string candidateJointName) && candidateJointName == jointName)
            {
                return component.transform;
            }
        }

        return robotRoot.FindDescendant(linkName);
    }

    private void AddJoint(ArticulationBody joint, string jointName)
    {
        joints.Add(joint);
        jointNames.Add(jointName);
        jointByName[jointName] = joint;
    }

    private static string ResolveJointName(ArticulationBody joint, string fallbackName)
    {
        foreach (MonoBehaviour component in joint.GetComponents<MonoBehaviour>())
        {
            if (TryGetUrdfJointName(component, out string urdfJointName) &&
                !string.IsNullOrWhiteSpace(urdfJointName))
            {
                string generatedName = joint.transform.parent != null
                    ? joint.transform.parent.name + "_" + joint.name + "_joint"
                    : string.Empty;

                if (urdfJointName == generatedName)
                {
                    return fallbackName;
                }

                return urdfJointName;
            }
        }

        return fallbackName;
    }

    private static bool TryGetUrdfJointName(MonoBehaviour component, out string jointName)
    {
        jointName = null;
        if (component == null || component.GetType().FullName != "Unity.Robotics.UrdfImporter.UrdfJoint")
        {
            return false;
        }

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

        ApplyJointPosition(joint, startPosition + deltaRadians);
        float forwardError = (targetPosition - endEffector.position).sqrMagnitude;
        if (forwardError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, startPosition - deltaRadians);
        float reverseError = (targetPosition - endEffector.position).sqrMagnitude;
        if (reverseError <= currentError)
        {
            return;
        }

        ApplyJointPosition(joint, startPosition);
    }

    private static void ApplyJointPosition(ArticulationBody joint, float positionRadians)
    {
        float nextPosition = ClampJointPosition(joint, positionRadians);
        joint.jointPosition = new ArticulationReducedSpace(nextPosition);
        SetDriveTarget(joint, nextPosition);
        SetZeroJointVelocity(joint);
        joint.PublishTransform();
        Physics.SyncTransforms();
    }

    private void ApplyNamedJointPosition(string jointName, float positionRadians)
    {
        if (string.IsNullOrWhiteSpace(jointName) || !jointByName.TryGetValue(jointName, out ArticulationBody joint))
        {
            return;
        }

        ApplyJointPosition(joint, RosToUnityJointPosition(joint, positionRadians));
    }

    private static void SetDriveTarget(ArticulationBody joint, float positionRadians)
    {
        ArticulationDrive drive = joint.xDrive;
        drive.target = joint.jointType == ArticulationJointType.RevoluteJoint
            ? positionRadians * Mathf.Rad2Deg
            : positionRadians;
        joint.xDrive = drive;
    }

    private static float RosToUnityJointPosition(ArticulationBody joint, float position)
    {
        // URDF revolute axes are imported onto the negative Unity articulation axis.
        return joint.jointType == ArticulationJointType.RevoluteJoint ? -position : position;
    }

    private static float UnityToRosJointPosition(ArticulationBody joint, float position)
    {
        return joint.jointType == ArticulationJointType.RevoluteJoint ? -position : position;
    }

    private static float ClampJointPosition(ArticulationBody joint, float positionRadians)
    {
        if (joint.twistLock != ArticulationDofLock.LimitedMotion)
        {
            return positionRadians;
        }

        ArticulationDrive drive = joint.xDrive;
        return Mathf.Clamp(
            positionRadians,
            drive.lowerLimit * Mathf.Deg2Rad,
            drive.upperLimit * Mathf.Deg2Rad);
    }

    private void ZeroJointVelocities()
    {
        foreach (ArticulationBody body in allBodies)
        {
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
