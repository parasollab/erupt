using System;

namespace Erupt.Interaction
{
    /// <summary>
    /// A backend. The only layer that knows about triggers, pinches, thumbsticks or gaze.
    /// Produces raw source state; applies no policy. Guidelines Part 3.
    /// </summary>
    public interface IInteractionSource
    {
        Modality Modality { get; }
        Capability Capabilities { get; }
        InteractionSample Current { get; }

        /// <summary>True while the source is tracking and should be considered for routing.</summary>
        bool IsActive { get; }

        /// <summary>
        /// Pre-policy intents. Only <see cref="InteractionRouter"/> subscribes — filtering,
        /// precision scaling and target resolution have not been applied yet.
        /// </summary>
        event Action<InteractionIntent> Raw;
    }

    public static class CapabilityExtensions
    {
        public static bool Has(this IInteractionSource source, Capability c) =>
            source != null && (source.Capabilities & c) == c;
    }
}
