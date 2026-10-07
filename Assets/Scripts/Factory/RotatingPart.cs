using System.Collections;
using UnityEngine;

namespace Factory
{
    // Continuous rotation about a local axis with linear spin-up / spin-down,
    // e.g. a spindle, lathe chuck or conveyor roller. The transform's pivot must lie on the axis.
    public class RotatingPart : MonoBehaviour
    {
        [Tooltip("Rotation axis in the part's local space.")]
        public Vector3 axis = Vector3.up;

        [Tooltip("Steady-state speed in revolutions per minute.")]
        public float rpm = 600f;

        [Tooltip("Seconds to reach full speed from rest (and to stop).")]
        public float spinUpTime = 2f;

        [Tooltip("Requested state; the speed ramps toward it every frame.")]
        public bool spinning;

        public float CurrentRpm { get; private set; }
        public bool AtTarget => Mathf.Approximately(CurrentRpm, spinning ? rpm : 0f);

        void Update()
        {
            float target = spinning ? rpm : 0f;
            float rate = spinUpTime > 0f ? Mathf.Abs(rpm) / spinUpTime : float.MaxValue;
            CurrentRpm = Mathf.MoveTowards(CurrentRpm, target, rate * Time.deltaTime);
            if (CurrentRpm != 0f)
                transform.Rotate(axis.normalized, CurrentRpm * 6f * Time.deltaTime, Space.Self);
        }

        public IEnumerator SpinUp()
        {
            spinning = true;
            while (!AtTarget) yield return null;
        }

        public IEnumerator SpinDown()
        {
            spinning = false;
            while (!AtTarget) yield return null;
        }
    }
}
