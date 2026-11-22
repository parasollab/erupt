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

        // ---- verbs that are shown only for some selections ----

        private sealed class Thing : ISelectable
        {
            public Thing(SelectionKind kind, string name) { Kind = kind; DisplayName = name; }
            public SelectionKind Kind { get; }
            public UnityEngine.GameObject GameObject => null;
            public string DisplayName { get; }
        }

        [Test]
        public void ShowWhen_ListsAVerbOnlyForTheSelectionsItsConditionAccepts()
        {
            var registry = VerbRegistry.FromTable();
            registry.Register(SelectionKind.RobotLink, "place", "Place", "ar");
            var marked = new Thing(SelectionKind.RobotLink, "marked");
            var plain = new Thing(SelectionKind.RobotLink, "plain");
            bool tagInView = true;
            registry.ShowWhen(SelectionKind.RobotLink, "place", target => tagInView && ReferenceEquals(target, marked));

            CollectionAssert.Contains(registry.For(SelectionKind.RobotLink, marked).Select(v => v.Id).ToList(), "place");
            CollectionAssert.DoesNotContain(registry.For(SelectionKind.RobotLink, plain).Select(v => v.Id).ToList(), "place");
            // The rest of the row is untouched, in its order.
            CollectionAssert.AreEqual(VerbTable.For(SelectionKind.RobotLink).Select(v => v.Id),
                registry.For(SelectionKind.RobotLink, plain).Select(v => v.Id));
            // The row itself still has the verb: the condition is about one selection.
            Assert.IsTrue(registry.Contains(SelectionKind.RobotLink, "place"));
            Assert.IsTrue(registry.IsShownFor(SelectionKind.RobotLink, "set-joint-angle", plain), "A verb without a condition is always shown.");

            tagInView = false;
            CollectionAssert.DoesNotContain(registry.For(SelectionKind.RobotLink, marked).Select(v => v.Id).ToList(), "place");
        }

        [Test]
        public void Refresh_TellsOpenMenusToBuildAgain_AndTheMenuDropsOrGainsTheVerb()
        {
            var registry = VerbRegistry.FromTable();
            registry.Register(SelectionKind.RobotLink, "place", "Place", "ar");
            bool tagInView = false;
            registry.ShowWhen(SelectionKind.RobotLink, "place", _ => tagInView);
            var menu = new ContextualMenuModel(registry);
            menu.Bind("place", _ => { });
            menu.SetTarget(new Thing(SelectionKind.RobotLink, "link"));
            int rebuilt = 0;
            menu.Changed += () => rebuilt++;

            Assert.IsFalse(menu.Verbs.Any(v => v.Id == "place"));
            Assert.IsTrue(menu.Invoke("place").IsRefused, "A verb that is not shown cannot be invoked through the menu.");

            tagInView = true;
            registry.Refresh();

            Assert.AreEqual(1, rebuilt);
            Assert.IsTrue(menu.Verbs.Any(v => v.Id == "place"));
            Assert.IsFalse(menu.Invoke("place").IsRefused);
        }

        [Test]
        public void ACondition_ThatThrows_HidesTheVerb_AndGoesWithItsPlugin()
        {
            var registry = VerbRegistry.FromTable();
            registry.Register(SelectionKind.EndEffector, "place", "Place", "ar");
            registry.ShowWhen(SelectionKind.EndEffector, "place", _ => throw new InvalidOperationException("gone"));
            var hand = new Thing(SelectionKind.EndEffector, "hand");

            Assert.IsFalse(registry.IsShownFor(SelectionKind.EndEffector, "place", hand));

            registry.RemoveAllFrom("ar");
            // Another plugin's verb of the same id does not inherit the condition.
            registry.Register(SelectionKind.EndEffector, "place", "Place", "other");
            Assert.IsTrue(registry.IsShownFor(SelectionKind.EndEffector, "place", hand));
        }
    }
}
