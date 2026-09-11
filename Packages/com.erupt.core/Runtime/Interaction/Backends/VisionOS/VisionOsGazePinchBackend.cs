using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// visionOS gaze-pinch backend. Stub — declares the capability shape so feature code
    /// can be written and reviewed against it before the port lands.
    /// </summary>
    /// <remarks>
    /// Deliberately declares Gaze and Pinch but NOT Ray. Guidelines Part 6: raw gaze
    /// direction is not available to apps, so gaze-based selection is supported while
    /// gaze analytics are not. Feature code that needs a ray must degrade, not branch.
    ///
    /// No PolySpatial reference: that is a new third-party dependency and is not added
    /// without approval. Whether ERUPT targets PolySpatial mixed reality or a fully
    /// immersive Metal build is open — see refactor/00-survey.md section 7, C4.
    ///
    /// Excluded from Android so nothing here ships in the Quest build.
    /// </remarks>
    public class VisionOsGazePinchBackend : InteractionSourceBehaviour
    {
        [SerializeField] private string sourceId = "gaze";

        public override Modality Modality => Modality.GazePinch;

        public override Capability Capabilities => Capability.Gaze | Capability.Pinch;

        // Never active until the port supplies real gaze-pinch events. Keeping this false
        // means the stub can sit in a scene without competing with a real backend.
        public override bool IsActive => false;

        public override InteractionSample Current => new InteractionSample(
            Modality.GazePinch, 0f, Time.unscaledTimeAsDouble, Pose.identity, sourceId);
    }
}
