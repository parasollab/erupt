using System;
using UnityEngine;

namespace Erupt.Interaction
{
    [Flags]
    public enum PinchEvents
    {
        None      = 0,
        Down      = 1 << 0,
        BeginDrag = 1 << 1,
        Drag      = 1 << 2,
        EndDrag   = 1 << 3,
        Up        = 1 << 4
    }

    /// <summary>
    /// Turns a per-frame fingertip gap into press and drag phases, for sources whose only
    /// button is a pinch. A controller has a trigger to select and a grip to drag; a hand
    /// has one gesture for both, so a pinch selects on contact and becomes a drag only
    /// once it is held or moved.
    /// </summary>
    /// <remarks>
    /// Plain logic with no device access, so it runs headless in tests. The press and
    /// release distances differ so a pinch held at the threshold does not chatter.
    /// </remarks>
    public class PinchTracker
    {
        /// <summary>Fingertip gap in metres below which a pinch starts.</summary>
        public float PressDistance = 0.02f;

        /// <summary>Fingertip gap in metres above which a held pinch ends.</summary>
        public float ReleaseDistance = 0.035f;

        /// <summary>Metres a held pinch must travel before it becomes a drag.</summary>
        public float DragStartDistance = 0.02f;

        /// <summary>Seconds after which a held pinch becomes a drag without moving.</summary>
        public float DragStartSeconds = 0.3f;

        public bool IsPinching { get; private set; }
        public bool IsDragging { get; private set; }

        /// <summary>Where the pinch point was when the current pinch started.</summary>
        public Vector3 DownPoint { get; private set; }

        private double downTime;

        public PinchEvents Update(float gap, Vector3 pinchPoint, double time)
        {
            if (!IsPinching)
            {
                if (gap >= PressDistance) return PinchEvents.None;

                IsPinching = true;
                DownPoint = pinchPoint;
                downTime = time;
                return PinchEvents.Down;
            }

            if (gap > ReleaseDistance) return Release();
            if (IsDragging) return PinchEvents.Drag;

            bool moved = (pinchPoint - DownPoint).sqrMagnitude >= DragStartDistance * DragStartDistance;
            bool held = time - downTime >= DragStartSeconds;
            if (!moved && !held) return PinchEvents.None;

            IsDragging = true;
            return PinchEvents.BeginDrag | PinchEvents.Drag;
        }

        /// <summary>End the pinch now — tracking was lost or the source was disabled.</summary>
        public PinchEvents Release()
        {
            if (!IsPinching) return PinchEvents.None;

            PinchEvents events = PinchEvents.Up;
            if (IsDragging) events |= PinchEvents.EndDrag;

            IsPinching = false;
            IsDragging = false;
            return events;
        }
    }
}
