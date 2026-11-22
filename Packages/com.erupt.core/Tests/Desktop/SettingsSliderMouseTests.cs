using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// The Settings tab worked with a simulated mouse, through an event system with the XR UI
    /// input module the scenes use (it serves the mouse, the XR rays and the poke alike):
    /// pressing on a slider puts its handle under the pointer, dragging moves it, and the
    /// value is the collision haptics' own from that moment.
    /// </summary>
    public class SettingsSliderMouseTests
    {
        private readonly InputTestFixture input = new InputTestFixture();
        private Mouse mouse;

        private GameObject cameraGo, eventsGo, uiGo, routerGo, hapticsGo;
        private Camera cam;
        private TierUiRig rig;
        private CollisionHaptics haptics;
        private MemoryUserSettingsStore store;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            input.Setup();
            // See DesktopMouseBackendTests: the fixture's clock has to start past the moment play mode began.
            input.currentTime = 1e6;
            mouse = InputSystem.AddDevice<Mouse>();
            store = new MemoryUserSettingsStore();
            UserSettings.Store = store;

            cameraGo = new GameObject("camera") { tag = "MainCamera" };
            cam = cameraGo.AddComponent<Camera>();
            cameraGo.transform.SetPositionAndRotation(new Vector3(80f, 1f, -3f), Quaternion.identity);

            eventsGo = new GameObject("events", typeof(EventSystem), typeof(XRUIInputModule));

            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            routerGo.AddComponent<InteractionRouter>();
            uiGo = new GameObject("ERUPT");
            uiGo.AddComponent<SelectionService>();
            rig = uiGo.AddComponent<TierUiRig>();
            hapticsGo = new GameObject("haptics");
            haptics = hapticsGo.AddComponent<CollisionHaptics>();

            yield return null;   // the component adds its section to the Settings tab
            yield return null;
            Assert.IsNotNull(rig.Settings.Tab, "The collision haptics put their controls on the Settings tab.");
            rig.SummonTab(SettingsTab.TabId);
            yield return null;
            yield return null;
            Canvas.ForceUpdateCanvases();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(hapticsGo);
            Object.DestroyImmediate(uiGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(eventsGo);
            Object.DestroyImmediate(cameraGo);
            UserSettings.Store = null;
            input.TearDown();
        }

        private T Control<T>(string name) where T : Component =>
            rig.Settings.Tab.Content.GetComponentsInChildren<T>(true).First(c => c.name == name);

        private static string TextOf(Component control) => control.GetComponentInChildren<TextMeshProUGUI>().text;

        // The screen point over a slider at a fraction of its travel (0 = left end, 1 = right end).
        private Vector2 OnSlider(Slider slider, float fraction)
        {
            var corners = new Vector3[4];
            ((RectTransform)slider.handleRect.parent).GetWorldCorners(corners);
            Vector3 left = (corners[0] + corners[1]) * 0.5f, right = (corners[2] + corners[3]) * 0.5f;
            Vector3 view = cam.WorldToViewportPoint(Vector3.Lerp(left, right, fraction));
            Assert.IsTrue(view.z > 0f && view.x > 0f && view.x < 1f && view.y > 0f && view.y < 1f, $"The slider is in the view ({view}).");
            return cam.WorldToScreenPoint(Vector3.Lerp(left, right, fraction));
        }

        private static float ValueAt(Slider slider, float fraction) => Mathf.Lerp(slider.minValue, slider.maxValue, fraction);

        private IEnumerator Press(Vector2 screen)
        {
            input.Move(mouse.position, screen);
            yield return null;
            input.Press(mouse.leftButton);
            yield return null;
            yield return null;
        }

        private IEnumerator DragTo(Vector2 from, Vector2 to, int steps = 6)
        {
            for (int i = 1; i <= steps; i++)
            {
                input.Move(mouse.position, Vector2.Lerp(from, to, i / (float)steps));
                yield return null;
            }
            yield return null;
        }

        private IEnumerator Release()
        {
            input.Release(mouse.leftButton);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator DraggingTheDistanceSlider_ChangesTheValue_AndItReachesTheHaptics()
        {
            Slider distance = Control<Slider>("CollisionHapticsDistance");
            Assert.AreEqual(0.3f, haptics.Distance, 1e-6f);
            Assert.AreEqual("Distance: 30 cm", TextOf(distance));
            float pixelsOfTravel = Vector2.Distance(OnSlider(distance, 0f), OnSlider(distance, 1f));
            float tolerance = 0.45f / pixelsOfTravel * 2f + 0.011f;   // two pixels and a step

            // A press on the groove puts the handle under the pointer.
            Vector2 from = OnSlider(distance, 0.2f);
            yield return Press(from);
            Assert.AreEqual(ValueAt(distance, 0.2f), haptics.Distance, tolerance, "The press set the value where the pointer is.");
            Assert.AreEqual(haptics.Distance, distance.value, 1e-5f);

            // Dragging moves it; every value on the way is the haptics' own at once.
            Vector2 to = OnSlider(distance, 0.8f);
            float before = haptics.Distance;
            yield return DragTo(from, Vector2.Lerp(from, to, 0.5f));
            Assert.Greater(haptics.Distance, before + 0.05f, "The value follows the drag while the button is held.");
            Assert.AreEqual(ValueAt(distance, 0.5f), haptics.Distance, tolerance);
            yield return DragTo(Vector2.Lerp(from, to, 0.5f), to);
            yield return Release();

            Assert.AreEqual(ValueAt(distance, 0.8f), haptics.Distance, tolerance, "The value is where the drag ended.");
            Assert.AreEqual(haptics.Distance, distance.value, 1e-5f);
            Assert.AreEqual($"Distance: {Mathf.RoundToInt(haptics.Distance * 100f)} cm", TextOf(distance), "The slider shows its value.");
            Assert.AreEqual(Mathf.Round(haptics.Distance * 100f), haptics.Distance * 100f, 1e-3f, "In whole centimetres.");
            Assert.IsTrue(store.TryGetFloat(CollisionHaptics.DistanceKey, out float kept));
            Assert.AreEqual(haptics.Distance, kept, 1e-6f, "And it is kept for the next session.");

            // The pulse for an obstacle 20 cm away follows: nothing with a 14 cm threshold, something with 41 cm.
            Assert.Greater(haptics.IntensityAt(0.2f), 0f);
            yield return Press(to);
            yield return DragTo(to, from);
            yield return Release();
            Assert.AreEqual(ValueAt(distance, 0.2f), haptics.Distance, tolerance, "Dragged back down.");
            Assert.AreEqual(0f, haptics.IntensityAt(0.2f), 1e-6f);

            // Moving the pointer with the button up changes nothing.
            float settled = haptics.Distance;
            input.Move(mouse.position, to);
            yield return null;
            yield return null;
            Assert.AreEqual(settled, haptics.Distance, 1e-6f);
        }

        [UnityTest]
        public IEnumerator TheStrengthSlider_AndTheSwitch_TakeTheMouseToo()
        {
            Slider strength = Control<Slider>("CollisionHapticsStrength");
            Button toggle = Control<Button>("CollisionHaptics");
            Assert.AreEqual(1f, haptics.Strength, 1e-6f);

            yield return Press(OnSlider(strength, 1f));
            yield return DragTo(OnSlider(strength, 1f), OnSlider(strength, 0.3f));
            yield return Release();
            Assert.AreEqual(0.3f, haptics.Strength, 0.051f, "Dragged down to about 30%.");
            Assert.AreEqual($"Strength: {Mathf.RoundToInt(haptics.Strength * 100f)}%", TextOf(strength));
            Assert.AreEqual(haptics.Strength, haptics.IntensityAt(0f), 1e-5f, "The pulse at contact is the strength the slider shows.");

            // The switch: one click off, the sliders greyed out and deaf; one click on again.
            Vector2 onToggle = cam.WorldToScreenPoint(toggle.transform.position);
            yield return Press(onToggle);
            yield return Release();
            Assert.IsFalse(haptics.HapticsEnabled, "A click on the switch turns the haptics off.");
            Assert.AreEqual("Collision haptics: off", TextOf(toggle));
            float off = haptics.Strength;
            yield return Press(OnSlider(strength, 0.9f));
            yield return Release();
            Assert.AreEqual(off, haptics.Strength, 1e-6f, "A greyed-out slider takes no press.");

            yield return Press(onToggle);
            yield return Release();
            Assert.IsTrue(haptics.HapticsEnabled);
            yield return Press(OnSlider(strength, 0.9f));
            yield return Release();
            Assert.AreEqual(0.9f, haptics.Strength, 0.051f);
        }
    }
}
