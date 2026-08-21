using System.Linq;
using NUnit.Framework;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>The Part 2 tier 2 vocabulary, and what of it actually works today.</summary>
    public class VerbTableTests
    {
        // Part 2 lists six selection kinds; every one must have a row, or a selection
        // exists that no menu can describe.
        [Test]
        public void EveryRealSelectionKindHasVerbs()
        {
            foreach (SelectionKind kind in System.Enum.GetValues(typeof(SelectionKind)))
            {
                if (kind == SelectionKind.None) continue;
                Assert.IsNotEmpty(VerbTable.For(kind), $"{kind} has no verbs.");
            }
        }

        [Test]
        public void NothingSelected_HasNoVerbs()
        {
            Assert.IsEmpty(VerbTable.For(SelectionKind.None));
        }

        [Test]
        public void VerbIdsAreUniqueWithinAKind()
        {
            foreach (var kind in VerbTable.Kinds)
            {
                var ids = VerbTable.For(kind).Select(v => v.Id).ToList();
                Assert.AreEqual(ids.Count, ids.Distinct().Count(), $"Duplicate verb id in {kind}.");
            }
        }

        // The migrate-only scope: these are the controls that exist today and must keep
        // working once the wrist menu is replaced.
        [TestCase(SelectionKind.Obstacle, "resize")]
        [TestCase(SelectionKind.Obstacle, "delete")]
        [TestCase(SelectionKind.Obstacle, "snap")]
        [TestCase(SelectionKind.Obstacle, "duplicate")]
        [TestCase(SelectionKind.RobotLink, "set-joint-angle")]
        [TestCase(SelectionKind.EndEffector, "set-goal")]
        [TestCase(SelectionKind.Trajectory, "execute")]
        [TestCase(SelectionKind.Trajectory, "preview")]
        public void ExistingFunctionalityIsMarkedAvailable(SelectionKind kind, string verbId)
        {
            var verb = VerbTable.For(kind).Single(v => v.Id == verbId);
            Assert.IsTrue(verb.IsAvailable, $"{verbId} exists today and must not be disabled.");
        }

        // Not-yet-built verbs stay visible but disabled, so the menu shows the intended
        // vocabulary rather than shrinking to whatever happens to be implemented.
        [TestCase(SelectionKind.Obstacle, "paint-cost")]
        [TestCase(SelectionKind.Manipulable, "grasp-here")]
        [TestCase(SelectionKind.Waypoint, "pin-timing")]
        public void UnbuiltVerbsArePresentButDisabled(SelectionKind kind, string verbId)
        {
            var verb = VerbTable.For(kind).Single(v => v.Id == verbId);
            Assert.AreEqual(VerbAvailability.NotYetImplemented, verb.Availability);
        }

        // Snap, duplicate and preview are ERUPT functionality with no row in Part 2.
        // Marked so the deviation is visible rather than silently absorbed.
        [Test]
        public void EruptAdditionsAreMarkedAsSuch()
        {
            var additions = VerbTable.Kinds
                .SelectMany(VerbTable.For)
                .Where(v => v.Origin == VerbOrigin.EruptAddition)
                .Select(v => v.Id)
                .OrderBy(id => id)
                .ToArray();

            Assert.AreEqual(new[] { "duplicate", "preview", "snap" }, additions);
        }
    }
}
