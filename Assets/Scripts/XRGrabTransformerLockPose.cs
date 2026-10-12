namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Grab transformer that keeps the object's rotation fixed while it is held.
    /// </summary>
    /// <seealso cref="UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable"/>
    public class XRGrabTransformerLockPose : XRBaseGrabTransformer
    {
        [SerializeField]
        [Tooltip("Keep the object's rotation while it is grabbed; controller twist and joystick rotation are ignored.")]
        bool m_freezePose = true;

        /// <summary>
        /// Rotation is ignored while the object is grabbed.
        /// </summary>
        public bool freezePose
        {
            get => m_freezePose;
            set => m_freezePose = value;
        }

        // World rotation the grab keeps. Captured when the grab starts, not only at Awake:
        // runtime spawners add this component and rotate the object into place afterwards
        // (factory parts are spawned upright in the planning frame), so an Awake-only capture
        // snapped every part back to its spawn rotation the moment it was picked up.
        private Quaternion m_InitialRotation;

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        protected void Awake()
        {
            m_InitialRotation = transform.rotation;
        }

        /// <summary>
        /// Re-syncs the stored rotation to the current transform rotation. Call this after a
        /// code-driven rotation change while the object is held (e.g. snap-to-surface) so the
        /// rest of the grab keeps the new orientation.
        /// </summary>
        public void SyncInitialRotation()
        {
            m_InitialRotation = transform.rotation;
        }

        /// <inheritdoc />
        public override void OnGrab(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable)
        {
            base.OnGrab(grabInteractable);
            SyncInitialRotation();
        }

        /// <inheritdoc />
        public override void Process(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            if (m_freezePose)
                targetPose.rotation = m_InitialRotation;
        }
    }
}
