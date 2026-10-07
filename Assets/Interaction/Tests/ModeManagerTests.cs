using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    /// <summary>Guidelines Part 4: three modes, one action to switch, always reversible.</summary>
    public class ModeManagerTests
    {
        private GameObject host;
        private ModeManager modes;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("modes");
            modes = host.AddComponent<ModeManager>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void ThereAreExactlyThreeModes()
        {
            Assert.AreEqual(3, System.Enum.GetValues(typeof(AppMode)).Length,
                "Part 4 fixes the mode set at Build, Plan and Teach.");
        }

        [Test]
        public void StartsInBuild()
        {
            Assert.AreEqual(AppMode.Build, modes.Current);
        }

        [Test]
        public void Cycle_VisitsEveryModeAndReturns()
        {
            Assert.AreEqual(AppMode.Build, modes.Current);
            modes.Cycle();
            Assert.AreEqual(AppMode.Plan, modes.Current);
            modes.Cycle();
            Assert.AreEqual(AppMode.Teach, modes.Current);
            modes.Cycle();
            Assert.AreEqual(AppMode.Build, modes.Current);
        }

        [Test]
        public void Revert_UndoesTheLastSwitch()
        {
            modes.SetMode(AppMode.Teach);
            modes.Revert();

            Assert.AreEqual(AppMode.Build, modes.Current, "Every switch must be reversible.");
        }

        [Test]
        public void ModeChanged_FiresOnceAndOnlyOnRealChanges()
        {
            int changes = 0;
            modes.ModeChanged += _ => changes++;

            modes.SetMode(AppMode.Plan);
            modes.SetMode(AppMode.Plan);

            Assert.AreEqual(1, changes);
        }

        [Test]
        public void Is_ReportsTheCurrentMode()
        {
            modes.SetMode(AppMode.Teach);
            Assert.IsTrue(modes.Is(AppMode.Teach));
            Assert.IsFalse(modes.Is(AppMode.Build));
        }
    }
}
