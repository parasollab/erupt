namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Grab transformer for objects that slide on the floor: keeps the height captured at grab
    /// time, drops pitch and roll so only the yaw can change, and optionally clamps the position
    /// to a rectangle on the XZ plane. Place it after the XRGeneralGrabTransformer in the grab
    /// interactable's starting transformer lists; it corrects the pose that transformer produced.
    /// </summary>
    /// <seealso cref="UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable"/>
    public class XRGrabTransformerFloorYaw : XRBaseGrabTransformer
    {
        [SerializeField]
        [Tooltip("Keep the world height the object had when the grab started.")]
        bool m_LockHeight = true;

        [SerializeField]
        [Tooltip("Zero pitch and roll so the object only turns about the world up axis.")]
        bool m_YawOnly = true;

        [SerializeField]
        [Tooltip("Clamp the world x/z position into [boundsMin, boundsMax].")]
        bool m_ClampToBounds;

        [SerializeField]
        [Tooltip("Minimum world x (X) and z (Y) when clamping.")]
        Vector2 m_BoundsMin = new Vector2(-1f, -1f);

        [SerializeField]
        [Tooltip("Maximum world x (X) and z (Y) when clamping.")]
        Vector2 m_BoundsMax = new Vector2(1f, 1f);

        float m_Height;

        public bool lockHeight { get => m_LockHeight; set => m_LockHeight = value; }
        public bool yawOnly { get => m_YawOnly; set => m_YawOnly = value; }
        public bool clampToBounds { get => m_ClampToBounds; set => m_ClampToBounds = value; }
        public Vector2 boundsMin { get => m_BoundsMin; set => m_BoundsMin = value; }
        public Vector2 boundsMax { get => m_BoundsMax; set => m_BoundsMax = value; }

        /// <inheritdoc />
        protected override RegistrationMode registrationMode => RegistrationMode.SingleAndMultiple;

        /// <inheritdoc />
        public override void OnGrab(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable)
        {
            base.OnGrab(grabInteractable);
            m_Height = transform.position.y;
        }

        /// <inheritdoc />
        public override void Process(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable,
                                     XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            Vector3 p = targetPose.position;
            if (m_LockHeight)
                p.y = m_Height;
            if (m_ClampToBounds)
            {
                p.x = Mathf.Clamp(p.x, m_BoundsMin.x, m_BoundsMax.x);
                p.z = Mathf.Clamp(p.z, m_BoundsMin.y, m_BoundsMax.y);
            }
            targetPose.position = p;
            if (m_YawOnly)
                targetPose.rotation = Quaternion.Euler(0f, targetPose.rotation.eulerAngles.y, 0f);
        }
    }
}
