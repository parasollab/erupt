namespace Erupt.Interaction
{
    /// <summary>
    /// An input source that can pulse (<see cref="Capability.Haptics"/>). Backends implement
    /// it; features send impulses through it without knowing the device.
    /// </summary>
    public interface IHapticOutput
    {
        /// <param name="amplitude">0..1.</param>
        /// <param name="seconds">Length of the pulse.</param>
        void SendHapticImpulse(float amplitude, float seconds);
    }
}
