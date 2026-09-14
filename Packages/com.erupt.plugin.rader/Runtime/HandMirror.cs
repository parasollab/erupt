using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using Erupt.Robot;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Drives the robot's end effector from one tracked hand: the hand's pose relative to
    /// <see cref="humanReference"/> is re-expressed relative to <see cref="robotReference"/>,
    /// shown on the indicator, and solved with <see cref="IRobotModel.TrySolveToTarget"/>.
    /// RADER's <c>HandMirror</c> re-pointed from <c>RobotManager</c> onto <see cref="IRobotModel"/>;
    /// one instance per hand (RADER paired left/right hands with left/right robots).
    /// </summary>
    /// <remarks>
    /// The core IK is position-only, so the indicator carries the hand's rotation but the
    /// robot follows position. The plugin assigns the robot on registration; a
    /// <see cref="DirectArticulationIKController"/> may be set in the inspector instead.
    /// </remarks>
    public class HandMirror : MonoBehaviour
    {
        [SerializeField] private Handedness hand = Handedness.Right;
        [Tooltip("Frame the human's hand pose is measured in (e.g. the XR origin).")]
        [SerializeField] private Transform humanReference;
        [Tooltip("Frame the mirrored pose is expressed in (e.g. the robot base).")]
        [SerializeField] private Transform robotReference;
        [Tooltip("Shown at the mirrored pose; hidden when the hand is not tracked.")]
        [SerializeField] private Transform endEffectorIndicator;
        [Tooltip("Optional explicit robot; the RADER plugin assigns the context's robot when this is empty.")]
        [SerializeField] private DirectArticulationIKController robotOverride;

        private static readonly Vector3 Hidden = new(0f, -1000f, 0f);

        private IRobotModel robot;
        private XRHandSubsystem handSubsystem;
        private bool interacting;

        public IRobotModel Robot => robot ?? (robotOverride != null ? robotOverride : null);
        public bool IsTracked { get; private set; }

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
            // The subsystem may start after us; attach when it appears.
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
            Mirror(xrHand.isTracked, xrHand.rootPose);
        }

        /// <summary>Apply one hand pose (world). Exposed so tests can drive it without a subsystem.</summary>
        public void Mirror(bool tracked, Pose handPose)
        {
            IsTracked = tracked;
            if (!tracked)
            {
                if (endEffectorIndicator != null) endEffectorIndicator.position = Hidden;
                EndInteraction();
                return;
            }

            Pose target = handPose;
            if (humanReference != null && robotReference != null)
            {
                Vector3 localPos = humanReference.InverseTransformPoint(handPose.position);
                Quaternion localRot = Quaternion.Inverse(humanReference.rotation) * handPose.rotation;
                target = new Pose(robotReference.TransformPoint(localPos), robotReference.rotation * localRot);
            }

            if (endEffectorIndicator != null) endEffectorIndicator.SetPositionAndRotation(target.position, target.rotation);

            var r = Robot;
            if (r == null) return;
            if (!interacting) { r.BeginInteraction(); interacting = true; }
            r.TrySolveToTarget(target.position);
        }

        private void EndInteraction()
        {
            if (!interacting) return;
            interacting = false;
            Robot?.EndInteraction();
        }
    }
}
