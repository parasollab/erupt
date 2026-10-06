using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// OpenXR hand-tracking backend: what <see cref="XriControllerBackend"/> does with
    /// buttons, done with gestures. Thumb-index pinch selects and, held or moved, drags;
    /// thumb-middle pinch held and raised or lowered is the thumbstick; the Meta menu
    /// gesture (palm toward the face, pinch) is the Menu button.
    /// </summary>
    /// <remarks>
    /// Gesture thresholds live here because they are raw source state; deadzone, precision
    /// scaling and smoothing stay Router policy under the Hand profile, per Guidelines
    /// Part 3. Every sample carries real tracking confidence — hands are the first source
    /// where it is not always 1.
    /// </remarks>
    public class OpenXrHandBackend : InteractionSourceBehaviour
    {
        [Header("Identity")]
        [SerializeField] private Handedness handedness = Handedness.Right;

        [Tooltip("Stable id carried on every sample. Use \"left\" or \"right\".")]
        [SerializeField] private string sourceId = "right";

        [Header("Pose")]
        [Tooltip("Transform the ray originates from: the hand's XRI Aim Pose, so this ray and XRI's agree. Estimated from the joints when empty or not yet driven.")]
        [SerializeField] private Transform rayOrigin;

        [Tooltip("Space the hand joints are reported in (the XR Origin's Camera Offset). Defaults to this transform's parent.")]
        [SerializeField] private Transform trackingSpace;

        [Tooltip("Controller this hand stands in for. The hand yields while it is active, so a held controller is never doubled by the hand around it.")]
        [SerializeField] private Transform controller;

        [Header("Select and drag (thumb + index)")]
        [Tooltip("Pinch distance in metres below which a pinch is considered held.")]
        [SerializeField] private float pinchThreshold = 0.02f;

        [Tooltip("Pinch distance in metres above which a held pinch is released.")]
        [SerializeField] private float pinchReleaseThreshold = 0.035f;

        [Tooltip("Metres a held pinch must travel before it becomes a drag.")]
        [SerializeField] private float dragStartDistance = 0.02f;

        [Tooltip("Seconds after which a held pinch becomes a drag without moving.")]
        [SerializeField] private float dragStartSeconds = 0.3f;

        [Header("Jog (thumb + middle)")]
        [Tooltip("Hold a thumb-middle pinch and raise or lower the hand to emit the axis a thumbstick would.")]
        [SerializeField] private bool jogGesture = true;

        [Tooltip("Hand travel in metres that maps to full axis deflection.")]
        [SerializeField] private float jogRange = 0.1f;

        [Header("Menu")]
        [Tooltip("Emit Activate on the Meta menu gesture. Needs the Meta Hand Tracking Aim OpenXR feature; the runtime reports it on the left hand.")]
        [SerializeField] private bool menuGesture = true;

        [Header("Tracking")]
        [Tooltip("Seconds a pinch survives lost tracking before it is released.")]
        [SerializeField] private float trackingLossGrace = 0.25f;

        private readonly PinchTracker selectPinch = new();
        private readonly PinchTracker jogPinch = new();

        private XRHandSubsystem handSubsystem;
        private InputAction aimFlagsAction;
        private Pose lastPose = Pose.identity;
        private float lastConfidence;
        private double lastTrackedTime;
        private InteractionSample downSample;
        private Ray downRay;
        private Vector3 jogRight;
        private bool wasMenuPressed;

        public override Modality Modality => Modality.Hand;

        public override Capability Capabilities =>
            Capability.Ray | Capability.Pinch | Capability.DirectTouch |
            (jogGesture ? Capability.Thumbstick : Capability.None);

        public override bool IsActive => isActiveAndEnabled && lastConfidence > 0f;

        public override InteractionSample Current =>
            new InteractionSample(Modality.Hand, lastConfidence, Time.unscaledTimeAsDouble, lastPose, sourceId);

        private Transform Space => trackingSpace != null ? trackingSpace : transform.parent;

        private void OnEnable()
        {
            // Resolves to nothing, and reads as no flags, when Meta Hand Tracking Aim is off.
            string hand = handedness == Handedness.Left ? "LeftHand" : "RightHand";
            aimFlagsAction = new InputAction("Meta Aim Flags", InputActionType.Value, $"<MetaAimHand>{{{hand}}}/aimFlags");
            aimFlagsAction.Enable();
            ApplyTuning();
        }

        private void OnValidate() => ApplyTuning();

        private void OnDisable()
        {
            // A disabled backend must not leave an interactable mid-drag.
            ReleaseGestures();
            lastConfidence = 0f;
            wasMenuPressed = false;

            aimFlagsAction?.Dispose();
            aimFlagsAction = null;
        }

        private void Update()
        {
            double now = Time.unscaledTimeAsDouble;

            if (YieldsToController() || !TryReadHand(out HandFrame frame))
            {
                lastConfidence = 0f;

                // Hands drop out for a frame or two all the time; only a real loss ends a drag.
                if (now - lastTrackedTime > trackingLossGrace) ReleaseGestures();
                return;
            }

            lastTrackedTime = now;
            lastConfidence = frame.confidence;
            lastPose = AimPose(frame);

            InteractionSample sample = Current;
            Ray ray = CurrentRay();
            MetaAimFlags flags = ReadAimFlags();

            bool menuPressed = menuGesture && (flags & MetaAimFlags.MenuPressed) != 0;
            if (menuPressed && !wasMenuPressed) EmitRaw(IntentKind.Activate, sample, ray);
            wasMenuPressed = menuPressed;

            // A palm turned to the face is the system and menu gesture; its pinch is not a select.
            if ((flags & MetaAimFlags.SystemGesture) != 0)
            {
                ReleaseGestures();
                return;
            }

            float indexGap = Vector3.Distance(frame.indexTip, frame.thumbTip);
            float middleGap = Vector3.Distance(frame.middleTip, frame.thumbTip);

            // One gesture owns the hand until it is released. A thumb closing on both
            // fingertips at once means the nearer one.
            bool preferJog = jogGesture && middleGap < indexGap;
            if (jogPinch.IsPinching || (!selectPinch.IsPinching && preferJog))
                UpdateJog(middleGap, (frame.middleTip + frame.thumbTip) * 0.5f, now, sample, ray);
            else
                UpdateSelect(indexGap, (frame.indexTip + frame.thumbTip) * 0.5f, now, sample, ray);
        }

        private void UpdateSelect(float gap, Vector3 pinchPoint, double now, InteractionSample sample, Ray ray)
        {
            PinchEvents events = selectPinch.Update(gap, pinchPoint, now);

            if ((events & PinchEvents.Down) != 0)
            {
                downSample = sample;
                downRay = ray;
                EmitRaw(IntentKind.Select, sample, ray);
            }

            // The drag starts where the pinch closed, not where the hand had drifted to by
            // the time it qualified as a drag — that is the target the user aimed at.
            if ((events & PinchEvents.BeginDrag) != 0) EmitRaw(IntentKind.BeginDrag, downSample, downRay);
            if ((events & PinchEvents.Drag) != 0) EmitRaw(IntentKind.Drag, sample, ray);
            if ((events & PinchEvents.EndDrag) != 0) EmitRaw(IntentKind.EndDrag, sample, ray);
        }

        private void UpdateJog(float gap, Vector3 pinchPoint, double now, InteractionSample sample, Ray ray)
        {
            PinchEvents events = jogPinch.Update(gap, pinchPoint, now);

            if ((events & PinchEvents.Down) != 0)
            {
                jogRight = Vector3.Cross(Vector3.up, ray.direction);
                jogRight = jogRight.sqrMagnitude > 1e-6f ? jogRight.normalized : Vector3.right;
            }

            if (!jogPinch.IsPinching || jogRange <= 0f) return;

            // Raw axis: displacement from where the pinch closed. The deadzone is Router policy.
            Vector3 offset = pinchPoint - jogPinch.DownPoint;
            var axis = new Vector2(
                Mathf.Clamp(Vector3.Dot(offset, jogRight) / jogRange, -1f, 1f),
                Mathf.Clamp(offset.y / jogRange, -1f, 1f));

            if (axis != Vector2.zero) EmitRawAxis(sample, ray, axis);
        }

        private void ReleaseGestures()
        {
            if ((selectPinch.Release() & PinchEvents.EndDrag) != 0)
                EmitRaw(IntentKind.EndDrag, Current, CurrentRay());

            jogPinch.Release();
        }

        private void ApplyTuning()
        {
            selectPinch.PressDistance = jogPinch.PressDistance = pinchThreshold;
            selectPinch.ReleaseDistance = jogPinch.ReleaseDistance = Mathf.Max(pinchThreshold, pinchReleaseThreshold);
            selectPinch.DragStartDistance = dragStartDistance;
            selectPinch.DragStartSeconds = dragStartSeconds;
        }

        private bool YieldsToController() => controller != null && controller.gameObject.activeInHierarchy;

        private Ray CurrentRay() => new Ray(lastPose.position, lastPose.rotation * Vector3.forward);

        private MetaAimFlags ReadAimFlags() =>
            aimFlagsAction != null ? (MetaAimFlags)aimFlagsAction.ReadValue<int>() : MetaAimFlags.None;

        // --- Joints ---------------------------------------------------------

        private struct HandFrame
        {
            public Pose wrist;
            public Vector3 indexProximal;
            public Vector3 indexTip;
            public Vector3 middleTip;
            public Vector3 thumbTip;
            public float confidence;
        }

        private bool TryReadHand(out HandFrame frame)
        {
            frame = default;
            if (!HandSubsystemLocator.TryGet(ref handSubsystem)) return false;

            XRHand hand = handedness == Handedness.Left
                ? handSubsystem.leftHand
                : handSubsystem.rightHand;

            if (!hand.isTracked) return false;

            bool highFidelity = true;
            if (!TryGetJoint(hand, XRHandJointID.Wrist, out frame.wrist, ref highFidelity) ||
                !TryGetJoint(hand, XRHandJointID.IndexProximal, out Pose indexProximal, ref highFidelity) ||
                !TryGetJoint(hand, XRHandJointID.IndexTip, out Pose indexTip, ref highFidelity) ||
                !TryGetJoint(hand, XRHandJointID.MiddleTip, out Pose middleTip, ref highFidelity) ||
                !TryGetJoint(hand, XRHandJointID.ThumbTip, out Pose thumbTip, ref highFidelity))
            {
                return false;
            }

            frame.indexProximal = indexProximal.position;
            frame.indexTip = indexTip.position;
            frame.middleTip = middleTip.position;
            frame.thumbTip = thumbTip.position;

            // XRHand exposes no scalar confidence. Joints the runtime is inferring rather
            // than observing — an occluded pinch, typically — are the degraded case.
            frame.confidence = highFidelity ? 1f : 0.5f;
            return true;
        }

        // Joint poses arrive relative to the XR Origin; everything downstream is world space.
        private bool TryGetJoint(XRHand hand, XRHandJointID id, out Pose pose, ref bool highFidelity)
        {
            XRHandJoint joint = hand.GetJoint(id);
            if (!joint.TryGetPose(out pose)) return false;

            if ((joint.trackingState & XRHandJointTrackingState.HighFidelityPose) == 0) highFidelity = false;

            Transform space = Space;
            if (space != null) pose = new Pose(space.TransformPoint(pose.position), space.rotation * pose.rotation);
            return true;
        }

        private Pose AimPose(in HandFrame frame)
        {
            // An Aim Pose still sitting on its parent has never been driven — the hand
            // input features that feed it are off — so fall back to the joints.
            if (rayOrigin != null && rayOrigin.gameObject.activeInHierarchy && rayOrigin.localPosition != Vector3.zero)
                return new Pose(rayOrigin.position, rayOrigin.rotation);

            Vector3 direction = frame.indexProximal - frame.wrist.position;
            if (direction.sqrMagnitude < 1e-6f) return new Pose(frame.indexProximal, frame.wrist.rotation);

            return new Pose(frame.indexProximal, Quaternion.LookRotation(direction, frame.wrist.rotation * Vector3.up));
        }
    }
}
