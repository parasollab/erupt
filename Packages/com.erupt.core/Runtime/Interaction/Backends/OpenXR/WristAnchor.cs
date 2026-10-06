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
    /// </remarks>
    public class WristAnchor : MonoBehaviour
    {
        [Tooltip("Transform to keep on the wrist. Tier 1 is parented to it.")]
        [SerializeField] private Transform anchor;

        [Tooltip("Controller the anchor rides while it is active.")]
        [SerializeField] private Transform controller;

        [SerializeField] private Handedness handedness = Handedness.Left;

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
                Attach(controller, Vector3.zero, Quaternion.identity);
            else
                Attach(transform, handPosition, Quaternion.Euler(handEuler));

            // With neither a controller nor a hand to ride, hide rather than sit at the rig origin.
            bool visible = onController || Time.unscaledTime - lastTrackedTime <= HideDelay;
            if (anchor.gameObject.activeSelf != visible) anchor.gameObject.SetActive(visible);
        }

        private void Attach(Transform parent, Vector3 localPosition, Quaternion localRotation)
        {
            if (anchor.parent == parent) return;

            anchor.SetParent(parent, false);
            anchor.SetLocalPositionAndRotation(localPosition, localRotation);
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
