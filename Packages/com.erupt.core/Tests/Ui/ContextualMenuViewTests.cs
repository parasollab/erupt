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

        // A verb that cannot run is not a silent no-op: the refusal the model returns goes to
        // the interaction router, where the toast and the haptics listen.
        [UnityTest]
        public IEnumerator ARefusedVerb_IsReportedOnTheRouter()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            var router = routerGo.AddComponent<InteractionRouter>();
            var refusals = new System.Collections.Generic.List<InteractionRefusal>();
            router.Refused += refusals.Add;
            view.SetRouter(router);
            yield return null;
            try
            {
                selection.Select(MakeObstacle());
                yield return null;

                // A verb of this kind that nothing implements.
                Verb unbound = view.Model.Verbs.First(v => !view.Model.CanInvoke(v));
                InteractionRefusal returned = view.Invoke(unbound);

                Assert.IsTrue(returned.IsRefused);
                Assert.AreEqual(1, refusals.Count, "The menu's refusal reaches the router like any other.");
                Assert.AreEqual(RefusalCode.VerbUnavailable, refusals[0].Code);
                Assert.AreEqual(returned.Reason, refusals[0].Reason);

                // A verb that runs reports nothing.
                bool ran = false;
                view.Bind(unbound.Id, _ => ran = true);
                Verb bound = view.Model.Verbs.First(v => v.Id == unbound.Id);
                if (view.Model.CanInvoke(bound))
                {
                    Assert.IsFalse(view.Invoke(bound).IsRefused);
                    Assert.IsTrue(ran);
                    Assert.AreEqual(1, refusals.Count);
                }
            }
            finally
            {
                Object.DestroyImmediate(routerGo);
            }
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

        // The menu is as tall as its header and buttons and stands on its lower edge above
        // the object: neither it nor the collider that takes presses for it hangs over the
        // object, which can still be pointed at.
        [UnityTest]
        public IEnumerator TheMenu_FitsItsContent_AndStandsAboveTheObject()
        {
            yield return null;
            view.Model.ListsUnavailableVerbs = false;
            view.Bind("delete", _ => { });
            view.Bind("snap", _ => { });
            selection.Select(MakeObstacle());
            yield return null;
            yield return null;

            var rect = view.Canvas.GetComponent<RectTransform>();
            Assert.AreEqual(2, view.Canvas.GetComponentsInChildren<Button>(false).Length);
            Assert.AreEqual(36f + 2 * 70f, rect.sizeDelta.y, 0.01f, "A header and two buttons.");

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float top = target.GetComponent<Renderer>().bounds.max.y;
            foreach (Vector3 corner in corners)
                Assert.Greater(corner.y, top, "No part of the menu is level with the object.");

            var collider = view.Canvas.GetComponent<BoxCollider>();
            Physics.SyncTransforms();
            Assert.Greater(collider.bounds.min.y, top, "Its collider does not cover the object either.");
            Assert.AreEqual(rect.sizeDelta.y * view.Canvas.transform.lossyScale.y, collider.bounds.size.y, 1e-3f);

            // One more verb: the menu grows upwards, its lower edge stays.
            float bottom = corners[0].y;
            view.Bind("duplicate", _ => { });
            yield return null;
            yield return null;
            rect.GetWorldCorners(corners);
            Assert.AreEqual(36f + 3 * 70f, rect.sizeDelta.y, 0.01f);
            Assert.AreEqual(bottom, corners[0].y, 1e-4f);
        }

        // Found against the real stack: when a scene is unloaded with something selected, a
        // plugin that unregisters refreshes the verbs while the selected object is already
        // destroyed. The menu goes; nothing is asked of the dead object.
        [UnityTest]
        public IEnumerator ARefresh_WhileTheSelectedObjectIsDestroyed_ClosesTheMenu_AndThrowsNothing()
        {
            yield return null;
            view.Bind("delete", _ => { });
            bool asked = false;
            view.Model.Registry.ShowWhen(SelectionKind.Obstacle, "delete", _ => { asked = true; return true; });
            selection.Select(MakeObstacle());
            yield return null;
            Assert.IsTrue(view.Canvas.gameObject.activeSelf);

            Object.DestroyImmediate(target);
            asked = false;
            Assert.DoesNotThrow(() => view.Model.Registry.Refresh());

            Assert.IsFalse(view.Canvas.gameObject.activeSelf, "No menu for an object that is gone.");
            Assert.IsEmpty(view.Model.Verbs);
            Assert.IsFalse(asked, "A verb's condition is not asked about a destroyed object.");
        }

        [UnityTest]
        public IEnumerator ARebuild_LeavesOneHeaderAndOneSetOfButtons()
        {
            yield return null;
            ISelectable obstacle = MakeObstacle();
            selection.Select(obstacle);
            yield return null;
            view.Bind("delete", _ => { });
            view.Bind("snap", _ => { });
            selection.ClearSelection();
            selection.Select(obstacle);
            yield return null;

            Assert.AreEqual(1, view.Canvas.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Count(t => t.name == "Header"),
                "Every rebuild used to leave its header behind, one on top of the other.");
            Assert.AreEqual(VerbTable.For(SelectionKind.Obstacle).Count, Buttons().Length);
            Assert.AreSame(Buttons().First(b => b.name == "delete"), view.ButtonFor("delete"));
            Assert.IsNull(view.ButtonFor("no-such-verb"));
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
