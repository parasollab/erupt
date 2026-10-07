using System.Collections;
using UnityEngine;

namespace Factory
{
    // A machine axis (or a stack of axes) that moves to local-space offsets from its authored
    // home pose with eased motion at a given speed, e.g. a mill table, lathe cross slide,
    // spindle head or injection-moulding platen.
    public class LinearAxis : MonoBehaviour
    {
        [Tooltip("Default travel speed in metres per second.")]
        public float speed = 0.25f;

        [Tooltip("Blend between constant speed (0) and smooth ease-in/out (1).")]
        [Range(0f, 1f)] public float smoothing = 1f;

        public Vector3 Offset => transform.localPosition - home;
        public bool IsMoving { get; private set; }

        Vector3 home;
        bool captured;

        void Awake() { Capture(); }

        void Capture()
        {
            if (captured) return;
            home = transform.localPosition;
            captured = true;
        }

        public void Set(Vector3 offset)
        {
            Capture();
            transform.localPosition = home + offset;
        }

        // Moves to the given offset from home; negative speed means "use the default".
        public IEnumerator MoveTo(Vector3 offset, float overrideSpeed = -1f)
        {
            Capture();
            Vector3 from = transform.localPosition;
            Vector3 to = home + offset;
            float s = overrideSpeed > 0f ? overrideSpeed : speed;
            float duration = Mathf.Max(0.01f, Vector3.Distance(from, to) / Mathf.Max(0.0001f, s));
            float t = 0f;
            IsMoving = true;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float u = Mathf.Clamp01(t);
                float eased = Mathf.Lerp(u, Mathf.SmoothStep(0f, 1f, u), smoothing);
                transform.localPosition = Vector3.LerpUnclamped(from, to, eased);
                yield return null;
            }
            transform.localPosition = to;
            IsMoving = false;
        }

        public IEnumerator Home(float overrideSpeed = -1f) { yield return MoveTo(Vector3.zero, overrideSpeed); }
    }
}
