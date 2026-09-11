using UnityEngine;

namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Two-handed grab transformer that scales an object based on the frame-to-frame
    /// change in the minimum distance between the two controller forward rays.
    /// Angling controllers toward each other (even without moving the grips) will
    /// shrink the object; angling them away will grow it.
    /// </summary>
    public class XRTwoHandedScaleTransformer : XRBaseGrabTransformer
    {
        [Tooltip("Minimum allowed local scale per axis")]
        public float minScale = 0.01f;

        // Below this distance (meters), scale updates are frozen to avoid
        // explosive ratios when controller rays are nearly intersecting.
        private const float MinDist = 0.01f;

        private bool _initialized = false;
        private float _prevDist;

        public override void Process(
            UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grab,
            XRInteractionUpdateOrder.UpdatePhase phase,
            ref Pose targetPose,
            ref Vector3 localScale)
        {
            if (phase != XRInteractionUpdateOrder.UpdatePhase.Dynamic)
                return;

            var interactors = grab.interactorsSelecting;
            if (interactors.Count < 2)
            {
                _initialized = false;
                return;
            }

            // Use the interactor's own transform (not attach transform) to get the
            // live controller position and pointing direction.
            Transform tfA = interactors[0].transform;
            Transform tfB = interactors[1].transform;
            float currentDist = RayClosestDistance(
                tfA.position, tfA.forward,
                tfB.position, tfB.forward);

            if (!_initialized)
            {
                _prevDist = currentDist;
                _initialized = true;
                return;
            }

            // Only update when both distances are above the minimum threshold
            if (_prevDist >= MinDist && currentDist >= MinDist)
            {
                float ratio = currentDist / _prevDist;
                localScale = new Vector3(
                    Mathf.Max(minScale, localScale.x * ratio),
                    Mathf.Max(minScale, localScale.y * ratio),
                    Mathf.Max(minScale, localScale.z * ratio)
                );
            }

            _prevDist = currentDist;
        }

        /// <summary>
        /// Minimum distance between two rays (p1+s*d1, s>=0) and (p2+t*d2, t>=0).
        /// d1 and d2 must be unit vectors.
        /// </summary>
        private static float RayClosestDistance(Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2)
        {
            Vector3 w = p1 - p2;
            float b = Vector3.Dot(d1, d2);
            float d = Vector3.Dot(d1, w);
            float e = Vector3.Dot(d2, w);
            float denom = 1f - b * b;

            float s, t;
            if (denom < 1e-6f) // parallel or anti-parallel
            {
                s = 0f;
                t = Mathf.Max(0f, e);
            }
            else
            {
                s = (b * e - d) / denom;
                t = (e - b * d) / denom;
                if (s < 0f)
                {
                    s = 0f;
                    t = Mathf.Max(0f, e);
                }
                else if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Max(0f, -d);
                }
            }

            return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
        }
    }
}
