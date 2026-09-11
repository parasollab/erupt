namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Scale transformer that restricts scaling on a per axis basis.
    /// </summary>
    /// <seealso cref="XRGrabInteractable"/>
    public class XRGrabTransformerLockPose : XRBaseGrabTransformer
    {
        [SerializeField]
        [Tooltip("Lock pose when scaling object.")]
        bool m_freezePose = true;

        /// <summary>
        /// Rotate object is ignored when scaling object.
        /// </summary>
        public bool freezePose
        {
            get => m_freezePose;
            set => m_freezePose = value;
        }

        private Quaternion m_InitialRotation;

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        protected void Awake()
        {
            m_InitialRotation = transform.localRotation;
        }

        /// <summary>
        /// Re-syncs the stored initial rotation to the current transform rotation.
        /// Call this after any code-driven rotation change (e.g. snap-to-surface)
        /// so that subsequent grabs do not revert the object to its old orientation.
        /// </summary>
        public void SyncInitialRotation()
        {
            m_InitialRotation = transform.localRotation;
        }

        /// <inheritdoc />
        public override void Process(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            if (m_freezePose)
                targetPose.rotation = m_InitialRotation;
        }
    }
}