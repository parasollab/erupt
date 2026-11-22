using System.Collections;
using UnityEngine;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Answers a refusal on the controller that caused it: a short double pulse. Sits next to
    /// an input source (the controller backend adds it) and reacts only to refusals attributed
    /// to that source (<see cref="InteractionRefusal.SourceId"/>), so the other controller
    /// stays quiet. A source without <see cref="Capability.Haptics"/> (tracked hands) gets
    /// nothing. Driven by <see cref="InteractionRouter.Refused"/> alone.
    /// </summary>
    public class RefusalHaptics : MonoBehaviour
    {
        [Tooltip("Found in the scene if empty.")]
        [SerializeField] private InteractionRouter router;
        [Tooltip("The input source to pulse. Empty = the one on this object.")]
        [SerializeField] private InteractionSourceBehaviour source;

        [SerializeField, Range(0f, 1f)] private float amplitude = 0.6f;
        [SerializeField, Min(0.01f)] private float pulseSeconds = 0.05f;
        [SerializeField, Min(0f)] private float gapSeconds = 0.07f;

        private InteractionRouter subscribed;
        private Coroutine pulsing;

        /// <summary>Impulses sent since this component was created (two per refusal).</summary>
        public int PulsesSent { get; private set; }

        /// <summary>Listen to this router's refusals.</summary>
        public void SetRouter(InteractionRouter target)
        {
            router = target;
            if (isActiveAndEnabled) Subscribe();
        }

        /// <summary>Pulse this source instead of the one on this object.</summary>
        public void SetSource(InteractionSourceBehaviour target) => source = target;

        private void OnEnable()
        {
            if (source == null) source = GetComponent<InteractionSourceBehaviour>();
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            Subscribe();
        }

        private void OnDisable()
        {
            if (subscribed != null) subscribed.Refused -= OnRefused;
            subscribed = null;
            pulsing = null;
        }

        // The router may not exist yet when a controller is enabled before it.
        private void Start()
        {
            if (subscribed != null) return;
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            Subscribe();
        }

        private void Subscribe()
        {
            if (subscribed == router) return;
            if (subscribed != null) subscribed.Refused -= OnRefused;
            subscribed = router;
            if (subscribed != null) subscribed.Refused += OnRefused;
        }

        private void OnRefused(InteractionRefusal refusal)
        {
            if (source == null || !source.Has(Capability.Haptics) || !(source is IHapticOutput)) return;
            if (string.IsNullOrEmpty(refusal.SourceId) || refusal.SourceId != source.Current.SourceId) return;

            if (pulsing != null) StopCoroutine(pulsing);
            pulsing = StartCoroutine(DoublePulse((IHapticOutput)source));
        }

        private IEnumerator DoublePulse(IHapticOutput output)
        {
            Pulse(output);
            yield return new WaitForSecondsRealtime(pulseSeconds + gapSeconds);
            Pulse(output);
            pulsing = null;
        }

        private void Pulse(IHapticOutput output)
        {
            output.SendHapticImpulse(amplitude, pulseSeconds);
            PulsesSent++;
        }
    }
}
