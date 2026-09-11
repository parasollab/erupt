using System;

namespace Erupt.Interaction
{
    /// <summary>
    /// What an input source can do. Feature code asks about capabilities instead of
    /// asking which device it is running on — Guidelines Part 1 P5, Part 3.
    /// </summary>
    [Flags]
    public enum Capability
    {
        None        = 0,
        Ray         = 1 << 0,
        Gaze        = 1 << 1,
        Pinch       = 1 << 2,
        Grip        = 1 << 3,
        Thumbstick  = 1 << 4,
        Haptics     = 1 << 5,
        DirectTouch = 1 << 6,
        Precision   = 1 << 7
    }
}
