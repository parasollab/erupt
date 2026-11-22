using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    public class SelectionServiceTests
    {
        private GameObject host;
        private SelectionService service;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("selection");
            service = host.AddComponent<SelectionService>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void Select_ReportsTheKind()
        {
            var obstacle = MakeSelectable(SelectionKind.Obstacle, out var go);

            service.Select(obstacle);

            Assert.AreEqual(SelectionKind.Obstacle, service.CurrentKind);
            Object.DestroyImmediate(go);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator ASelectedObjectThatIsDestroyed_StopsBeingTheSelection()
        {
            var plan = MakeSelectable(SelectionKind.Trajectory, out var go);
            var hovered = MakeSelectable(SelectionKind.Obstacle, out var hoveredGo);
            service.Select(plan);
            service.SetHover(hovered);
            int changes = 0;
            ISelectable last = plan;
            service.SelectionChanged += s => { changes++; last = s; };

            Object.Destroy(go);
            Object.Destroy(hoveredGo);
            yield return null;
            yield return null;

            Assert.IsNull(service.Current, "Whatever follows the selection (the tier 2 menu, every frame) would read a dead object.");
            Assert.IsNull(service.Hovered);
            Assert.AreEqual(1, changes);
            Assert.IsNull(last);
            Assert.IsFalse(SelectionService.IsDestroyed(null));
        }

        [Test]
        public void NothingSelected_ReportsNone()
        {
            Assert.AreEqual(SelectionKind.None, service.CurrentKind);
        }

        [Test]
        public void SelectionChanged_FiresOncePerChange()
        {
            var a = MakeSelectable(SelectionKind.Obstacle, out var goA);
            int changes = 0;
            service.SelectionChanged += _ => changes++;

            service.Select(a);
            service.Select(a);

            Assert.AreEqual(1, changes, "Reselecting the same object must not re-fire.");
            Object.DestroyImmediate(goA);
        }

        [Test]
        public void ClearSelection_FiresWithNull()
        {
            var a = MakeSelectable(SelectionKind.Manipulable, out var goA);
            service.Select(a);

            ISelectable seen = a;
            service.SelectionChanged += s => seen = s;
            service.ClearSelection();

            Assert.IsNull(seen);
            Assert.AreEqual(SelectionKind.None, service.CurrentKind);
            Object.DestroyImmediate(goA);
        }

        // Pointing at an object is not selecting it: hover never changes the selection.
        [Test]
        public void Hover_IsIndependentOfSelection()
        {
            var selected = MakeSelectable(SelectionKind.Obstacle, out var goA);
            var hovered = MakeSelectable(SelectionKind.RobotLink, out var goB);

            service.Select(selected);
            service.SetHover(hovered);

            Assert.AreSame(selected, service.Current);
            Assert.AreSame(hovered, service.Hovered);
            Assert.AreEqual(SelectionKind.Obstacle, service.CurrentKind);

            Object.DestroyImmediate(goA);
            Object.DestroyImmediate(goB);
        }

        [Test]
        public void Resolve_FindsSelectableOnAParent()
        {
            MakeSelectable(SelectionKind.EndEffector, out var parent);
            var child = new GameObject("collider-child");
            child.transform.SetParent(parent.transform);

            var resolved = SelectionService.Resolve(child);

            Assert.IsNotNull(resolved);
            Assert.AreEqual(SelectionKind.EndEffector, resolved.Kind);
            Object.DestroyImmediate(parent);
        }

        [Test]
        public void Resolve_ReturnsNullForUnmarkedObjects()
        {
            var plain = new GameObject("plain");
            Assert.IsNull(SelectionService.Resolve(plain));
            Object.DestroyImmediate(plain);
        }

        private static ISelectable MakeSelectable(SelectionKind kind, out GameObject go)
        {
            go = new GameObject(kind.ToString());
            var selectable = go.AddComponent<SelectableMarker>();
            selectable.SetKind(kind);
            return selectable;
        }
    }
}
