namespace Erupt.Ui
{
    /// <summary>
    /// The three tiers from Guidelines Part 2. Every UI element declares one; there is no
    /// default and no way to register without choosing.
    /// </summary>
    public enum UiTier
    {
        /// <summary>Always visible. Hard cap of four controls.</summary>
        Persistent,

        /// <summary>Attached to the current selection, gone on deselect. Most features live here.</summary>
        Contextual,

        /// <summary>Configuration and libraries. One open at a time, none open by default.</summary>
        Summoned
    }
}
