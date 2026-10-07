using System;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Filter and precision parameters for one modality. Guidelines Part 3 gives the
    /// starting points: controller 1.0/0.007, hands 0.6/0.020, gaze-pinch 0.5/0.030.
    /// </summary>
    [Serializable]
    public struct ModalityFilterProfile
    {
        public Modality modality;
        public float minCutoff;
        public float beta;

        [Tooltip("Multiplier applied to axis input before it reaches interactables.")]
        public float precisionScale;

        [Tooltip("Axis magnitudes below this are treated as zero.")]
        public float axisDeadzone;

        public static ModalityFilterProfile[] Defaults() => new[]
        {
            // Deadzone 0.18 matches the value Quest3ControllerRayInteractor applied
            // in-backend, so the migrated controller path feels identical.
            new ModalityFilterProfile { modality = Modality.Controller, minCutoff = 1.0f,  beta = 0.007f, precisionScale = 1f,    axisDeadzone = 0.18f },
            new ModalityFilterProfile { modality = Modality.Hand,       minCutoff = 0.6f,  beta = 0.020f, precisionScale = 0.75f, axisDeadzone = 0.20f },
            new ModalityFilterProfile { modality = Modality.GazePinch,  minCutoff = 0.5f,  beta = 0.030f, precisionScale = 0.6f,  axisDeadzone = 0.25f },
            new ModalityFilterProfile { modality = Modality.Mouse,      minCutoff = 1.0f,  beta = 0.000f, precisionScale = 1f,    axisDeadzone = 0.05f }
        };
    }
}
