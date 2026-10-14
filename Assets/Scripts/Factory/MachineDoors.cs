using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Factory
{
    // The operator-side sliding doors of a machine (e.g. the two front doors of the Haas VF-2),
    // opened and closed as one unit from a menu or script. Each door is a SlidingPart; this
    // drives them together and reports the combined state. Doors start in their authored
    // (closed) pose unless startOpen is set.
    public class MachineDoors : MonoBehaviour
    {
        [Tooltip("Door panels driven together; each one's openOffset is its own travel.")]
        public SlidingPart[] doors;

        [Tooltip("Open the doors once at start instead of leaving them closed.")]
        public bool startOpen = false;

        [Tooltip("Human-readable machine name for menus and logs.")]
        public string machineName = "Machine";

        public enum DoorState { Closed, Opening, Open, Closing }

        public DoorState State { get; private set; } = DoorState.Closed;
        public bool IsOpen => State == DoorState.Open;
        public bool IsMoving => State == DoorState.Opening || State == DoorState.Closing;

        /// <summary>0 = closed, 1 = fully open (largest open amount among the doors).</summary>
        public float OpenAmount => doors == null || doors.Length == 0
            ? 0f
            : doors.Where(d => d != null).Select(d => d.openAmount).DefaultIfEmpty(0f).Max();

        public event Action<DoorState> StateChanged;

        Coroutine motion;

        void Start()
        {
            if (startOpen) Open();
        }

        public void Open() => Drive(true);
        public void Close() => Drive(false);

        public void Toggle()
        {
            if (State == DoorState.Open || State == DoorState.Opening) Close();
            else Open();
        }

        /// <summary>Jumps straight to the end pose without animating (editor previews, probes).</summary>
        public void SetImmediate(bool open)
        {
            if (motion != null) { StopCoroutine(motion); motion = null; }
            foreach (SlidingPart d in doors ?? Array.Empty<SlidingPart>())
                if (d != null) d.Set(open ? 1f : 0f);
            SetState(open ? DoorState.Open : DoorState.Closed);
        }

        void Drive(bool open)
        {
            if (!isActiveAndEnabled) { SetImmediate(open); return; }
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(Move(open));
        }

        IEnumerator Move(bool open)
        {
            SetState(open ? DoorState.Opening : DoorState.Closing);
            int remaining = 0;
            foreach (SlidingPart d in doors ?? Array.Empty<SlidingPart>())
            {
                if (d == null) continue;
                remaining++;
                StartCoroutine(Tracked(open ? d.Open() : d.Close(), () => remaining--));
            }
            while (remaining > 0) yield return null;
            motion = null;
            SetState(open ? DoorState.Open : DoorState.Closed);
        }

        IEnumerator Tracked(IEnumerator routine, Action done)
        {
            yield return routine;
            done();
        }

        void SetState(DoorState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }
    }
}
