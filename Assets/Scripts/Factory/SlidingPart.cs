using System.Collections;
using UnityEngine;

namespace Factory
{
    // A machine part that translates between its authored (closed) pose and an open pose,
    // e.g. a sliding enclosure door or a safety gate. The open pose is the authored local
    // position plus openOffset, so the part can be re-posed in the editor without retuning.
    public class SlidingPart : MonoBehaviour
    {
        [Tooltip("Local-space offset of the fully open pose relative to the authored (closed) pose.")]
        public Vector3 openOffset = new Vector3(0.6f, 0f, 0f);

        [Tooltip("Seconds for a full closed-to-open travel.")]
        public float travelTime = 1.5f;

        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Range(0f, 1f)]
        [Tooltip("Current open amount; 0 = closed (authored pose), 1 = open.")]
        public float openAmount;

        public bool IsMoving { get; private set; }

        Vector3 closedPosition;
        bool captured;

        void Awake()
        {
            Capture();
            Apply();
        }

        void Capture()
        {
            if (captured) return;
            closedPosition = transform.localPosition;
            captured = true;
        }

        public void Set(float amount)
        {
            Capture();
            openAmount = Mathf.Clamp01(amount);
            Apply();
        }

        void Apply()
        {
            transform.localPosition = closedPosition + openOffset * ease.Evaluate(openAmount);
        }

        public IEnumerator Open() { yield return MoveTo(1f); }
        public IEnumerator Close() { yield return MoveTo(0f); }

        public IEnumerator MoveTo(float target)
        {
            Capture();
            target = Mathf.Clamp01(target);
            float from = openAmount;
            float duration = Mathf.Max(0.01f, travelTime * Mathf.Abs(target - from));
            float t = 0f;
            IsMoving = true;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                openAmount = Mathf.Lerp(from, target, Mathf.Clamp01(t));
                Apply();
                yield return null;
            }
            openAmount = target;
            Apply();
            IsMoving = false;
        }
    }
}
