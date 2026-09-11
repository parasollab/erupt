using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// 1-Euro filter. Lives in Core because filtering is Router policy, not backend
    /// behavior — Guidelines Part 3: "filter parameters vary by modality; filter
    /// behavior does not".
    /// </summary>
    public sealed class OneEuroFilter
    {
        private readonly float derivativeCutoff;
        private float minCutoff;
        private float beta;

        private bool hasPrevious;
        private Vector3 previousValue;
        private Vector3 previousDerivative;
        private double previousTimestamp;

        public OneEuroFilter(float minCutoff, float beta, float derivativeCutoff = 1f)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.derivativeCutoff = derivativeCutoff;
        }

        public void SetParameters(float newMinCutoff, float newBeta)
        {
            minCutoff = newMinCutoff;
            beta = newBeta;
        }

        public void Reset() => hasPrevious = false;

        public Vector3 Filter(Vector3 value, double timestamp)
        {
            if (!hasPrevious)
            {
                hasPrevious = true;
                previousValue = value;
                previousDerivative = Vector3.zero;
                previousTimestamp = timestamp;
                return value;
            }

            float dt = (float)(timestamp - previousTimestamp);
            if (dt <= 0f) return previousValue;
            previousTimestamp = timestamp;

            Vector3 derivative = (value - previousValue) / dt;
            previousDerivative = Vector3.Lerp(previousDerivative, derivative, Alpha(derivativeCutoff, dt));

            float cutoff = minCutoff + beta * previousDerivative.magnitude;
            previousValue = Vector3.Lerp(previousValue, value, Alpha(cutoff, dt));
            return previousValue;
        }

        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
