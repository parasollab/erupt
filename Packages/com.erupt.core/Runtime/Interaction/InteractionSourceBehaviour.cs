using System;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// MonoBehaviour base for backends, so the router can discover them in the rig
    /// hierarchy. Backends live in platform-specific assemblies; this base and the
    /// interface it implements are the only things Core knows about them.
    /// </summary>
    public abstract class InteractionSourceBehaviour : MonoBehaviour, IInteractionSource
    {
        public abstract Modality Modality { get; }
        public abstract Capability Capabilities { get; }
        public abstract InteractionSample Current { get; }

        public virtual bool IsActive => isActiveAndEnabled;

        public event Action<InteractionIntent> Raw;

        protected void EmitRaw(InteractionIntent intent) => Raw?.Invoke(intent);

        /// <summary>Convenience for backends that only produce a pose and a kind.</summary>
        protected void EmitRaw(IntentKind kind, InteractionSample sample, Ray ray)
        {
            EmitRaw(new InteractionIntent(kind, sample, ray, false, default, Vector2.zero));
        }

        protected void EmitRawAxis(InteractionSample sample, Ray ray, Vector2 axis)
        {
            EmitRaw(new InteractionIntent(IntentKind.Axis, sample, ray, false, default, axis));
        }
    }
}
