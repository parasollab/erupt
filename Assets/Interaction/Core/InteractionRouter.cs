using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// The only place with interaction policy. Resolves targets, applies filtering and
    /// precision scaling, emits intents, and records telemetry. Guidelines Part 3.
    /// </summary>
    /// <remarks>
    /// Backends produce raw state and know nothing about targets; interactables consume
    /// intents and never see device state. Everything in between happens here.
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    public class InteractionRouter : MonoBehaviour
    {
        [Header("Targeting")]
        [Tooltip("Maximum ray length. 6 m matches the pre-refactor controller ray.")]
        [SerializeField] private float rayLength = 6f;
        [SerializeField] private LayerMask raycastLayers = ~0;

        [Tooltip("Layers treated as UI surfaces and never returned as world targets.")]
        [SerializeField] private LayerMask uiLayers = 0;

        [Header("Filtering (Guidelines Part 3)")]
        [SerializeField] private ModalityFilterProfile[] profiles = ModalityFilterProfile.Defaults();

        [Header("Diagnostics")]
        [SerializeField] private bool logRefusals = true;

        private readonly List<InteractionSourceBehaviour> sources = new();
        private readonly Dictionary<string, OneEuroFilter> filters = new();

        public event Action<InteractionIntent> Select;
        public event Action<InteractionIntent> Deselect;
        public event Action<InteractionIntent> BeginDrag;
        public event Action<InteractionIntent> Drag;
        public event Action<InteractionIntent> EndDrag;
        public event Action<InteractionIntent> Axis;
        public event Action<InteractionIntent> Activate;
        public event Action<InteractionRefusal> Refused;

        /// <summary>Sources currently registered, for capability queries by feature code.</summary>
        public IReadOnlyList<InteractionSourceBehaviour> Sources => sources;

        /// <summary>True if any active source offers the capability. Replaces platform checks.</summary>
        public bool AnySourceHas(Capability c)
        {
            for (int i = 0; i < sources.Count; i++)
                if (sources[i].IsActive && sources[i].Has(c)) return true;
            return false;
        }

        private void Awake()
        {
            // Scene-wide, not GetComponentsInChildren: backends live on the controllers,
            // which are not descendants of the router. Registration must also happen at
            // runtime — a Register() call made by editor tooling populates a non-serialized
            // list and a non-serialized event subscription, so it does not survive play.
            foreach (var source in FindObjectsByType<InteractionSourceBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Register(source);
            }

            if (sources.Count == 0)
                Debug.LogError("InteractionRouter: no interaction sources found — input will not reach features.");
        }

        public void Register(InteractionSourceBehaviour source)
        {
            if (source == null || sources.Contains(source)) return;
            sources.Add(source);
            source.Raw += OnRaw;
        }

        public void Unregister(InteractionSourceBehaviour source)
        {
            if (source == null || !sources.Remove(source)) return;
            source.Raw -= OnRaw;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < sources.Count; i++)
                if (sources[i] != null) sources[i].Raw -= OnRaw;
            sources.Clear();
            InteractionSampleBus.Reset();
        }

        // --- Policy ---------------------------------------------------------

        private void OnRaw(InteractionIntent raw)
        {
            ModalityFilterProfile profile = ProfileFor(raw.Sample.Modality);
            bool blockedByUi = false;

            InteractionIntent routed;
            switch (raw.Kind)
            {
                case IntentKind.Axis:
                    routed = ApplyPrecision(raw, profile);
                    break;

                // Smoothing exists for continuous manipulation. Applying it to a discrete
                // press only adds targeting lag, and makes the drawn ray (which reads the
                // live pose) disagree with the resolved target under fast motion.
                case IntentKind.Drag:
                    routed = ResolveTarget(ApplyFilter(raw, profile), out blockedByUi);
                    break;

                case IntentKind.BeginDrag:
                    ResetFilter(raw.Sample.SourceId);
                    routed = ResolveTarget(raw, out blockedByUi);
                    break;

                default:
                    routed = ResolveTarget(raw, out blockedByUi);
                    break;
            }

            // The controller trigger feeds both XRI UI Press and the world Select intent.
            // A UI hit must consume the world Select. Dispatching it as an empty miss made
            // SelectionManager clear the object before the button callback could edit it.
            if (raw.Kind == IntentKind.Select && blockedByUi) return;

            Dispatch(routed);
        }

        // Jitter smoothing. Behavior is identical across devices; only the parameters
        // differ by modality — Guidelines Part 3.
        private InteractionIntent ApplyFilter(InteractionIntent intent, ModalityFilterProfile profile)
        {
            OneEuroFilter filter = FilterFor(intent.Sample.SourceId, profile);

            Pose pose = intent.Sample.Pose;
            pose.position = filter.Filter(pose.position, intent.Sample.Timestamp);

            var sample = intent.Sample.WithPose(pose);
            var ray = new Ray(pose.position, intent.Ray.direction);
            return new InteractionIntent(intent.Kind, sample, ray, intent.HasHit, intent.Hit, intent.Axis);
        }

        private InteractionIntent ApplyPrecision(InteractionIntent intent, ModalityFilterProfile profile)
        {
            Vector2 axis = intent.Axis;
            if (Mathf.Abs(axis.x) < profile.axisDeadzone) axis.x = 0f;
            if (Mathf.Abs(axis.y) < profile.axisDeadzone) axis.y = 0f;
            axis *= profile.precisionScale;
            return new InteractionIntent(intent.Kind, intent.Sample, intent.Ray, intent.HasHit, intent.Hit, axis);
        }

        // Replaces the raycasting previously done inside the controller script and in
        // SelectionManager.TrySelect. UI surfaces are rejected by layer rather than by
        // matching GameObject names.
        private InteractionIntent ResolveTarget(InteractionIntent intent, out bool blockedByUi)
        {
            bool hasHit = Physics.Raycast(
                intent.Ray, out RaycastHit hit, rayLength, raycastLayers, QueryTriggerInteraction.Collide);

            blockedByUi = hasHit && IsUiSurface(hit.collider.gameObject);
            if (blockedByUi) hasHit = false;

            return new InteractionIntent(intent.Kind, intent.Sample, intent.Ray, hasHit, hit, intent.Axis);
        }

        /// <summary>
        /// Current pointer ray and target for a source. Exposed so ray visuals and hover
        /// affordances do not each re-implement hit-testing — targeting policy stays here.
        /// </summary>
        public bool ResolvePointer(IInteractionSource source, out Ray ray, out RaycastHit hit)
        {
            ray = default;
            hit = default;
            if (source == null || !source.IsActive) return false;

            Pose pose = source.Current.Pose;
            ray = new Ray(pose.position, pose.rotation * Vector3.forward);

            bool hasHit = Physics.Raycast(
                ray, out hit, rayLength, raycastLayers, QueryTriggerInteraction.Collide);

            return hasHit && !IsUiSurface(hit.collider.gameObject);
        }

        /// <summary>Maximum pointer distance, so visuals can draw the unhit case consistently.</summary>
        public float RayLength => rayLength;

        private bool IsUiSurface(GameObject go) => (uiLayers.value & (1 << go.layer)) != 0;

        private void Dispatch(InteractionIntent intent)
        {
            InteractionSampleBus.Publish(intent);

            switch (intent.Kind)
            {
                case IntentKind.Select:    Select?.Invoke(intent);    break;
                case IntentKind.Deselect:  Deselect?.Invoke(intent);  break;
                case IntentKind.BeginDrag: BeginDrag?.Invoke(intent); break;
                case IntentKind.Drag:      Drag?.Invoke(intent);      break;
                case IntentKind.EndDrag:   EndDrag?.Invoke(intent);   break;
                case IntentKind.Axis:      Axis?.Invoke(intent);      break;
                case IntentKind.Activate:  Activate?.Invoke(intent);  break;
            }
        }

        /// <summary>
        /// Report an interaction that could not proceed. Guidelines Part 3 — refusals
        /// carry a user-facing reason. Phase 1 logs and broadcasts; spatial presentation
        /// arrives in Phase 3.
        /// </summary>
        public void ReportRefusal(InteractionRefusal refusal)
        {
            if (!refusal.IsRefused) return;
            InteractionSampleBus.Publish(refusal);
            Refused?.Invoke(refusal);
            if (logRefusals) Debug.Log($"[InteractionRouter] Refused: {refusal.Reason}");
        }

        // A fresh grab must not inherit smoothed history from the previous one.
        private void ResetFilter(string sourceId)
        {
            if (filters.TryGetValue(sourceId ?? "default", out var filter)) filter.Reset();
        }

        private ModalityFilterProfile ProfileFor(Modality modality)
        {
            for (int i = 0; i < profiles.Length; i++)
                if (profiles[i].modality == modality) return profiles[i];
            return new ModalityFilterProfile { modality = modality, minCutoff = 1f, beta = 0f, precisionScale = 1f, axisDeadzone = 0f };
        }

        private OneEuroFilter FilterFor(string sourceId, ModalityFilterProfile profile)
        {
            string key = sourceId ?? "default";
            if (!filters.TryGetValue(key, out var filter))
            {
                filter = new OneEuroFilter(profile.minCutoff, profile.beta);
                filters[key] = filter;
            }
            else
            {
                filter.SetParameters(profile.minCutoff, profile.beta);
            }
            return filter;
        }
    }
}
