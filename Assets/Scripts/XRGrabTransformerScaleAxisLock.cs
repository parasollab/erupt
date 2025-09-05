namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Scale transformer that restricts scaling on a per axis basis.
    /// </summary>
    /// <seealso cref="XRGrabInteractable"/>
    public class XRGrabTransformerScaleAxisLock : XRBaseGrabTransformer
    {
        [SerializeField]
        [Tooltip("X axis is ignored when scaling object.")]
        bool m_FreezeXScale = false;

        /// <summary>
        /// X axis is ignored when scaling object.
        /// </summary>
        public bool freezeXScale
        {
            get => m_FreezeXScale;
            set => m_FreezeXScale = value;
        }

        [SerializeField]
        [Tooltip("Y axis is ignored when scaling object.")]
        bool m_FreezeYScale = false;

        /// <summary>
        /// X axis is ignored when scaling object.
        /// </summary>
        public bool freezeYScale
        {
            get => m_FreezeYScale;
            set => m_FreezeYScale = value;
        }

        [SerializeField]
        [Tooltip("X axis is ignored when scaling object.")]
        bool m_FreezeZScale = false;

        /// <summary>
        /// X axis is ignored when scaling object.
        /// </summary>
        public bool freezeZScale
        {
            get => m_FreezeZScale;
            set => m_FreezeZScale = value;
        }

        private Vector3 m_InitialScale;

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        protected void Awake()
        {
            m_InitialScale = transform.localScale;
        }

        /// <inheritdoc />
        public override void Process(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
                localScale.x = m_FreezeXScale ? m_InitialScale.x : localScale.x;
                localScale.y = m_FreezeYScale ? m_InitialScale.y : localScale.y;
                localScale.z = m_FreezeZScale ? m_InitialScale.z : localScale.z;
        }
    }
}