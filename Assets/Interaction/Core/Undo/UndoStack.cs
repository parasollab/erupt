using System;
using System.Collections.Generic;

namespace Erupt.Interaction
{
    /// <summary>
    /// Command history behind tier 1's undo and redo.
    /// </summary>
    /// <remarks>
    /// Commands must be self-contained: undoing an obstacle deletion has to recreate the
    /// object with its original id, because the planning scene is keyed by id and
    /// reversing a Destroy is not enough to put it back in MoveIt's world.
    /// </remarks>
    public class UndoStack
    {
        private readonly List<IUndoableCommand> undo = new();
        private readonly List<IUndoableCommand> redo = new();
        private readonly int capacity;

        public UndoStack(int capacity = 64)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        public string NextUndoLabel => CanUndo ? undo[^1].Label : null;
        public string NextRedoLabel => CanRedo ? redo[^1].Label : null;

        /// <summary>Fires whenever undo/redo availability may have changed.</summary>
        public event Action Changed;

        /// <summary>Run a command and record it. Clears the redo branch.</summary>
        public void Do(IUndoableCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            command.Execute();
            undo.Add(command);
            redo.Clear();

            // Oldest first: the far end of the history is the cheapest thing to lose.
            if (undo.Count > capacity) undo.RemoveAt(0);

            Changed?.Invoke();
        }

        /// <summary>Record a command that has already been applied, without re-running it.</summary>
        public void Record(IUndoableCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            undo.Add(command);
            redo.Clear();
            if (undo.Count > capacity) undo.RemoveAt(0);
            Changed?.Invoke();
        }

        public bool Undo()
        {
            if (!CanUndo) return false;

            var command = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            command.Undo();
            redo.Add(command);

            Changed?.Invoke();
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;

            var command = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            command.Execute();
            undo.Add(command);

            Changed?.Invoke();
            return true;
        }

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
            Changed?.Invoke();
        }
    }
}
