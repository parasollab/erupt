using System;

namespace Erupt.Interaction
{
    /// <summary>
    /// The single sink every manipulation sample flows through. LfD recording, planning
    /// scene sync and study telemetry all subscribe here rather than tapping input
    /// directly. Guidelines Part 3.
    /// </summary>
    /// <remarks>
    /// Static because subscribers outlive any one rig and are spread across scenes.
    /// <see cref="Reset"/> must be called on scene teardown or handlers leak across loads.
    /// </remarks>
    public static class InteractionSampleBus
    {
        /// <summary>Every routed manipulation frame, with modality and confidence.</summary>
        public static event Action<InteractionSample> Sample;

        /// <summary>Every intent the router emitted, after policy.</summary>
        public static event Action<InteractionIntent> Intent;

        /// <summary>Every refusal an interactable reported. Guidelines Part 3.</summary>
        public static event Action<InteractionRefusal> Refusal;

        public static void Publish(InteractionSample sample) => Sample?.Invoke(sample);

        public static void Publish(InteractionIntent intent)
        {
            Intent?.Invoke(intent);
            Sample?.Invoke(intent.Sample);
        }

        public static void Publish(InteractionRefusal refusal)
        {
            if (refusal.IsRefused) Refusal?.Invoke(refusal);
        }

        /// <summary>
        /// Drop subscribers left over from a previous play session, for the same reason
        /// RosBus resets: static event handlers survive a domain-reload-free Play and
        /// would otherwise fire into destroyed objects.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => Reset();

        /// <summary>Drop all subscribers. Called by the router on destroy.</summary>
        public static void Reset()
        {
            Sample = null;
            Intent = null;
            Refusal = null;
        }
    }
}
