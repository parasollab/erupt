using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// The collision haptics as the user sets them while using the app: a switch, a strength
    /// and a distance, applied from the next frame, kept between sessions, and shown on the
    /// tier 3 Settings tab by the component itself (so a scene without it has no such controls).
    /// </summary>
    public class CollisionHapticsSettingsTests
    {
        private sealed class Tip : IProximitySource
        {
            public Vector3 Point;
            public void GetProbePoints(List<Vector3> points) => points.Add(Point);
            public bool IsOwnCollider(Collider collider) => false;
        }

        private MemoryUserSettingsStore store;
        private GameObject routerGo, hapticsGo, uiGo, obstacle;
        private InteractionRouter router;
        private CollisionHaptics haptics;
        private TierUiRig rig;
        private readonly Tip tip = new Tip();

        [SetUp]
        public void SetUp()
        {
            store = new MemoryUserSettingsStore();
            UserSettings.Store = store;
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            router.AddProximitySource(tip);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(hapticsGo);
            Object.DestroyImmediate(uiGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(obstacle);
            UserSettings.Store = null;
        }

        private CollisionHaptics MakeHaptics()
        {
            hapticsGo = new GameObject("haptics");
            haptics = hapticsGo.AddComponent<CollisionHaptics>();
            haptics.SetRouter(router);
            return haptics;
        }

        private void MakeUi()
        {
            uiGo = new GameObject("ERUPT");
            uiGo.AddComponent<SelectionService>();
            rig = uiGo.AddComponent<TierUiRig>();
        }

        // An obstacle whose near face is this far from the tip.
        private void ObstacleAt(float distance)
        {
            if (obstacle == null)
            {
                obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obstacle.transform.localScale = Vector3.one * 0.1f;
            }
            tip.Point = new Vector3(40f, 0f, 0f);
            obstacle.transform.position = tip.Point + Vector3.forward * (distance + 0.05f);
            Physics.SyncTransforms();
        }

        private T Control<T>(string name) where T : Component =>
            rig.Settings.Tab.Content.GetComponentsInChildren<T>(true).First(c => c.name == name);

        private static string TextOf(Component control) => control.GetComponentInChildren<TextMeshProUGUI>().text;

        [Test]
        public void TheSerializedValues_AreTheDefaults()
        {
            MakeHaptics();
            Assert.IsTrue(haptics.HapticsEnabled);
            Assert.AreEqual(1f, haptics.Strength, 1e-6f);
            Assert.AreEqual(0.3f, haptics.Distance, 1e-6f);
            Assert.IsFalse(store.Has(CollisionHaptics.StrengthKey), "Nothing is written until the user changes something.");
        }

        [Test]
        public void StrengthAndDistance_ShapeThePulse_AndTheSwitchSilencesIt()
        {
            MakeHaptics();
            Assert.AreEqual(1f, haptics.IntensityAt(0f), 1e-5f, "Full strength at contact.");
            Assert.AreEqual(0.25f, haptics.IntensityAt(0.15f), 1e-5f, "Half way in, a quarter (the ramp is quadratic).");
            Assert.AreEqual(0f, haptics.IntensityAt(0.3f), 1e-6f, "Nothing at the threshold and beyond.");

            haptics.Strength = 0.4f;
            Assert.AreEqual(0.4f, haptics.IntensityAt(0f), 1e-5f);
            Assert.AreEqual(0.1f, haptics.IntensityAt(0.15f), 1e-5f);

            haptics.Distance = 0.1f;
            Assert.AreEqual(0f, haptics.IntensityAt(0.15f), 1e-6f, "15 cm is outside a 10 cm threshold.");
            Assert.AreEqual(0.1f, haptics.IntensityAt(0.05f), 1e-5f);

            haptics.HapticsEnabled = false;
            Assert.AreEqual(0f, haptics.IntensityAt(0f), 1e-6f, "Switched off, there is no pulse at any distance.");

            // The values stay inside their ranges.
            haptics.Strength = 7f;
            Assert.AreEqual(1f, haptics.Strength, 1e-6f);
            haptics.Strength = -1f;
            Assert.AreEqual(0f, haptics.Strength, 1e-6f);
            haptics.Distance = 0f;
            Assert.AreEqual(CollisionHaptics.NearestDistance, haptics.Distance, 1e-6f);
            haptics.Distance = 5f;
            Assert.AreEqual(CollisionHaptics.FarthestDistance, haptics.Distance, 1e-6f);
        }

        [UnityTest]
        public IEnumerator AChange_AppliesFromTheNextFrame()
        {
            MakeHaptics();
            ObstacleAt(0.15f);
            yield return null;
            yield return null;
            Assert.AreEqual(0.15f, haptics.LastClosestDistance, 2e-3f);
            Assert.AreEqual(0.25f, haptics.LastIntensity, 0.01f, "Near an obstacle the controllers are asked to pulse.");

            haptics.Strength = 0.5f;
            yield return null;
            Assert.AreEqual(0.125f, haptics.LastIntensity, 0.01f, "Half the strength, half the pulse.");

            haptics.Distance = 0.5f;
            yield return null;
            Assert.AreEqual(0.5f * 0.7f * 0.7f, haptics.LastIntensity, 0.01f, "A farther threshold: the same obstacle is deeper inside it.");

            haptics.Distance = 0.1f;
            yield return null;
            Assert.AreEqual(0f, haptics.LastIntensity, 1e-6f, "A nearer threshold: the obstacle is outside it.");

            haptics.Distance = 0.3f;
            haptics.HapticsEnabled = false;
            yield return null;
            Assert.AreEqual(0f, haptics.LastIntensity, 1e-6f, "Switched off: no pulse.");
            Assert.IsTrue(float.IsPositiveInfinity(haptics.LastClosestDistance), "And nothing is measured.");

            haptics.HapticsEnabled = true;
            yield return null;
            Assert.AreEqual(0.125f, haptics.LastIntensity, 0.01f, "Switched on again, with the strength it had.");
        }

        [Test]
        public void WhatTheUserSets_IsKeptBetweenSessions()
        {
            MakeHaptics();
            int changes = 0;
            haptics.SettingsChanged += () => changes++;
            haptics.HapticsEnabled = false;
            haptics.Strength = 0.35f;
            haptics.Distance = 0.12f;
            Assert.AreEqual(3, changes);
            haptics.Strength = 0.35f;
            Assert.AreEqual(3, changes, "Setting the same value is no change.");

            Assert.IsTrue(store.Has(CollisionHaptics.EnabledKey) && store.Has(CollisionHaptics.StrengthKey) && store.Has(CollisionHaptics.DistanceKey));
            int saves = store.Saves;
            Object.DestroyImmediate(hapticsGo);
            Assert.Greater(store.Saves, saves, "The settings are written out when the component goes.");

            // The next session: another component, the same store.
            CollisionHaptics next = MakeHaptics();
            Assert.IsFalse(next.HapticsEnabled);
            Assert.AreEqual(0.35f, next.Strength, 1e-6f);
            Assert.AreEqual(0.12f, next.Distance, 1e-6f);

            // A stored value outside the range (an older build, a hand-edited file) is brought inside it.
            store.SetFloat(CollisionHaptics.DistanceKey, 9f);
            store.SetFloat(CollisionHaptics.StrengthKey, -3f);
            Object.DestroyImmediate(hapticsGo);
            next = MakeHaptics();
            Assert.AreEqual(CollisionHaptics.FarthestDistance, next.Distance, 1e-6f);
            Assert.AreEqual(0f, next.Strength, 1e-6f);
        }

        [UnityTest]
        public IEnumerator TheComponent_PutsItsControlsOnTheSettingsTab_AndTakesThemAwayWithIt()
        {
            MakeUi();
            rig.AddTab("scene", "Scene", null).Order = 0;
            yield return null;
            Assert.IsNull(rig.Settings.Tab, "A scene without collision haptics has no Settings tab.");

            MakeHaptics();
            yield return null;   // Start: the component finds the tier UI by itself
            yield return null;

            Assert.IsNotNull(rig.Settings.Tab, "Nothing was wired: the component found the tier UI.");
            CollectionAssert.AreEqual(new[] { "scene", SettingsTab.TabId }, rig.TierThree.Tabs.Select(t => t.Id).ToArray(), "Settings stands last.");
            CollectionAssert.AreEqual(new[] { CollisionHaptics.SettingsSection }, rig.Settings.Sections.ToArray());
            Assert.AreEqual(3, rig.Settings.InteractiveElementCount, "A switch and two sliders (Part 8: at most 7).");

            var toggle = Control<Button>("CollisionHaptics");
            var strength = Control<Slider>("CollisionHapticsStrength");
            var distance = Control<Slider>("CollisionHapticsDistance");
            Assert.AreEqual("Collision haptics: on", TextOf(toggle));
            Assert.AreEqual("Strength: 100%", TextOf(strength));
            Assert.AreEqual("Distance: 30 cm", TextOf(distance));
            Assert.AreEqual(0f, strength.minValue, 1e-6f);
            Assert.AreEqual(1f, strength.maxValue, 1e-6f);
            Assert.AreEqual(0.05f, distance.minValue, 1e-6f);
            Assert.AreEqual(0.5f, distance.maxValue, 1e-6f);

            // The sliders set the component, in the steps the tab shows.
            strength.value = 0.62f;
            Assert.AreEqual(0.6f, haptics.Strength, 1e-5f);
            Assert.AreEqual("Strength: 60%", TextOf(strength));
            distance.value = 0.1049f;
            Assert.AreEqual(0.10f, haptics.Distance, 1e-5f);
            Assert.AreEqual("Distance: 10 cm", TextOf(distance));
            Assert.AreEqual(0.6f, store.TryGetFloat(CollisionHaptics.StrengthKey, out float kept) ? kept : -1f, 1e-5f, "And it is remembered.");

            // The switch.
            toggle.onClick.Invoke();
            Assert.IsFalse(haptics.HapticsEnabled);
            Assert.AreEqual("Collision haptics: off", TextOf(toggle));
            Assert.IsFalse(strength.interactable, "With the haptics off the sliders are greyed out.");
            Assert.IsFalse(distance.interactable);
            Assert.AreEqual("Strength: 60%", TextOf(strength), "Their values stay readable.");
            toggle.onClick.Invoke();
            Assert.IsTrue(haptics.HapticsEnabled);
            Assert.IsTrue(strength.interactable);

            // A change made in code shows on the tab.
            haptics.Distance = 0.25f;
            Assert.AreEqual(0.25f, distance.value, 1e-5f);
            Assert.AreEqual("Distance: 25 cm", TextOf(distance));

            // Without the component the controls are gone, and the tab with them.
            Object.DestroyImmediate(hapticsGo);
            yield return null;
            Assert.IsNull(rig.Settings.Tab);
            CollectionAssert.AreEqual(new[] { "scene" }, rig.TierThree.Tabs.Select(t => t.Id).ToArray());
        }

        // The component may start before the tier UI has woken (another scene object, a later frame).
        [UnityTest]
        public IEnumerator ATierUiThatWakesLater_StillGetsTheControls()
        {
            uiGo = new GameObject("ERUPT");
            uiGo.SetActive(false);
            uiGo.AddComponent<SelectionService>();
            rig = uiGo.AddComponent<TierUiRig>();
            MakeHaptics();
            haptics.ShowInSettings(rig);
            yield return null;
            Assert.IsNull(rig.Settings, "The rig has not built its tiers yet.");

            uiGo.SetActive(true);
            yield return null;
            yield return null;
            Assert.IsNotNull(rig.Settings.Tab);
            Assert.AreEqual(3, rig.Settings.InteractiveElementCount);
        }
    }
}
