using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// One frame of manipulation state. Every manipulation emits these so that LfD
    /// recording, planning scene sync, and study telemetry all read the same stream.
    /// Guidelines Part 3.
    /// </summary>
    public readonly struct InteractionSample
    {
        public readonly Modality Modality;

        /// <summary>Tracking confidence, 0..1. Controllers report 1; hands and gaze vary.</summary>
        public readonly float Confidence;

        /// <summary>Seconds since startup, captured when the backend produced the sample.</summary>
        public readonly double Timestamp;

        public readonly Pose Pose;

        /// <summary>"left", "right", "gaze" or "mouse". Stable for the life of a source.</summary>
        public readonly string SourceId;

        public InteractionSample(Modality modality, float confidence, double timestamp, Pose pose, string sourceId)
        {
            Modality = modality;
            Confidence = Mathf.Clamp01(confidence);
            Timestamp = timestamp;
            Pose = pose;
            SourceId = sourceId;
        }

        public InteractionSample WithPose(Pose pose) =>
            new InteractionSample(Modality, Confidence, Timestamp, pose, SourceId);
    }
}
