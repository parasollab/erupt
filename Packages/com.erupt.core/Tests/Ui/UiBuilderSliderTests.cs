using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>
    /// <see cref="UiBuilder.CreateSlider"/>: one interactive element that shows its value,
    /// reports changes (in steps when asked), and greys out like the rest of the tier UI.
    /// Dragging it with a pointer is proved with the simulated mouse in the desktop tests.
    /// </summary>
    public class UiBuilderSliderTests
    {
        private Canvas canvas;
        private readonly List<float> reported = new();

        [SetUp]
        public void SetUp()
        {
            canvas = UiBuilder.CreateWorldCanvas("panel", null, new Vector2(600f, 300f));
            reported.Clear();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(canvas.gameObject);

        private Slider Make(float min, float max, float initial, float step = 0f, System.Func<float, string> format = null) =>
            UiBuilder.CreateSlider("Strength", canvas.transform, "Strength", min, max, initial, new Vector2(300f, 76f), reported.Add, format, step);

        private static string TextOf(Slider slider) => slider.GetComponentInChildren<TextMeshProUGUI>().text;

        [Test]
        public void ASlider_IsOneInteractiveElement()
        {
            UiBuilder.CreateButton("Button", canvas.transform, "Button", new Vector2(300f, 56f));
            Assert.AreEqual(1, UiBuilder.CountInteractive(canvas.transform));

            Slider slider = Make(0f, 1f, 0.5f);

            Assert.AreEqual(2, UiBuilder.CountInteractive(canvas.transform), "The slider counts once against the tier budget, whatever it is built of.");
            Assert.AreEqual(1, slider.GetComponentsInChildren<Selectable>(true).Length);
            Assert.AreEqual(Navigation.Mode.None, slider.navigation.mode, "The arrow keys turn the desktop view; they do not move a slider.");
        }

        [Test]
        public void ItShowsItsValue_AndStartsWithoutReporting()
        {
            Slider slider = Make(0f, 1f, 0.7f, format: v => $"{Mathf.RoundToInt(v * 100f)}%");

            Assert.AreEqual(0.7f, slider.value, 1e-5f);
            Assert.AreEqual("Strength: 70%", TextOf(slider));
            Assert.IsEmpty(reported, "The initial value is not a change.");

            slider.value = 0.25f;
            Assert.AreEqual("Strength: 25%", TextOf(slider));
            CollectionAssert.AreEqual(new[] { 0.25f }, reported);
        }

        [Test]
        public void WithoutAFormat_TheValueReadsWithTwoDecimals()
        {
            Slider slider = Make(0f, 2f, 1.5f);
            Assert.AreEqual("Strength: 1.50", TextOf(slider));
        }

        [Test]
        public void AStep_SnapsTheValue_AndReportsEachStepOnce()
        {
            Slider slider = Make(0.05f, 0.5f, 0.3f, step: 0.01f, format: v => $"{Mathf.RoundToInt(v * 100f)} cm");
            Assert.AreEqual("Strength: 30 cm", TextOf(slider));

            slider.value = 0.2149f;
            Assert.AreEqual(0.21f, slider.value, 1e-5f, "The handle goes to the step.");
            Assert.AreEqual("Strength: 21 cm", TextOf(slider));

            // A drag passes many raw values inside one step: one report.
            slider.value = 0.2121f;
            slider.value = 0.2089f;
            Assert.AreEqual(1, reported.Count);
            Assert.AreEqual(0.21f, reported[0], 1e-5f);

            slider.value = 10f;
            Assert.AreEqual(0.5f, slider.value, 1e-5f, "Never past the range.");
            Assert.AreEqual(0.5f, reported[^1], 1e-5f);
        }

        [Test]
        public void TheInitialValue_IsKeptInsideTheRange()
        {
            Slider slider = Make(0.05f, 0.5f, 3f);
            Assert.AreEqual(0.5f, slider.value, 1e-5f);
            Assert.AreEqual(0.05f, slider.minValue, 1e-6f);
            Assert.AreEqual(0.5f, slider.maxValue, 1e-6f);
        }

        [Test]
        public void TheHandleAndTheFill_FollowTheValue()
        {
            Slider slider = Make(0f, 1f, 0f);
            Canvas.ForceUpdateCanvases();
            float left = slider.handleRect.position.x;
            Assert.AreEqual(0f, slider.fillRect.anchorMax.x, 1e-5f);

            slider.value = 1f;
            Canvas.ForceUpdateCanvases();
            Assert.Greater(slider.handleRect.position.x, left, "The handle moved to the right.");
            Assert.AreEqual(1f, slider.fillRect.anchorMax.x, 1e-5f, "The groove is filled up to the handle.");

            // The handle stays inside the plate at both ends.
            var plate = new Vector3[4];
            ((RectTransform)slider.transform).GetWorldCorners(plate);
            var handle = new Vector3[4];
            slider.handleRect.GetWorldCorners(handle);
            Assert.LessOrEqual(handle[2].x, plate[2].x + 1e-5f);
        }

        [Test]
        public void ADisabledSlider_IsGreyedOut_AndKeepsItsValue()
        {
            Slider slider = Make(0f, 1f, 0.4f, format: v => $"{Mathf.RoundToInt(v * 100f)}%");
            UiBuilder.SetInteractable(slider, false);

            Assert.IsFalse(slider.interactable);
            Assert.Less(slider.GetComponentInChildren<TextMeshProUGUI>().color.a, 0.5f);
            Assert.AreEqual("Strength: 40%", TextOf(slider), "Its value stays readable.");

            UiBuilder.SetInteractable(slider, true);
            Assert.IsTrue(slider.interactable);
            Assert.AreEqual(1f, slider.GetComponentInChildren<TextMeshProUGUI>().color.a, 1e-5f);
        }

        [Test]
        public void ItSitsOnThePlateOfTheOtherControls()
        {
            Slider slider = Make(0f, 1f, 0.4f);
            Button button = UiBuilder.CreateButton("Button", canvas.transform, "Button", new Vector2(300f, 56f));
            Assert.AreEqual(button.GetComponent<Image>().color, slider.GetComponent<Image>().color);
            Assert.AreEqual(UiBuilder.ControlColor, slider.GetComponent<Image>().color);
        }
    }
}
