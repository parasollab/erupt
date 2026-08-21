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
            host.AddComponent<ModeManager>();
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
                new[] { "mode", "undo", "redo", "voice" },
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

            // Screen-space canvases do not work under PolySpatial (backlog B18).
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

        [UnityTest]
        public IEnumerator ModeButtonShowsAndCyclesTheMode()
        {
            yield return null;
            Button modeButton = FindButton("Mode");

            Assert.AreEqual("Build", TextOf(modeButton));

            modeButton.onClick.Invoke();
            Assert.AreEqual("Plan", TextOf(modeButton), "Mode must be one action from changing.");
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
