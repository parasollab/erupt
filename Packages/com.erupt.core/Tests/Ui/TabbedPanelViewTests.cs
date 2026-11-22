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

        // A scene reset: the active robot changes while the robots are unloaded, which asks the
        // robot tabs to rebuild in a scene that is on its way out.
        [UnityTest]
        public IEnumerator ARebuildAskedForWhileTheSceneIsUnloaded_BuildsNothing()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("tier3 scene " + System.Guid.NewGuid().ToString("N"));
            var sceneHost = new GameObject("tier3 in scene");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(sceneHost, scene);
            var scenePanel = sceneHost.AddComponent<TabbedPanelView>();
            yield return null;
            PanelTab tab = scenePanel.AddTab("planner", "Planner");
            int builds = 0;
            System.Action<RectTransform> build = content =>
            {
                builds++;
                new GameObject("control", typeof(RectTransform)).transform.SetParent(content, false);
            };
            tab.Rebuild(build);
            Assert.AreEqual(1, builds, "A rebuild in a live scene builds.");

            var onTheWayOut = new GameObject("leaving").AddComponent<RebuildOnDestroy>();
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(onTheWayOut.gameObject, scene);
            onTheWayOut.Tab = tab;
            onTheWayOut.Build = build;

            // Unity logs an error when objects are left behind by a scene that closes.
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
            yield return null;
            Assert.IsTrue(onTheWayOut.Asked, "The rebuild was asked for during the unload.");
            Assert.AreEqual(1, builds, "Nothing is built in a scene that is being unloaded.");
            LogAssert.NoUnexpectedReceived();
        }

        private sealed class RebuildOnDestroy : MonoBehaviour
        {
            public PanelTab Tab;
            public System.Action<RectTransform> Build;
            public bool Asked;
            private void OnDestroy()
            {
                Asked = true;
                Tab?.Rebuild(Build);
            }
        }

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
        public IEnumerator TabsStandByTheirOrder_WhateverOrderTheyWereAddedIn()
        {
            yield return null;
            // Plugins start in no fixed order; the strip must read the same every session.
            PanelTab late = panel.AddTab("late", "Late");
            late.Order = 30;
            PanelTab plain = panel.AddTab("plain", "Plain");
            PanelTab first = panel.AddTab("first", "First");
            first.Order = 0;
            PanelTab middle = panel.AddTab("middle", "Middle");
            middle.Order = 10;

            CollectionAssert.AreEqual(new[] { "first", "middle", "late", "plain" },
                panel.Tabs.Select(t => t.Id).ToArray(),
                "Lower orders stand further left; a tab without an order comes after them.");
            Assert.AreEqual("first", panel.ActiveTab.Id, "Until a tab is picked the panel shows the leftmost one.");
            Assert.IsTrue(first.Content.gameObject.activeSelf);
            Assert.IsFalse(late.Content.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator ATabThatWasPicked_StaysWhenAnEarlierTabArrives()
        {
            yield return null;
            panel.AddTab("planner", "Planner");
            panel.AddTab("library", "Library");
            panel.SelectTab("library");

            PanelTab scene = panel.AddTab("scene", "Scene");
            scene.Order = 0;

            Assert.AreEqual("scene", panel.Tabs[0].Id);
            Assert.AreEqual("library", panel.ActiveTab.Id, "A tab arriving later does not take the panel from the tab that was picked.");
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

        // A single tabbed panel makes Part 2's one-open-at-a-time invariant structural.
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
