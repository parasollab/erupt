using System.Collections;
using UnityEngine;

namespace Factory
{
    // Base for looping machine animations: subclasses describe one production cycle as a
    // coroutine built from the part components (SlidingPart, RotatingPart, IndexingPart,
    // LinearAxis). Cycles are desynchronised with a random start delay so a hall of machines
    // does not move in lock-step.
    public abstract class MachineCycle : MonoBehaviour
    {
        [Tooltip("Repeat the cycle forever; otherwise run it once.")]
        public bool loop = true;

        [Tooltip("Fixed delay before the first cycle, in seconds.")]
        public float startDelay = 0f;

        [Tooltip("Additional random delay (0..this) before the first cycle.")]
        public float randomStartDelay = 6f;

        [Tooltip("Pause between cycles (load / unload time), in seconds.")]
        public float idleBetweenCycles = 4f;

        public bool Running { get; private set; }
        public int CyclesCompleted { get; private set; }

        protected abstract IEnumerator RunCycle();

        IEnumerator Start()
        {
            float delay = startDelay + Random.Range(0f, Mathf.Max(0f, randomStartDelay));
            if (delay > 0f) yield return new WaitForSeconds(delay);
            Running = true;
            do
            {
                yield return RunCycle();
                CyclesCompleted++;
                if (idleBetweenCycles > 0f) yield return new WaitForSeconds(idleBetweenCycles);
            }
            while (loop);
            Running = false;
        }

        // Runs several part routines at once and waits until all of them finish.
        protected IEnumerator Together(params IEnumerator[] routines)
        {
            int remaining = 0;
            foreach (IEnumerator routine in routines)
            {
                if (routine == null) continue;
                remaining++;
                StartCoroutine(Tracked(routine, () => remaining--));
            }
            while (remaining > 0) yield return null;
        }

        IEnumerator Tracked(IEnumerator routine, System.Action done)
        {
            yield return routine;
            done();
        }

        protected static IEnumerator Open(SlidingPart part) => part != null ? part.Open() : null;
        protected static IEnumerator Close(SlidingPart part) => part != null ? part.Close() : null;
        protected static IEnumerator SpinUp(RotatingPart part) => part != null ? part.SpinUp() : null;
        protected static IEnumerator SpinDown(RotatingPart part) => part != null ? part.SpinDown() : null;
        protected static IEnumerator Index(IndexingPart part, int steps = 1) => part != null ? part.Index(steps) : null;
        protected static IEnumerator Move(LinearAxis axis, Vector3 offset, float speed = -1f) => axis != null ? axis.MoveTo(offset, speed) : null;
        protected static IEnumerator Home(LinearAxis axis, float speed = -1f) => axis != null ? axis.Home(speed) : null;

        protected static WaitForSeconds Dwell(float seconds) => new WaitForSeconds(seconds);

        // Coroutine helper so a null routine (missing optional part) is simply skipped.
        protected IEnumerator Do(IEnumerator routine)
        {
            if (routine != null) yield return routine;
        }
    }
}
