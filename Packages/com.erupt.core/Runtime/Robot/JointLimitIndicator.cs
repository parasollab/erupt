using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// Shows a joint at its limit on the robot itself: the link the joint moves is tinted when
    /// the router raises a joint-limit refusal about it, and stays tinted for
    /// <see cref="HoldSeconds"/> after the last one. While the user keeps pushing against the
    /// limit the refusals keep coming (the router lets one through every half second), so the
    /// tint lasts as long as the joint is pinned and a moment longer. Guidelines Part 2:
    /// joint-limit indicators are in-world, on the links.
    /// </summary>
    /// <remarks>
    /// One per robot, on the robot's root; it reacts only to refusals whose subject is under
    /// that object, so several robots each show their own. Driven by
    /// <see cref="InteractionRouter.Refused"/> alone. The tint is the top layer of
    /// <see cref="RendererTint"/> (a property block, so no material is created); whatever the
    /// link shows underneath (a selection highlight, the active-robot tint) comes back when it goes.
    /// </remarks>
    public class JointLimitIndicator : MonoBehaviour
    {
        [Tooltip("Found in the scene if empty.")]
        [SerializeField] private InteractionRouter router;
        [SerializeField] private Color limitColor = new Color(1f, 0.12f, 0.1f, 1f);
        [Tooltip("Seconds the tint stays after the last refusal about the joint. Longer than the router's coalescing window, or the tint would blink during a drag.")]
        [SerializeField, Min(0f)] private float holdSeconds = 1f;

        private sealed class Tint
        {
            public Renderer[] renderers;
            public float until;
        }

        private readonly Dictionary<Transform, Tint> tints = new Dictionary<Transform, Tint>();
        private readonly List<Transform> expired = new List<Transform>();
        private InteractionRouter subscribed;

        public float HoldSeconds
        {
            get => holdSeconds;
            set => holdSeconds = Mathf.Max(0f, value);
        }

        public Color LimitColor => limitColor;

        /// <summary>Whether a link is tinted right now.</summary>
        public bool IsIndicating(Transform link) => link != null && tints.ContainsKey(link);

        /// <summary>How many links are tinted right now.</summary>
        public int ActiveCount => tints.Count;

        /// <summary>Listen to this router's refusals.</summary>
        public void SetRouter(InteractionRouter target)
        {
            router = target;
            if (isActiveAndEnabled) Subscribe();
        }

        private void OnEnable()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ClearAll();
        }

        private void Subscribe()
        {
            if (subscribed == router) return;
            Unsubscribe();
            if (router == null) return;
            router.Refused += OnRefused;
            subscribed = router;
        }

        private void Unsubscribe()
        {
            if (subscribed != null) subscribed.Refused -= OnRefused;
            subscribed = null;
        }

        private static bool IsAboutAJointLimit(RefusalCode code) =>
            code == RefusalCode.JointLimit || code == RefusalCode.LimitMismatch || code == RefusalCode.PlanOutsideLimits;

        private void OnRefused(InteractionRefusal refusal)
        {
            Transform link = refusal.Subject;
            if (link == null || !IsAboutAJointLimit(refusal.Code)) return;
            // Another robot's joint is that robot's indicator's business.
            if (link != transform && !link.IsChildOf(transform)) return;

            if (!tints.TryGetValue(link, out Tint tint))
            {
                tint = Apply(link);
                tints[link] = tint;
            }
            tint.until = Time.unscaledTime + holdSeconds;
        }

        private void Update()
        {
            if (tints.Count == 0) return;

            float now = Time.unscaledTime;
            expired.Clear();
            foreach (KeyValuePair<Transform, Tint> pair in tints)
                if (pair.Key == null || now >= pair.Value.until) expired.Add(pair.Key);
            foreach (Transform link in expired)
            {
                Restore(tints[link]);
                tints.Remove(link);
            }
        }

        private Tint Apply(Transform link)
        {
            Renderer[] renderers = OwnRenderers(link);
            foreach (Renderer renderer in renderers)
                RendererTint.Set(renderer, RendererTint.Layer.JointLimit, limitColor);
            return new Tint { renderers = renderers };
        }

        private static void Restore(Tint tint)
        {
            foreach (Renderer renderer in tint.renderers)
                RendererTint.Clear(renderer, RendererTint.Layer.JointLimit);
        }

        private void ClearAll()
        {
            foreach (Tint tint in tints.Values) Restore(tint);
            tints.Clear();
        }

        // The renderers of this link alone. Links are nested (each is a child of the one before
        // it), so the renderers of the links further down the arm are left out.
        internal static Renderer[] OwnRenderers(Transform link)
        {
            ArticulationBody body = link.GetComponent<ArticulationBody>();
            Renderer[] all = link.GetComponentsInChildren<Renderer>();
            if (body == null) return all;

            var own = new List<Renderer>(all.Length);
            foreach (Renderer renderer in all)
                if (renderer.GetComponentInParent<ArticulationBody>() == body) own.Add(renderer);
            return own.ToArray();
        }
    }
}
