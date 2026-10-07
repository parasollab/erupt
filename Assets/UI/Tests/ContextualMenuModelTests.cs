using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>
    /// Tier 2 lifetime and invocation rules. Guidelines Part 2: the menu is attached to
    /// the selection and disappears on deselect.
    /// </summary>
    public class ContextualMenuModelTests
    {
        private ContextualMenuModel menu;
        private GameObject host;
        private SelectionService selection;

        [SetUp]
        public void SetUp()
        {
            menu = new ContextualMenuModel();
            host = new GameObject("selection");
            selection = host.AddComponent<SelectionService>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void NoSelection_MeansNoMenu()
        {
            Assert.IsFalse(menu.IsVisible);
            Assert.IsEmpty(menu.Verbs);
        }

        [Test]
        public void SelectingAnObstacle_ShowsObstacleVerbs()
        {
            menu.SetTarget(Make(SelectionKind.Obstacle, out var go));

            Assert.IsTrue(menu.IsVisible);
            Assert.AreEqual(SelectionKind.Obstacle, menu.Kind);
            CollectionAssert.Contains(Ids(), "delete");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Deselecting_HidesTheMenu()
        {
            menu.SetTarget(Make(SelectionKind.Obstacle, out var go));
            menu.SetTarget(null);

            Assert.IsFalse(menu.IsVisible, "Tier 2 must disappear on deselect.");
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ChangingKind_SwapsTheVerbs()
        {
            menu.SetTarget(Make(SelectionKind.Obstacle, out var a));
            CollectionAssert.Contains(Ids(), "snap");

            menu.SetTarget(Make(SelectionKind.RobotLink, out var b));
            CollectionAssert.DoesNotContain(Ids(), "snap");
            CollectionAssert.Contains(Ids(), "set-joint-angle");

            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }

        [Test]
        public void FollowingSelection_TracksTheService()
        {
            menu.FollowSelection(selection);
            var obstacle = Make(SelectionKind.Obstacle, out var go);

            selection.Select(obstacle);
            Assert.AreEqual(SelectionKind.Obstacle, menu.Kind);

            selection.ClearSelection();
            Assert.IsFalse(menu.IsVisible);

            menu.StopFollowing(selection);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Invoke_RunsABoundVerb()
        {
            var obstacle = Make(SelectionKind.Obstacle, out var go);
            menu.SetTarget(obstacle);

            ISelectable acted = null;
            menu.Bind("delete", s => acted = s);

            var refusal = menu.Invoke("delete");

            Assert.IsFalse(refusal.IsRefused);
            Assert.AreSame(obstacle, acted);
            Object.DestroyImmediate(go);
        }

        // Guidelines Part 3: no silent no-ops. A disabled verb explains itself.
        [Test]
        public void Invoke_OnAnUnbuiltVerb_RefusesWithAReason()
        {
            menu.SetTarget(Make(SelectionKind.Obstacle, out var go));

            var refusal = menu.Invoke("paint-cost");

            Assert.IsTrue(refusal.IsRefused);
            StringAssert.Contains("not available yet", refusal.Reason);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Invoke_OnAnUnwiredVerb_RefusesRatherThanDoingNothing()
        {
            menu.SetTarget(Make(SelectionKind.Obstacle, out var go));

            var refusal = menu.Invoke("delete");

            Assert.IsTrue(refusal.IsRefused);
            StringAssert.Contains("not wired up", refusal.Reason);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void CanInvoke_RequiresBothAvailabilityAndAHandler()
        {
            var obstacle = Make(SelectionKind.Obstacle, out var go);
            menu.SetTarget(obstacle);

            var delete = System.Array.Find(System.Linq.Enumerable.ToArray(menu.Verbs), v => v.Id == "delete");
            Assert.IsFalse(menu.CanInvoke(delete), "Available but unbound must not be pressable.");

            menu.Bind("delete", _ => { });
            Assert.IsTrue(menu.CanInvoke(delete));

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Changed_FiresOnTargetChangeOnly()
        {
            var obstacle = Make(SelectionKind.Obstacle, out var go);
            int changes = 0;
            menu.Changed += () => changes++;

            menu.SetTarget(obstacle);
            menu.SetTarget(obstacle);

            Assert.AreEqual(1, changes);
            Object.DestroyImmediate(go);
        }

        private string[] Ids() =>
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(menu.Verbs, v => v.Id));

        private static ISelectable Make(SelectionKind kind, out GameObject go)
        {
            go = new GameObject(kind.ToString());
            var marker = go.AddComponent<SelectableMarker>();
            marker.SetKind(kind);
            return marker;
        }
    }
}
