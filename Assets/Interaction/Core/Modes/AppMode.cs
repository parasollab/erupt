namespace Erupt.Interaction
{
    /// <summary>
    /// The complete set of modes. Guidelines Part 4: "Modes are the smallest possible
    /// set" — three, and no more may be added.
    /// </summary>
    public enum AppMode
    {
        /// <summary>Shaping the environment: creating and editing obstacles.</summary>
        Build,

        /// <summary>Specifying goals, planning, previewing and executing.</summary>
        Plan,

        /// <summary>Demonstration and correction. Where LfD lives.</summary>
        Teach
    }
}
