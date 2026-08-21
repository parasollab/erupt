using UnityEngine;

namespace Erupt.Interaction
{
    public enum IntentKind
    {
        Select,
        Deselect,
        BeginDrag,
        Drag,
        EndDrag,
        Axis,
        Activate
    }

    /// <summary>
    /// A resolved, policy-applied interaction. Interactables consume these and never see
    /// device state. Produced by <see cref="InteractionRouter"/>. Guidelines Part 3.
    /// </summary>
    public readonly struct InteractionIntent
    {
        public readonly IntentKind Kind;
        public readonly InteractionSample Sample;
        public readonly Ray Ray;

        /// <summary>Whether <see cref="Hit"/> holds a resolved target.</summary>
        public readonly bool HasHit;
        public readonly RaycastHit Hit;

        /// <summary>Precision-scaled axis value, for <see cref="IntentKind.Axis"/>.</summary>
        public readonly Vector2 Axis;

        public InteractionIntent(IntentKind kind, InteractionSample sample, Ray ray,
                                 bool hasHit, RaycastHit hit, Vector2 axis)
        {
            Kind = kind;
            Sample = sample;
            Ray = ray;
            HasHit = hasHit;
            Hit = hit;
            Axis = axis;
        }

        public GameObject Target => HasHit && Hit.collider != null ? Hit.collider.gameObject : null;

        public InteractionIntent AsKind(IntentKind kind) =>
            new InteractionIntent(kind, Sample, Ray, HasHit, Hit, Axis);
    }
}
