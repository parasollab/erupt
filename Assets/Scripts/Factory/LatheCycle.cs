using System.Collections;
using UnityEngine;

namespace Factory
{
    // Production cycle for a CNC turning centre (Haas ST-style): door closes, chuck spins up, the turret
    // indexes to a tool, feeds in for a turning pass and a facing pass, retracts, and the door opens.
    public class LatheCycle : MachineCycle
    {
        [Header("Parts")]
        public SlidingPart door;
        public RotatingPart chuck;
        public LinearAxis turretSlide;
        public IndexingPart turret;

        [Header("Motion (local offsets of the turret slide from its authored pose)")]
        [Tooltip("Offset that brings the tool tip onto the bar surface (lathe X axis).")]
        public Vector3 cutDepthOffset;
        [Tooltip("Offset along the spindle axis for the turning pass (lathe Z axis).")]
        public Vector3 turnPassVector;
        [Tooltip("Offset toward the chuck for the facing pass start.")]
        public Vector3 faceStartOffset;
        [Tooltip("Facing travel (across the bar end).")]
        public Vector3 facePassVector;
        public float feedSpeed = 0.05f;
        public float rapidSpeed = 0.3f;

        protected override IEnumerator RunCycle()
        {
            yield return Close(door);
            yield return Dwell(0.4f);
            yield return Together(SpinUp(chuck), Index(turret, 1));

            // Turning pass: approach, feed along the bar, retract.
            yield return Move(turretSlide, cutDepthOffset, rapidSpeed);
            yield return Move(turretSlide, cutDepthOffset + turnPassVector, feedSpeed);
            yield return Move(turretSlide, turnPassVector, rapidSpeed);
            yield return Home(turretSlide, rapidSpeed);

            yield return Do(Index(turret, 1));

            // Facing pass.
            yield return Move(turretSlide, faceStartOffset, rapidSpeed);
            yield return Move(turretSlide, faceStartOffset + facePassVector, feedSpeed);
            yield return Home(turretSlide, rapidSpeed);

            yield return Together(SpinDown(chuck), Index(turret, -2));
            yield return Open(door);
        }
    }
}
