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
    /// <summary>
    /// The tier 3 Settings tab: made of the sections that components contribute, last in the
    /// strip, and not there at all while nothing has contributed one. Tier 1's Menu button
    /// opens and closes the panel like the controller's Menu button.
    /// </summary>
    public class SettingsTabTests
    {
        private GameObject go;
        private TierUiRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            go = new GameObject("ERUPT");
            go.AddComponent<SelectionService>();
            rig = go.AddComponent<TierUiRig>();
            yield return null;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(go);

        private string[] Strip() => rig.TierThree.Tabs.Select(t => t.Id).ToArray();

        [Test]
        public void WithoutASection_ThereIsNoSettingsTab()
        {
            Assert.IsNotNull(rig.Settings);
            Assert.IsNull(rig.Settings.Tab);
            Assert.IsEmpty(Strip(), "No setting, no tab: nothing dead to press.");
            Assert.AreEqual(0, rig.Settings.InteractiveElementCount);
        }

        [Test]
        public void TheFirstSection_MakesTheTab_AndItStandsLast()
        {
            rig.AddTab("scene", "Scene", null).Order = 0;
            PanelTab ar = rig.AddTab("ar", "AR", null);
            ar.Order = 40;

            rig.Settings.AddSection("haptics", column =>
                UiBuilder.CreateSlider("Strength", column, "Strength", 0f, 1f, 1f, new Vector2(300f, 76f), _ => { }));

            // A plugin that sets no order, added after the Settings tab.
            rig.AddTab("unordered", "Plugin", null);

            CollectionAssert.AreEqual(new[] { "scene", "ar", "unordered", SettingsTab.TabId }, Strip(), "Settings is the last tab of the strip.");
            Assert.AreEqual("Settings", rig.Settings.Tab.Label);
            Assert.Greater(SettingsTab.TabOrder, PanelTab.DefaultOrder);
            Assert.AreEqual(1, rig.Settings.InteractiveElementCount);
            Assert.AreEqual("scene", rig.TierThree.ActiveTab.Id, "The panel still opens on its first tab.");
        }

        [UnityTest]
        public IEnumerator Sections_StandInTheOrderTheyWereAdded_AndTheLastOneTakesTheTabWithIt()
        {
            rig.AddTab("scene", "Scene", null).Order = 0;
            rig.Settings.AddSection("one", column => UiBuilder.CreateButton("One", column, "One", new Vector2(300f, 56f)));
            rig.Settings.AddSection("two", column => UiBuilder.CreateButton("Two", column, "Two", new Vector2(300f, 56f)));
            yield return null;

            CollectionAssert.AreEqual(new[] { "one", "two" }, rig.Settings.Sections.ToArray());
            CollectionAssert.AreEqual(new[] { "One", "Two" },
                rig.Settings.Tab.Content.GetComponentsInChildren<Button>(true).Select(b => b.name).ToArray());

            // The same id again replaces the section; it is not listed twice.
            rig.Settings.AddSection("one", column => UiBuilder.CreateButton("Uno", column, "Uno", new Vector2(300f, 56f)));
            yield return null;
            CollectionAssert.AreEqual(new[] { "Uno", "Two" },
                rig.Settings.Tab.Content.GetComponentsInChildren<Button>(true).Select(b => b.name).ToArray());

            Assert.IsTrue(rig.Settings.RemoveSection("one"));
            yield return null;
            CollectionAssert.AreEqual(new[] { "Two" },
                rig.Settings.Tab.Content.GetComponentsInChildren<Button>(true).Select(b => b.name).ToArray());
            CollectionAssert.Contains(Strip(), SettingsTab.TabId);

            // Open on Settings when its last section goes: the panel falls back to the first tab.
            rig.SummonTab(SettingsTab.TabId);
            Assert.AreEqual(SettingsTab.TabId, rig.TierThree.ActiveTab.Id);
            Assert.IsTrue(rig.Settings.RemoveSection("two"));
            yield return null;
            CollectionAssert.AreEqual(new[] { "scene" }, Strip(), "The tab went with its last section.");
            Assert.IsNull(rig.Settings.Tab);
            Assert.AreEqual("scene", rig.TierThree.ActiveTab.Id);
            Assert.IsFalse(rig.Settings.RemoveSection("two"));
            Assert.IsNull(rig.TierThree.Canvas.transform.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Tab_settings"),
                "Its header is gone from the strip.");
        }

        // A section that needs the room (the ROS address keypad) has the tab to itself for a while.
        [UnityTest]
        public IEnumerator OneSection_CanHaveTheTabToItself_AndGiveItBack()
        {
            int builtOne = 0, builtTwo = 0;
            rig.Settings.AddSection("one", column => { builtOne++; UiBuilder.CreateButton("One", column, "One", new Vector2(300f, 56f)); });
            rig.Settings.AddSection("two", column => { builtTwo++; UiBuilder.CreateButton("Two", column, "Two", new Vector2(300f, 56f)); });
            yield return null;
            string[] Shown() => rig.Settings.Tab.Content.GetComponentsInChildren<Button>(true).Select(b => b.name).ToArray();
            Assert.IsNull(rig.Settings.Showing);

            rig.Settings.ShowOnly("two");
            yield return null;
            Assert.AreEqual("two", rig.Settings.Showing);
            CollectionAssert.AreEqual(new[] { "Two" }, Shown());
            Assert.AreEqual(1, rig.Settings.InteractiveElementCount);
            CollectionAssert.AreEqual(new[] { "one", "two" }, rig.Settings.Sections.ToArray(), "The other section is still a section.");

            // Asking again builds nothing again.
            int before = builtTwo;
            rig.Settings.ShowOnly("two");
            Assert.AreEqual(before, builtTwo);

            rig.Settings.ShowOnly(null);
            yield return null;
            Assert.IsNull(rig.Settings.Showing);
            CollectionAssert.AreEqual(new[] { "One", "Two" }, Shown(), "In the order they were added, as before.");
            Assert.Greater(builtOne, 1);

            // A section that is not there cannot have the tab; one that leaves gives it back.
            rig.Settings.ShowOnly("nobody");
            Assert.IsNull(rig.Settings.Showing);
            rig.Settings.ShowOnly("one");
            Assert.IsTrue(rig.Settings.RemoveSection("one"));
            yield return null;
            Assert.IsNull(rig.Settings.Showing);
            CollectionAssert.AreEqual(new[] { "Two" }, Shown());
        }

        [UnityTest]
        public IEnumerator RemovingATab_TakesItsHeaderAndContent_AndAnEmptyPanelCloses()
        {
            rig.AddTab("only", "Only", content => UiBuilder.CreateButton("Inside", content, "Inside", new Vector2(300f, 56f)));
            rig.SummonTab("only");
            Assert.IsTrue(rig.TierThree.IsOpen);

            Assert.IsTrue(rig.RemoveTab("only"));
            yield return null;

            Assert.IsEmpty(Strip());
            Assert.IsNull(rig.TierThree.ActiveTab);
            Assert.IsFalse(rig.TierThree.IsOpen, "A panel without tabs has nothing to show.");
            Assert.IsEmpty(rig.TierThree.Canvas.GetComponentsInChildren<Button>(true));
            Assert.IsFalse(rig.RemoveTab("only"));

            // The id is free again.
            Assert.DoesNotThrow(() => rig.AddTab("only", "Only", null));
        }

        // Five or six tabs share the panel's width; a title is drawn smaller rather than over its neighbours.
        [UnityTest]
        public IEnumerator TabTitles_FitTheirHeaders_HoweverManyTabsThereAre()
        {
            foreach (string title in new[] { "Scene", "MoveIt", "MTC", "Demonstrations", "AR", "Settings" })
                rig.AddTab(title.ToLowerInvariant(), title, null);
            rig.SummonTab("scene");
            yield return null;
            Canvas.ForceUpdateCanvases();

            float panelWidth = rig.TierThree.Canvas.GetComponent<RectTransform>().rect.width;
            float used = 0f;
            foreach (PanelTab tab in rig.TierThree.Tabs)
            {
                Button header = rig.TierThree.Canvas.GetComponentsInChildren<Button>(true).First(b => b.name == $"Tab_{tab.Id}");
                var label = header.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                label.ForceMeshUpdate();
                float width = ((RectTransform)header.transform).rect.width;
                used += width;
                Assert.IsTrue(label.enableAutoSizing);
                Assert.LessOrEqual(label.textBounds.size.x, width + 0.5f, $"'{tab.Label}' fits its header ({width:F0} wide).");
                Assert.GreaterOrEqual(label.fontSize, 14f - 1e-3f, $"'{tab.Label}' is still readable.");
            }
            Assert.LessOrEqual(used, panelWidth + 0.5f, "The headers share the panel's width.");
        }

        [UnityTest]
        public IEnumerator TierOnesMenuButton_OpensAndClosesThePanel_AsTheActivateIntentDoes()
        {
            rig.AddTab("scene", "Scene", null);
            Button menu = rig.TierOne.Canvas.GetComponentsInChildren<Button>(true).First(b => b.name == "Menu");
            Assert.IsFalse(rig.TierThree.IsOpen);

            menu.onClick.Invoke();
            Assert.IsTrue(rig.TierThree.IsOpen, "Menu opens tier 3.");
            Assert.AreEqual("scene", rig.TierThree.ActiveTab.Id);
            Assert.AreSame(rig.TierThree, rig.Registry.OpenPanel, "Through the registry, like every summon.");

            menu.onClick.Invoke();
            Assert.IsFalse(rig.TierThree.IsOpen, "Menu closes it again.");

            // The same toggle the router's Activate intent drives (the controller's Menu button).
            rig.ToggleTierThree();
            Assert.IsTrue(rig.TierThree.IsOpen);
            menu.onClick.Invoke();
            Assert.IsFalse(rig.TierThree.IsOpen, "What one opened the other closes: they are one toggle.");
            yield return null;
        }

        [Test]
        public void WithoutATab_MenuDoesNothing()
        {
            Button menu = rig.TierOne.Canvas.GetComponentsInChildren<Button>(true).First(b => b.name == "Menu");
            Assert.DoesNotThrow(() => menu.onClick.Invoke());
            Assert.IsFalse(rig.TierThree.IsOpen);
        }
    }
}
