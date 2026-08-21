using NUnit.Framework;
using System.Collections.Generic;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    public class UndoStackTests
    {
        private UndoStack stack;
        private List<string> log;

        [SetUp]
        public void SetUp()
        {
            stack = new UndoStack();
            log = new List<string>();
        }

        [Test]
        public void Do_ExecutesAndBecomesUndoable()
        {
            stack.Do(new FakeCommand("move", log));

            Assert.AreEqual(new[] { "do:move" }, log.ToArray());
            Assert.IsTrue(stack.CanUndo);
            Assert.IsFalse(stack.CanRedo);
            Assert.AreEqual("move", stack.NextUndoLabel);
        }

        [Test]
        public void Undo_ThenRedo_RoundTrips()
        {
            stack.Do(new FakeCommand("move", log));
            stack.Undo();
            stack.Redo();

            Assert.AreEqual(new[] { "do:move", "undo:move", "do:move" }, log.ToArray());
            Assert.IsTrue(stack.CanUndo);
            Assert.IsFalse(stack.CanRedo);
        }

        [Test]
        public void Undo_UnwindsInReverseOrder()
        {
            stack.Do(new FakeCommand("create", log));
            stack.Do(new FakeCommand("scale", log));
            log.Clear();

            stack.Undo();
            stack.Undo();

            Assert.AreEqual(new[] { "undo:scale", "undo:create" }, log.ToArray());
            Assert.IsFalse(stack.CanUndo);
        }

        [Test]
        public void NewCommand_DiscardsTheRedoBranch()
        {
            stack.Do(new FakeCommand("a", log));
            stack.Undo();

            stack.Do(new FakeCommand("b", log));

            Assert.IsFalse(stack.CanRedo, "A new action must discard the redo branch.");
        }

        [Test]
        public void Record_TracksAnAlreadyAppliedChangeWithoutRerunningIt()
        {
            stack.Record(new FakeCommand("drag", log));

            Assert.IsEmpty(log, "Record must not re-execute; the change already happened.");
            Assert.IsTrue(stack.CanUndo);
        }

        [Test]
        public void Capacity_DropsOldestFirst()
        {
            var bounded = new UndoStack(capacity: 2);
            bounded.Do(new FakeCommand("first", log));
            bounded.Do(new FakeCommand("second", log));
            bounded.Do(new FakeCommand("third", log));

            Assert.IsTrue(bounded.Undo());
            Assert.IsTrue(bounded.Undo());
            Assert.IsFalse(bounded.Undo(), "Only the two most recent commands should survive.");
        }

        [Test]
        public void Undo_OnEmptyStack_IsANoOp()
        {
            Assert.IsFalse(stack.Undo());
            Assert.IsFalse(stack.Redo());
        }

        [Test]
        public void Changed_FiresOnEveryTransition()
        {
            int changes = 0;
            stack.Changed += () => changes++;

            stack.Do(new FakeCommand("a", log));
            stack.Undo();
            stack.Redo();
            stack.Clear();

            Assert.AreEqual(4, changes);
        }

        private class FakeCommand : IUndoableCommand
        {
            private readonly List<string> log;
            public FakeCommand(string label, List<string> log) { Label = label; this.log = log; }
            public string Label { get; }
            public void Execute() => log.Add($"do:{Label}");
            public void Undo() => log.Add($"undo:{Label}");
        }
    }
}
