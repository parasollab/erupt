using System.Collections;
using UnityEngine;

namespace Factory
{
    // Production cycle for a vertical machining centre (Haas VF-style): doors close, spindle spins up,
    // the head plunges and the table traces a few pocketing passes, then everything retracts and the
    // doors open for load / unload. All offsets are in the local space of the part's parent.
    public class VerticalMillCycle : MachineCycle
    {
        [Header("Parts")]
        public SlidingPart doorLeft;
        public SlidingPart doorRight;
        public RotatingPart spindle;
        public LinearAxis head;
        public LinearAxis saddle;
        public LinearAxis table;
        public IndexingPart toolCarousel;

        [Header("Motion (local offsets from the authored pose)")]
        [Tooltip("Head offset that brings the tool tip down to the workpiece.")]
        public Vector3 headCutOffset = new Vector3(0f, -0.4f, 0f);
        [Tooltip("Saddle (Y axis) offset that centres the workpiece under the spindle.")]
        public Vector3 saddleCentreOffset;
        [Tooltip("Table (X axis) offset that centres the workpiece under the spindle.")]
        public Vector3 tableCentreOffset;
        [Tooltip("Half-width of the pocketing pattern on the table axis.")]
        public Vector3 tablePassVector = new Vector3(0.09f, 0f, 0f);
        [Tooltip("Step between passes on the saddle axis.")]
        public Vector3 saddleStepVector = new Vector3(0f, 0f, 0.03f);
        public int passes = 3;
        public float feedSpeed = 0.06f;
        public float rapidSpeed = 0.35f;
        public float plungeSpeed = 0.08f;

        protected override IEnumerator RunCycle()
        {
            yield return Together(Close(doorLeft), Close(doorRight));
            yield return Dwell(0.5f);

            // Rapid the workpiece under the spindle while the spindle comes up to speed.
            yield return Together(SpinUp(spindle),
                Move(saddle, saddleCentreOffset, rapidSpeed),
                Move(table, tableCentreOffset - tablePassVector, rapidSpeed));

            yield return Move(head, headCutOffset, plungeSpeed);

            for (int i = 0; i < passes; i++)
            {
                Vector3 row = saddleCentreOffset + saddleStepVector * (i - (passes - 1) * 0.5f);
                yield return Move(saddle, row, feedSpeed);
                bool forward = (i % 2) == 0;
                yield return Move(table, tableCentreOffset + (forward ? tablePassVector : -tablePassVector), feedSpeed);
            }

            yield return Move(head, Vector3.zero, rapidSpeed);
            yield return Together(SpinDown(spindle), Home(saddle, rapidSpeed), Home(table, rapidSpeed));

            // Index the side-mount carousel as if swapping tools for the next job.
            yield return Do(Index(toolCarousel, 1));
            yield return Dwell(0.3f);

            yield return Together(Open(doorLeft), Open(doorRight));
        }
    }
}
