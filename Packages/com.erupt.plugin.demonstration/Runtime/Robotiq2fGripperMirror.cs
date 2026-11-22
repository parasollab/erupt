using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using Erupt.Robot;

namespace Erupt.Plugins.Demonstration
{
    /// <summary>
    /// Mirrors a pinch onto a Robotiq 2F gripper: the thumb–index distance maps to the gripper
    /// driver joint's angle, and (unless <see cref="gripperOnly"/>) the pinch midpoint, pushed
    /// back along the gripper length, becomes the end-effector target. Both go through
    /// <see cref="IRobotModel"/> (<c>ApplyJointState</c> for the finger, <c>TrySolveToTarget</c>
    /// for the pose); one instance per hand.
    /// </summary>
    /// <remarks>
    /// Which joint is driven and between which angles comes from the gripper description in the
    /// robot's profile (<see cref="GripperSpec"/>: driver joint, open and closed positions). A
    /// robot without one is not driven; a warning says so once. Angles are sent in radians.
    /// </remarks>
    public class Robotiq2fGripperMirror : MonoBehaviour
    {
        [SerializeField] private Handedness hand = Handedness.Right;
        [Tooltip("Thumb–index distance (m) that maps to a fully open gripper.")]
        [SerializeField] private float maxFingerDistance = 0.11f;
        [Tooltip("Only drive the gripper; leave the end-effector pose alone.")]
        [SerializeField] private bool gripperOnly;
        [Tooltip("Distance from the pinch midpoint back to the wrist frame (m).")]
        [SerializeField] private float gripperLength = 0.1493f;
        [Tooltip("Optional: shown at the pinch pose.")]
        [SerializeField] private Transform pinchIndicator;
        [Tooltip("Optional explicit robot; the demonstration plugin assigns the active robot when this is empty.")]
        [SerializeField] private RobotInstance robotOverride;

        private RobotInstance robot;
        private bool warnedNoGripper;
        private XRHandSubsystem handSubsystem;
        private bool interacting;
        private readonly string[] jointName = new string[1];
        private readonly float[] jointValue = new float[1];

        /// <summary>The robot being driven: the one assigned in the inspector, else the one the plugin handed over.</summary>
        public RobotInstance Target => robotOverride != null ? robotOverride : robot;
        public IRobotModel Robot => Target != null ? Target.Model : null;
        public float LastJointAngleDegrees { get; private set; }

        /// <summary>The robot to drive when none is assigned in the inspector.</summary>
        public void SetRobot(RobotInstance target) => robot = target;

        /// <summary>Assign the robot explicitly (what the inspector field does); it wins over <see cref="SetRobot"/>.</summary>
        public void SetRobotOverride(RobotInstance target) => robotOverride = target;

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

            GripperSpec gripper = Target.Profile != null ? Target.Profile.gripper : null;
            if (gripper == null || !gripper.IsSet)
            {
                if (!warnedNoGripper)
                {
                    warnedNoGripper = true;
                    Debug.LogWarning($"[Robotiq2fGripperMirror] Robot '{Target.Id}' has no gripper description in its profile; the pinch does not drive a finger joint.", this);
                }
                return;
            }

            // A closed pinch is the closed position, a pinch as wide as maxFingerDistance the open one.
            float distance = Vector3.Distance(thumbTip, indexTip);
            float t = maxFingerDistance > 0f ? Mathf.Clamp01(distance / maxFingerDistance) : 0f;
            jointName[0] = gripper.driverJoint;
            jointValue[0] = Mathf.Lerp(gripper.closed, gripper.open, t);
            LastJointAngleDegrees = jointValue[0] * Mathf.Rad2Deg;
            r.ApplyJointState(jointName, jointValue);
        }

        private void EndInteraction()
        {
            if (!interacting) return;
            interacting = false;
            Robot?.EndInteraction();
        }
    }
}
