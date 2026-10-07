using System.Collections;
using UnityEngine;

namespace Factory
{
    // Production cycle for a horizontal machining centre with a rotary pallet changer (Haas EC-style):
    // the load-station door closes, the pallet changer swings the loaded pallet into the machine,
    // the tool carousel indexes a few times while "machining", then the pallet comes back out.
    public class HorizontalMillCycle : MachineCycle
    {
        [Header("Parts")]
        public SlidingPart loadDoor;
        public IndexingPart palletChanger;
        public IndexingPart toolCarousel;

        [Tooltip("Simulated machining time with the pallet inside, in seconds.")]
        public float machiningTime = 8f;
        public int toolChanges = 3;

        protected override IEnumerator RunCycle()
        {
            yield return Close(loadDoor);
            yield return Dwell(0.5f);
            yield return Index(palletChanger, 1);

            float slice = machiningTime / Mathf.Max(1, toolChanges);
            for (int i = 0; i < toolChanges; i++)
            {
                yield return Dwell(slice);
                yield return Do(Index(toolCarousel, 1));
            }

            yield return Index(palletChanger, 1);
            yield return Dwell(0.5f);
            yield return Open(loadDoor);
        }
    }
}
