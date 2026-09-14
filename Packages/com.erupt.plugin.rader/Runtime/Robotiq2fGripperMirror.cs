using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using Erupt.Robot;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Mirrors a pinch onto a Robotiq 2F gripper: the thumb–index distance maps to the master
    /// finger joint's angle, and (unless <see cref="gripperOnly"/>) the pinch midpoint, pushed
    /// back along the gripper length, becomes the end-effector target. RADER's
    /// <c>Robotiq2fGripperMirror</c> re-pointed from <c>RobotManager</c> onto <see cref="IRobotModel"/>
    /// (<c>ApplyJointState</c> for the finger, <c>TrySolveToTarget</c> for the pose); one instance per hand.
    /// </summary>
    /// <remarks>
    /// The finger joint must be part of the robot model's joint set for the angle to take
    /// effect; the controller warns once per unknown joint name. Angles are sent in radians.
    /// </remarks>
    public class Robotiq2fGripperMirror : MonoBehaviour
    {
        [SerializeField] private Handedness hand = Handedness.Right;
        [Tooltip("URDF joint the pinch drives.")]
        [SerializeField] private string masterJoint = "finger_joint";
        [Tooltip("Thumb–index distance (m) that maps to a fully open gripper.")]
        [SerializeField] private float maxFingerDistance = 0.11f;
        [Tooltip("Finger joint angle (degrees) when the pinch is closed.")]
        [SerializeField] private float maxJointAngleDegrees = 45f;
        [Tooltip("Only drive the gripper; leave the end-effector pose alone.")]
        [SerializeField] private bool gripperOnly;
        [Tooltip("Distance from the pinch midpoint back to the wrist frame (m).")]
        [SerializeField] private float gripperLength = 0.1493f;
        [Tooltip("Optional: shown at the pinch pose.")]
        [SerializeField] private Transform pinchIndicator;
        [Tooltip("Optional explicit robot; the RADER plugin assigns the context's robot when this is empty.")]
        [SerializeField] private DirectArticulationIKController robotOverride;

        private IRobotModel robot;
        private XRHandSubsystem handSubsystem;
        private bool interacting;
        private readonly string[] jointName = new string[1];
        private readonly float[] jointValue = new float[1];

        public IRobotModel Robot => robot ?? (robotOverride != null ? robotOverride : null);
        public float LastJointAngleDegrees { get; private set; }

        public void SetRobot(IRobotModel model) => robot = model;

        private void OnEnable()
        {
            if (TryAcquireSubsystem()) handSubsystem.updatedHands += OnHandUpdate;
        }

        private void OnDisable()
        {
            if (handSubsystem != null) handSubsystem.updatedHands -= OnHandUpdate;
            EndInteraction();
        }

        private void Update()
        {
            if (handSubsystem == null && TryAcquireSubsystem()) handSubsystem.updatedHands += OnHandUpdate;
        }

        private bool TryAcquireSubsystem()
        {
            if (handSubsystem != null && handSubsystem.running) return true;
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            handSubsystem = subsystems.Count > 0 ? subsystems[0] : null;
            return handSubsystem != null && handSubsystem.running;
        }

        private void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags,
                                  XRHandSubsystem.UpdateType updateType)
        {
            if (updateType != XRHandSubsystem.UpdateType.BeforeRender) return;
            XRHand xrHand = hand == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
            if (!xrHand.isTracked) { EndInteraction(); return; }

            if (!xrHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTip) ||
                !xrHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTip) ||
                !xrHand.GetJoint(XRHandJointID.ThumbProximal).TryGetPose(out Pose thumbProx) ||
                !xrHand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out Pose indexProx))
                return;

            Mirror(thumbTip.position, indexTip.position, thumbProx.rotation, indexProx.rotation);
        }

        /// <summary>Apply one pinch (world poses). Exposed so tests can drive it without a subsystem.</summary>
        public void Mirror(Vector3 thumbTip, Vector3 indexTip, Quaternion thumbProximal, Quaternion indexProximal)
        {
            var r = Robot;
            if (r == null) return;
            if (!interacting) { r.BeginInteraction(); interacting = true; }

            if (!gripperOnly)
            {
                Vector3 midpoint = (thumbTip + indexTip) / 2f;
                Quaternion rotation = Quaternion.Slerp(thumbProximal, indexProximal, 0.5f)
                                      * Quaternion.Euler(90, 0, 0) * Quaternion.Euler(0, 90, 0);
                midpoint -= rotation * Vector3.up * gripperLength;
                if (pinchIndicator != null) pinchIndicator.SetPositionAndRotation(midpoint, rotation);
                r.TrySolveToTarget(midpoint);
            }

            float distance = Vector3.Distance(thumbTip, indexTip);
            LastJointAngleDegrees = Map(distance, 0f, maxFingerDistance, 0f, maxJointAngleDegrees, invert: true);
            jointName[0] = masterJoint;
            jointValue[0] = LastJointAngleDegrees * Mathf.Deg2Rad;
            r.ApplyJointState(jointName, jointValue);
        }

        private void EndInteraction()
        {
            if (!interacting) return;
            interacting = false;
            Robot?.EndInteraction();
        }

        private static float Map(float value, float fromLow, float fromHigh, float toLow, float toHigh, bool invert)
        {
            float t = fromHigh - fromLow == 0f ? 0f : (value - fromLow) / (fromHigh - fromLow);
            t = Mathf.Clamp01(t);
            return invert ? toHigh - t * (toHigh - toLow) : toLow + t * (toHigh - toLow);
        }
    }
}
