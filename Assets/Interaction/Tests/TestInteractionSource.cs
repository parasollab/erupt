using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    /// <summary>Scriptable stand-in for a backend, so router policy can be tested headless.</summary>
    public class TestInteractionSource : InteractionSourceBehaviour
    {
        public string id = "fake";

        public override Modality Modality => Modality.Controller;
        public override Capability Capabilities => Capability.Ray | Capability.Grip | Capability.Thumbstick;
        public override bool IsActive => true;
        public override InteractionSample Current =>
            new InteractionSample(Modality.Controller, 1f, 0.0, Pose.identity, id);

        public void EmitAxis(Vector2 axis, Modality modality)
        {
            var sample = new InteractionSample(modality, 1f, Time.realtimeSinceStartupAsDouble, Pose.identity, id);
            EmitRaw(new InteractionIntent(IntentKind.Axis, sample, default, false, default, axis));
        }

        public void EmitSelect(Modality modality, float confidence)
        {
            var sample = new InteractionSample(modality, confidence, Time.realtimeSinceStartupAsDouble, Pose.identity, id);
            EmitRaw(new InteractionIntent(IntentKind.Select, sample, default, false, default, Vector2.zero));
        }

        public void EmitAt(IntentKind kind, Vector3 position, Modality modality, double timestamp)
        {
            var pose = new Pose(position, Quaternion.identity);
            var sample = new InteractionSample(modality, 1f, timestamp, pose, id);
            EmitRaw(new InteractionIntent(kind, sample, new Ray(position, Vector3.forward), false, default, Vector2.zero));
        }
    }
}
