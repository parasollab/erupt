using NUnit.Framework;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>
    /// The Part 2 tier invariants, asserted rather than trusted. These are the checks
    /// that make Part 8 code review item 7 mechanical instead of a habit.
    /// </summary>
    public class UiTierRegistryTests
    {
        private UiTierRegistry registry;

        [SetUp]
        public void SetUp() => registry = new UiTierRegistry();

        [Test]
        public void TierOne_AcceptsExactlyFourControls()
        {
            registry.RegisterTierOne(new FakeControl("mode"));
            registry.RegisterTierOne(new FakeControl("undo"));
            registry.RegisterTierOne(new FakeControl("redo"));
            registry.RegisterTierOne(new FakeControl("voice"));

            Assert.AreEqual(4, registry.TierOneControls.Count);
        }

        // Guidelines Part 2 makes this a hard cap, so a fifth must fail loudly.
        [Test]
        public void TierOne_RejectsAFifthControl()
        {
            registry.RegisterTierOne(new FakeControl("mode"));
            registry.RegisterTierOne(new FakeControl("undo"));
            registry.RegisterTierOne(new FakeControl("redo"));
            registry.RegisterTierOne(new FakeControl("voice"));

            var ex = Assert.Throws<UiTierViolationException>(
                () => registry.RegisterTierOne(new FakeControl("settings")));

            StringAssert.Contains("capped at 4", ex.Message);
            StringAssert.Contains("tier 2 or tier 3", ex.Message);
        }

        [Test]
        public void TierOne_RejectsAnElementDeclaringAnotherTier()
        {
            var wrong = new FakeControl("oops") { DeclaredTier = UiTier.Summoned };
            Assert.Throws<UiTierViolationException>(() => registry.RegisterTierOne(wrong));
        }

        [Test]
        public void TierOne_RejectsDuplicateIds()
        {
            registry.RegisterTierOne(new FakeControl("undo"));
            Assert.Throws<UiTierViolationException>(() => registry.RegisterTierOne(new FakeControl("undo")));
        }

        [Test]
        public void TierOne_FreesASlotOnUnregister()
        {
            var fourth = new FakeControl("voice");
            registry.RegisterTierOne(new FakeControl("mode"));
            registry.RegisterTierOne(new FakeControl("undo"));
            registry.RegisterTierOne(new FakeControl("redo"));
            registry.RegisterTierOne(fourth);

            registry.UnregisterTierOne(fourth);

            Assert.DoesNotThrow(() => registry.RegisterTierOne(new FakeControl("transcript")));
        }

        [Test]
        public void Summoned_NoneOpenByDefault()
        {
            registry.RegisterPanel(new FakePanel("planner"));
            registry.RegisterPanel(new FakePanel("library"));

            Assert.IsNull(registry.OpenPanel);
        }

        [Test]
        public void Summoned_OpeningOneClosesTheOther()
        {
            var planner = new FakePanel("planner");
            var library = new FakePanel("library");
            registry.RegisterPanel(planner);
            registry.RegisterPanel(library);

            registry.Open(planner);
            registry.Open(library);

            Assert.IsFalse(planner.IsOpen, "Opening a second panel must close the first.");
            Assert.IsTrue(library.IsOpen);
            Assert.AreSame(library, registry.OpenPanel);
        }

        [Test]
        public void Summoned_RejectsUnregisteredPanel()
        {
            Assert.Throws<UiTierViolationException>(() => registry.Open(new FakePanel("ghost")));
        }

        [Test]
        public void Summoned_CloseAllLeavesNoneOpen()
        {
            var planner = new FakePanel("planner");
            registry.RegisterPanel(planner);
            registry.Open(planner);

            registry.CloseAll();

            Assert.IsNull(registry.OpenPanel);
        }

        private class FakeControl : ITierOneControl
        {
            public FakeControl(string id) => Id = id;
            public UiTier DeclaredTier = UiTier.Persistent;
            public UiTier Tier => DeclaredTier;
            public string Id { get; }
        }

        private class FakePanel : ISummonedPanel
        {
            public FakePanel(string id) => Id = id;
            public UiTier Tier => UiTier.Summoned;
            public string Id { get; }
            public bool IsOpen { get; private set; }
            public void Open() => IsOpen = true;
            public void Close() => IsOpen = false;
        }
    }
}
