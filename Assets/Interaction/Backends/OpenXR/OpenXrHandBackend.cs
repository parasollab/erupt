using UnityEngine;
using UnityEngine.XR.Hands;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// OpenXR hand-tracking backend. Stub: reports capabilities, pose and real tracking
    /// confidence, and emits pinch as select. Continuous manipulation lands in Phase 2.
    /// </summary>
    /// <remarks>
    /// Confidence is the point of this backend existing now — Guidelines Part 3 requires
    /// every sample to carry it, and hands are the first source where it is not always 1.
    /// </remarks>
    public class OpenXrHandBackend : InteractionSourceBehaviour
    {
        [SerializeField] private Handedness handedness = Handedness.Right;
        [SerializeField] private string sourceId = "right-hand";

        [Tooltip("Pinch distance in metres below which a pinch is considered held.")]
        [SerializeField] private float pinchThreshold = 0.02f;

        private XRHandSubsystem handSubsystem;
        private bool wasPinching;
        private Pose lastPose = Pose.identity;
        private float lastConfidence;

        public override Modality Modality => Modality.Hand;

        public override Capability Capabilities =>
            Capability.Ray | Capability.Pinch | Capability.DirectTouch;

        public override bool IsActive => isActiveAndEnabled && lastConfidence > 0f;

        public override InteractionSample Current =>
            new InteractionSample(Modality.Hand, lastConfidence, Time.unscaledTimeAsDouble, lastPose, sourceId);

        private void Update()
        {
            if (!TryAcquireSubsystem()) return;

            XRHand hand = handedness == Handedness.Left
                ? handSubsystem.leftHand
                : handSubsystem.rightHand;

            if (!hand.isTracked)
            {
                lastConfidence = 0f;
                return;
            }

            if (!TryGetJoint(hand, XRHandJointID.IndexTip, out Pose indexTip) ||
                !TryGetJoint(hand, XRHandJointID.ThumbTip, out Pose thumbTip))
            {
                lastConfidence = 0f;
                return;
            }

            lastPose = indexTip;

            // Nothing finer-grained is exposed by XRHand, so tracked/untracked is the
            // honest confidence signal until per-joint tracking flags are available.
            lastConfidence = 1f;

            bool isPinching = Vector3.Distance(indexTip.position, thumbTip.position) < pinchThreshold;
            var sample = Current;
            var ray = new Ray(indexTip.position, indexTip.rotation * Vector3.forward);

            if (isPinching && !wasPinching) EmitRaw(IntentKind.Select, sample, ray);
            wasPinching = isPinching;
        }

        private bool TryAcquireSubsystem()
        {
            if (handSubsystem != null && handSubsystem.running) return true;

            var subsystems = new System.Collections.Generic.List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            handSubsystem = subsystems.Count > 0 ? subsystems[0] : null;
            return handSubsystem != null && handSubsystem.running;
        }

        private static bool TryGetJoint(XRHand hand, XRHandJointID id, out Pose pose)
        {
            return hand.GetJoint(id).TryGetPose(out pose);
        }
    }
}
