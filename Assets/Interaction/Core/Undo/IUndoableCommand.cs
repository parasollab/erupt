namespace Erupt.Interaction
{
    /// <summary>
    /// One reversible mutation. Guidelines Part 2 puts undo and redo in tier 1, so every
    /// user-visible change to the world has to arrive as one of these.
    /// </summary>
    public interface IUndoableCommand
    {
        /// <summary>Short user-facing description, e.g. "Move Cube".</summary>
        string Label { get; }

        void Execute();
        void Undo();
    }
}
