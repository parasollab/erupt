using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    public class ContextualMenuViewTests
    {
        private GameObject host;
        private GameObject selectionHost;
        private SelectionService selection;
        private ContextualMenuView view;
        private GameObject target;

        [SetUp]
        public void SetUp()
        {
            selectionHost = new GameObject("selection");
            selection = selectionHost.AddComponent<SelectionService>();

            host = new GameObject("tier2");
            view = host.AddComponent<ContextualMenuView>();
        }

        [TearDown]
        public void TearDown()
        {
            if (target != null) Object.DestroyImmediate(target);
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(selectionHost);
        }

        [UnityTest]
        public IEnumerator HiddenWhenNothingIsSelected()
        {
            yield return null;
            Assert.IsFalse(view.Canvas.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator AppearsOnSelectAndDisappearsOnDeselect()
        {
            yield return null;

            selection.Select(MakeObstacle());
            yield return null;
            Assert.IsTrue(view.Canvas.gameObject.activeSelf, "Tier 2 must appear on select.");

            selection.ClearSelection();
            yield return null;
            Assert.IsFalse(view.Canvas.gameObject.activeSelf, "Tier 2 must disappear on deselect.");
        }

        [UnityTest]
        public IEnumerator ShowsEveryVerbForTheKind()
        {
            yield return null;
            selection.Select(MakeObstacle());
            yield return null;

            var ids = Buttons().Select(b => b.name).ToArray();
            CollectionAssert.AreEquivalent(
                VerbTable.For(SelectionKind.Obstacle).Select(v => v.Id).ToArray(), ids);
        }

        // Unbuilt verbs stay visible so the vocabulary is legible, but must not be
        // pressable - a button that silently does nothing is the failure Part 3 warns about.
        [UnityTest]
        public IEnumerator UnbuiltVerbsRenderDisabled()
        {
            yield return null;
            selection.Select(MakeObstacle());
            yield return null;

            Button paintCost = Buttons().First(b => b.name == "paint-cost");
            Assert.IsFalse(paintCost.interactable);
        }

        [UnityTest]
        public IEnumerator AvailableButUnboundVerbsAlsoRenderDisabled()
        {
            yield return null;
            selection.Select(MakeObstacle());
            yield return null;

            Assert.IsFalse(Buttons().First(b => b.name == "delete").interactable,
                "Nothing is bound to delete yet, so it must not look pressable.");
        }

        [UnityTest]
        public IEnumerator BoundVerbBecomesPressableAndInvokes()
        {
            yield return null;
            ISelectable obstacle = MakeObstacle();

            ISelectable acted = null;
            view.Bind("delete", s => acted = s);

            selection.Select(obstacle);
            yield return null;

            Button delete = Buttons().First(b => b.name == "delete");
            Assert.IsTrue(delete.interactable);

            delete.onClick.Invoke();
            Assert.AreSame(obstacle, acted);
        }

        // Part 8 design review: no more than ~7 interactive elements with an object
        // selected. Disabled verbs do not count, which is what keeps an obstacle - the
        // richest row in the table - inside the budget alongside tier 1.
        [UnityTest]
        public IEnumerator InteractiveElementCountStaysWithinTheDesignBudget()
        {
            yield return null;

            foreach (var verb in VerbTable.AvailableFor(SelectionKind.Obstacle))
                view.Bind(verb.Id, _ => { });

            selection.Select(MakeObstacle());
            yield return null;

            Assert.LessOrEqual(view.InteractiveElementCount, 4,
                "Tier 2 must leave room for tier 1's controls inside the ~7 budget.");
        }

        [UnityTest]
        public IEnumerator MenuIsPositionedClearOfTheObject()
        {
            yield return null;
            selection.Select(MakeObstacle());
            yield return null;
            yield return null;

            var renderer = target.GetComponent<Renderer>();
            Assert.Greater(view.Canvas.transform.position.y, renderer.bounds.max.y - 0.01f,
                "The menu must clear the object rather than sit inside it.");
        }

        private Button[] Buttons() =>
            view.Canvas.GetComponentsInChildren<Button>(true);

        private ISelectable MakeObstacle()
        {
            target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var marker = target.AddComponent<SelectableMarker>();
            marker.SetKind(SelectionKind.Obstacle);
            return marker;
        }
    }
}
