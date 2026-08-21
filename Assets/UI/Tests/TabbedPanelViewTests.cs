using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>Tier 3 behaviour. Guidelines Part 2 and Part 6.</summary>
    public class TabbedPanelViewTests
    {
        private GameObject host;
        private TabbedPanelView panel;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("tier3");
            panel = host.AddComponent<TabbedPanelView>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        // Part 2: "none open by default".
        [UnityTest]
        public IEnumerator ClosedByDefault()
        {
            yield return null;

            Assert.IsFalse(panel.IsOpen);
            Assert.IsFalse(panel.Canvas.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator OpenAndCloseToggleVisibility()
        {
            yield return null;

            panel.Open();
            Assert.IsTrue(panel.IsOpen);
            Assert.IsTrue(panel.Canvas.gameObject.activeSelf);

            panel.Close();
            Assert.IsFalse(panel.IsOpen);
            Assert.IsFalse(panel.Canvas.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator FirstTabAddedBecomesActive()
        {
            yield return null;
            panel.AddTab("planner", "Planner");

            Assert.AreEqual("planner", panel.ActiveTab.Id);
            Assert.IsTrue(panel.ActiveTab.Content.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator OnlyTheActiveTabsContentIsVisible()
        {
            yield return null;
            var planner = panel.AddTab("planner", "Planner");
            var library = panel.AddTab("library", "Library");

            Assert.IsTrue(planner.Content.gameObject.activeSelf);
            Assert.IsFalse(library.Content.gameObject.activeSelf);

            panel.SelectTab("library");

            Assert.IsFalse(planner.Content.gameObject.activeSelf);
            Assert.IsTrue(library.Content.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator DuplicateTabIdIsRejected()
        {
            yield return null;
            panel.AddTab("planner", "Planner");

            Assert.Throws<UiTierViolationException>(() => panel.AddTab("planner", "Planner Again"));
        }

        [UnityTest]
        public IEnumerator TabChangedFiresOnRealChangesOnly()
        {
            yield return null;
            panel.AddTab("planner", "Planner");
            panel.AddTab("library", "Library");

            int changes = 0;
            panel.TabChanged += _ => changes++;

            panel.SelectTab("library");
            panel.SelectTab("library");

            Assert.AreEqual(1, changes);
        }

        // A single tabbed panel makes Part 2's one-open-at-a-time invariant structural,
        // and matches Part 6's single visionOS window so the port is a re-skin.
        [UnityTest]
        public IEnumerator RegistryTreatsItAsTheOnlySummonedPanel()
        {
            yield return null;
            var registry = new UiTierRegistry();
            registry.RegisterPanel(panel);

            registry.Open(panel);
            Assert.AreSame(panel, registry.OpenPanel);

            registry.CloseAll();
            Assert.IsNull(registry.OpenPanel);
        }

        [UnityTest]
        public IEnumerator ActiveTabHeaderIsNotPressable()
        {
            yield return null;
            panel.AddTab("planner", "Planner");
            panel.AddTab("library", "Library");

            var headers = panel.Canvas.GetComponentsInChildren<UnityEngine.UI.Button>(true)
                               .Where(b => b.name.StartsWith("Tab_")).ToArray();

            Assert.AreEqual(1, headers.Count(b => !b.interactable),
                "Exactly the active tab's header should be non-pressable.");
        }

        [UnityTest]
        public IEnumerator UsesAWorldSpaceCanvas()
        {
            yield return null;
            Assert.AreEqual(RenderMode.WorldSpace, panel.Canvas.renderMode);
        }
    }
}
