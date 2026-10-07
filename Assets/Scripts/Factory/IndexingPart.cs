using System.Collections;
using UnityEngine;

namespace Factory
{
    // Rotates in discrete eased steps about a local axis, e.g. a lathe turret,
    // tool-changer carousel, rotary table or pallet changer. Pivot must lie on the axis.
    public class IndexingPart : MonoBehaviour
    {
        [Tooltip("Rotation axis in the part's local space.")]
        public Vector3 axis = Vector3.up;

        [Tooltip("Degrees per index step.")]
        public float stepDegrees = 30f;

        [Tooltip("Seconds for one step.")]
        public float stepTime = 0.6f;

        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public float Angle { get; private set; }
        public bool IsMoving { get; private set; }

        Quaternion restRotation;
        bool captured;

        void Awake() { Capture(); }

        void Capture()
        {
            if (captured) return;
            restRotation = transform.localRotation;
            captured = true;
        }

        public IEnumerator Index(int steps = 1)
        {
            Capture();
            float from = Angle;
            float to = Angle + steps * stepDegrees;
            float duration = Mathf.Max(0.01f, stepTime * Mathf.Abs(steps));
            float t = 0f;
            IsMoving = true;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                Angle = Mathf.Lerp(from, to, ease.Evaluate(Mathf.Clamp01(t)));
                Apply();
                yield return null;
            }
            Angle = to;
            Apply();
            IsMoving = false;
        }

        void Apply()
        {
            transform.localRotation = restRotation * Quaternion.AngleAxis(Angle, axis.normalized);
        }
    }
}
