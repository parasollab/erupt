using UnityEngine;
using UnityEngine.XR.Hands;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Keeps a wrist-anchored transform — tier 1 — on whichever of the controller or the
    /// tracked hand is in use. Guidelines Part 6: tier 1 is wrist-anchored on OpenXR.
    /// </summary>
    /// <remarks>
    /// The anchor cannot simply be a child of the controller: XRI deactivates the
    /// controller object when the user puts it down for hands, and tier 1 would vanish
    /// with it. This component stays active under the tracking space and moves the anchor
    /// between the controller and itself, following the wrist joint while hands are in use.
    ///
    /// On the controller the anchor sits where a watch face would: behind the controller's
    /// origin, toward the wrist, lifted, leaned back and turned a little toward the body's
    /// midline, so that it faces the user's eyes when the controller is held in front of the
    /// body and points ahead. The controller's own ray
    /// starts at the origin and runs along +Z; everything on the anchor is behind the origin,
    /// so that ray cannot cross it. It is pressed with the other hand's ray or a poke. The
    /// numbers are serialized because they want tuning on a headset.
    /// </remarks>
    public class WristAnchor : MonoBehaviour
    {
        [Tooltip("Transform to keep on the wrist. Tier 1 is parented to it.")]
        [SerializeField] private Transform anchor;

        [Tooltip("Controller the anchor rides while it is active.")]
        [SerializeField] private Transform controller;

        [SerializeField] private Handedness handedness = Handedness.Left;

        [Header("Controller placement, relative to the controller")]
        [Tooltip("Controller axes (its pointing pose): +Z along the ray, +Y up with the thumb on top. The default is behind the origin, toward the wrist, and lifted clear of the thumb. Keep it behind the origin (negative Z), or the controller's own ray crosses what is on the anchor.")]
        [SerializeField] private Vector3 controllerPosition = new(0f, 0.05f, -0.12f);

        [Tooltip("Zero stands a canvas on the anchor upright across the controller, facing back along the ray. The default leans it back 45 degrees and turns it 20 degrees toward the body's midline (the sign is for a left controller; use +20 for a right one), toward the eyes of a user who holds the controller in front of the body.")]
        [SerializeField] private Vector3 controllerEuler = new(45f, -20f, 0f);

        [Tooltip("Scale of the anchor on the controller. Tier 1 is built for arm's length; on the wrist it is this much smaller.")]
        [SerializeField, Min(0.01f)] private float controllerScale = 0.4f;

        [Header("Hand placement, relative to the wrist joint")]
        [Tooltip("Wrist joint axes: +Z toward the fingers, +Y out of the back of the hand. The default lifts the anchor clear of the hand.")]
        [SerializeField] private Vector3 handPosition = new(0f, 0.06f, 0f);

        [Tooltip("Zero keeps the controller's layout: a canvas on the anchor stands across the wrist, facing back along the forearm.")]
        [SerializeField] private Vector3 handEuler = Vector3.zero;

        // Hands drop out for a frame or two all the time; the anchor should not blink with them.
        private const float HideDelay = 0.5f;

        private XRHandSubsystem handSubsystem;
        private XRHandSubsystem subscribedSubsystem;
        private float lastTrackedTime = float.NegativeInfinity;

        private void OnDisable() => Subscribe(null);

        private void Update()
        {
            // The subsystem may start after us; attach when it appears.
            HandSubsystemLocator.TryGet(ref handSubsystem);
            Subscribe(handSubsystem);

            if (anchor == null) return;

            bool onController = controller != null && controller.gameObject.activeInHierarchy;
            if (onController)
                Attach(controller, controllerPosition, Quaternion.Euler(controllerEuler), controllerScale);
            else
                Attach(transform, handPosition, Quaternion.Euler(handEuler), 1f);

            // With neither a controller nor a hand to ride, hide rather than sit at the rig origin.
            bool visible = onController || Time.unscaledTime - lastTrackedTime <= HideDelay;
            if (anchor.gameObject.activeSelf != visible) anchor.gameObject.SetActive(visible);
        }

        // The pose is written every frame, so the offsets can be tuned while the app runs.
        private void Attach(Transform parent, Vector3 localPosition, Quaternion localRotation, float scale)
        {
            if (anchor.parent != parent) anchor.SetParent(parent, false);
            anchor.SetLocalPositionAndRotation(localPosition, localRotation);
            anchor.localScale = Vector3.one * scale;
        }

        /// <summary>Where the anchor sits on the controller, in the controller's space.</summary>
        public Pose ControllerPose
        {
            get => new(controllerPosition, Quaternion.Euler(controllerEuler));
            set
            {
                controllerPosition = value.position;
                controllerEuler = value.rotation.eulerAngles;
            }
        }

        /// <summary>The anchor's scale while it is on the controller.</summary>
        public float ControllerScale
        {
            get => controllerScale;
            set => controllerScale = Mathf.Max(0.01f, value);
        }

        /// <summary>Set what the anchor rides (a scene wires these in the inspector; a test here).</summary>
        public void Configure(Transform anchorTransform, Transform controllerTransform)
        {
            anchor = anchorTransform;
            controller = controllerTransform;
        }

        private void Subscribe(XRHandSubsystem subsystem)
        {
            if (subscribedSubsystem == subsystem) return;

            if (subscribedSubsystem != null) subscribedSubsystem.updatedHands -= OnUpdatedHands;
            subscribedSubsystem = subsystem;
            if (subscribedSubsystem != null) subscribedSubsystem.updatedHands += OnUpdatedHands;
        }

        // Fires in the dynamic update and again just before render, so the anchor does not
        // trail the rendered hand by a frame.
        private void OnUpdatedHands(XRHandSubsystem subsystem,
                                    XRHandSubsystem.UpdateSuccessFlags flags,
                                    XRHandSubsystem.UpdateType updateType)
        {
            XRHand hand = handedness == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
            if (!hand.isTracked || !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wrist)) return;

            // Joint poses are relative to the XR Origin, which is this transform's parent space.
            transform.SetLocalPositionAndRotation(wrist.position, wrist.rotation);
            lastTrackedTime = Time.unscaledTime;
        }
    }
}
