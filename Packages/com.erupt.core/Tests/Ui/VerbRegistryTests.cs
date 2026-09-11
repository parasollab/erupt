using System;
using System.Linq;
using NUnit.Framework;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    public class VerbRegistryTests
    {
        [Test]
        public void FromTable_SeedsEveryGuidelineRow()
        {
            var registry = VerbRegistry.FromTable();
            foreach (var kind in VerbTable.Kinds)
                CollectionAssert.AreEqual(VerbTable.For(kind).Select(v => v.Id), registry.For(kind).Select(v => v.Id));
        }

        [Test]
        public void Register_AddsAPluginVerbToItsKind_WithOriginAndPluginId()
        {
            var registry = VerbRegistry.FromTable();
            int changed = 0;
            registry.Changed += () => changed++;

            Verb verb = registry.Register(SelectionKind.EndEffector, "plan", "Plan", "moveit");

            Assert.AreEqual(VerbOrigin.Plugin, verb.Origin);
            Assert.AreEqual("moveit", verb.PluginId);
            Assert.IsTrue(verb.IsAvailable);
            Assert.IsTrue(registry.Contains(SelectionKind.EndEffector, "plan"));
            Assert.IsFalse(registry.Contains(SelectionKind.Obstacle, "plan"), "A verb belongs to one kind's row.");
            Assert.AreEqual(1, changed);
        }

        [Test]
        public void Register_RefusesADuplicateIdInTheSameRow()
        {
            var registry = VerbRegistry.FromTable();
            Assert.Throws<InvalidOperationException>(() => registry.Register(SelectionKind.Obstacle, "delete", "Delete again", "x"),
                "'delete' already exists for Obstacle; a plugin binds it, it does not add it twice.");
            registry.Register(SelectionKind.Trajectory, "scrub-fast", "Scrub", "x");
            Assert.Throws<InvalidOperationException>(() => registry.Register(SelectionKind.Trajectory, "scrub-fast", "Scrub", "y"));
        }

        [Test]
        public void Register_RefusesNoneKind()
        {
            var registry = VerbRegistry.FromTable();
            Assert.Throws<ArgumentException>(() => registry.Register(SelectionKind.None, "x", "X", "p"));
        }

        [Test]
        public void RemoveAllFrom_DropsOnlyThatPluginsVerbs()
        {
            var registry = VerbRegistry.FromTable();
            registry.Register(SelectionKind.EndEffector, "plan", "Plan", "moveit");
            registry.Register(SelectionKind.Trajectory, "replan", "Replan", "moveit");
            registry.Register(SelectionKind.Trajectory, "solve", "Solve", "mtc");

            Assert.AreEqual(2, registry.RemoveAllFrom("moveit"));
            Assert.IsFalse(registry.Contains(SelectionKind.EndEffector, "plan"));
            Assert.IsTrue(registry.Contains(SelectionKind.Trajectory, "solve"));
            Assert.IsTrue(registry.Contains(SelectionKind.Obstacle, "delete"), "Guideline rows are never removed.");
        }

        [Test]
        public void ContextualMenuModel_ShowsPluginVerbsForTheSelectedKind_AndRefreshesOnChange()
        {
            var registry = VerbRegistry.FromTable();
            var model = new ContextualMenuModel(registry);
            var target = new UnityEngine.GameObject("ee").AddComponent<SelectableMarker>();
            target.SetKind(SelectionKind.EndEffector);
            model.SetTarget(target);
            int before = model.Verbs.Count;

            registry.Register(SelectionKind.EndEffector, "plan", "Plan", "moveit");

            Assert.AreEqual(before + 1, model.Verbs.Count, "An open menu picks up a newly registered verb.");
            Assert.IsTrue(model.Verbs.Any(v => v.Id == "plan"));
            UnityEngine.Object.DestroyImmediate(target.gameObject);
        }
    }
}
