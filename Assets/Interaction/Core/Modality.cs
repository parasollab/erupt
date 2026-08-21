namespace Erupt.Interaction
{
    /// <summary>
    /// What kind of input produced a sample. Carried on every <see cref="InteractionSample"/>
    /// so LfD can down-weight degraded spans and studies can report modality mix.
    /// Guidelines Part 3, "Modality and confidence on every sample".
    /// </summary>
    public enum Modality
    {
        Controller,
        Hand,
        GazePinch,
        Mouse
    }
}
