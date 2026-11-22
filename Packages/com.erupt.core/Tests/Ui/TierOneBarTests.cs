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
    public class TierOneBarTests
    {
        private GameObject host;
        private TierOneBar bar;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("tier1");
            bar = host.AddComponent<TierOneBar>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [UnityTest]
        public IEnumerator BuildsExactlyFourRegisteredControls()
        {
            yield return null;

            Assert.AreEqual(4, bar.Registry.TierOneControls.Count);
            CollectionAssert.AreEquivalent(
                new[] { "menu", "undo", "redo", "voice" },
                bar.Registry.TierOneControls.Select(c => c.Id).ToArray());
        }

        // The cap has to bite against the real intended contents, not against whatever
        // happens to be built so far.
        [UnityTest]
        public IEnumerator RefusesAFifthControl()
        {
            yield return null;

            Assert.Throws<UiTierViolationException>(
                () => bar.Registry.RegisterTierOne(host.AddComponent<VoiceControlPlaceholder>()));
        }

        [UnityTest]
        public IEnumerator UsesAWorldSpaceCanvas()
        {
            yield return null;

            // Screen-space canvases do not render in a headset.
            Assert.AreEqual(RenderMode.WorldSpace, bar.Canvas.renderMode);
        }

        [UnityTest]
        public IEnumerator UndoAndRedoStartDisabled()
        {
            yield return null;

            Assert.IsFalse(FindButton("Undo").interactable);
            Assert.IsFalse(FindButton("Redo").interactable);
        }

        [UnityTest]
        public IEnumerator UndoBecomesEnabledOnceThereIsSomethingToUndo()
        {
            yield return null;
            bar.UndoStack.Do(new NoOpCommand());

            Assert.IsTrue(FindButton("Undo").interactable);
            Assert.IsFalse(FindButton("Redo").interactable, "Nothing has been undone yet.");
        }

        // Menu, Undo, Redo, in that order, and nothing that names a mode.
        [UnityTest]
        public IEnumerator HoldsMenuUndoRedo_InThatOrder()
        {
            yield return null;

            Button[] buttons = bar.Canvas.GetComponentsInChildren<Button>(true);
            CollectionAssert.AreEqual(new[] { "Menu", "Undo", "Redo" }, buttons.Select(TextOf).ToArray());
            CollectionAssert.AreEqual(new[] { TierOneBar.MenuLabel, TierOneBar.UndoLabel, TierOneBar.RedoLabel }, buttons.Select(TextOf).ToArray());
            Assert.That(buttons.Select(b => b.transform.position.x).ToArray(), Is.Ordered, "Left to right.");
        }

        [UnityTest]
        public IEnumerator MenuButton_RaisesPressed_AndIsAlwaysPressable()
        {
            yield return null;
            int presses = 0;
            bar.Menu.Pressed += () => presses++;
            Button menu = FindButton("Menu");

            Assert.IsTrue(menu.interactable);
            menu.onClick.Invoke();
            menu.onClick.Invoke();
            Assert.AreEqual(2, presses);
        }

        // The bar is as wide as its three buttons, the gaps between them and its margin.
        [UnityTest]
        public IEnumerator TheBarIsSizedToItsContent()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            RectTransform canvas = bar.Canvas.GetComponent<RectTransform>();
            Button[] buttons = bar.Canvas.GetComponentsInChildren<Button>(true);
            Vector2 button = buttons[0].GetComponent<RectTransform>().rect.size;

            Assert.AreEqual(TierOneBar.SizeFor(3, button, 8f, 12f), canvas.sizeDelta);
            Assert.Less(canvas.sizeDelta.x, 560f, "Narrower than the bar that held the mode.");

            // Every button lies inside the canvas, with the margin round the row.
            var corners = new Vector3[4];
            foreach (Button b in buttons)
            {
                b.GetComponent<RectTransform>().GetWorldCorners(corners);
                foreach (Vector3 corner in corners)
                {
                    Vector3 local = canvas.InverseTransformPoint(corner);
                    Assert.That(Mathf.Abs(local.x), Is.LessThanOrEqualTo(canvas.sizeDelta.x * 0.5f - 11.9f), $"{b.name} is inside the bar.");
                    Assert.That(Mathf.Abs(local.y), Is.LessThanOrEqualTo(canvas.sizeDelta.y * 0.5f - 11.9f), $"{b.name} is inside the bar.");
                }
            }
            var collider = bar.Canvas.GetComponent<BoxCollider>();
            Assert.AreEqual(new Vector3(canvas.sizeDelta.x, canvas.sizeDelta.y, 1f), collider.size, "The press-consuming collider is the bar's size.");
        }

        private Button FindButton(string name) =>
            bar.Canvas.GetComponentsInChildren<Button>(true).First(b => b.name == name);

        private static string TextOf(Button button) =>
            button.GetComponentInChildren<TMPro.TextMeshProUGUI>().text;

        private class NoOpCommand : IUndoableCommand
        {
            public string Label => "noop";
            public void Execute() { }
            public void Undo() { }
        }
    }
}
