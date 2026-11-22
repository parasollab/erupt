using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Erupt.Ui
{
    /// <summary>
    /// Constructs world-space uGUI hierarchies in code.
    /// </summary>
    /// <remarks>
    /// Everything is world-space uGUI with TextMeshPro: screen-space canvases do not
    /// render in a headset, and UI Toolkit is not used.
    ///
    /// Text keeps TextMeshPro's default SDF material; nothing here assigns one.
    /// </remarks>
    public static class UiBuilder
    {
        public const float PixelsPerUnit = 1000f;

        /// <summary>
        /// Raised for every world canvas built here, so a platform backend can add the
        /// raycaster its input needs (XRI's tracked-device raycaster on OpenXR) without
        /// this assembly knowing about it.
        /// </summary>
        public static event System.Action<Canvas> CanvasCreated;

        public static Canvas CreateWorldCanvas(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = PixelsPerUnit / 10f;

            // A GraphicRaycaster is enough for mouse and for tests. XR ray interaction
            // additionally needs a tracked-device raycaster, which lives in the OpenXR
            // backend so this assembly stays platform-agnostic.
            go.AddComponent<GraphicRaycaster>();

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.localScale = Vector3.one / PixelsPerUnit;

            // A thin collider on the UI layer, so the interaction router's physics ray
            // sees the panel and consumes the press instead of selecting (or clearing)
            // whatever the world has behind it. The router rejects UI-layer hits by design.
            var collider = go.AddComponent<BoxCollider>();
            collider.size = new Vector3(size.x, size.y, 1f);
            collider.isTrigger = true;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) go.layer = uiLayer;

            CanvasCreated?.Invoke(canvas);
            return canvas;
        }

        /// <summary>
        /// Give a canvas made by <see cref="CreateWorldCanvas"/> another size (canvas units),
        /// and its press-consuming collider with it, whatever the canvas's pivot.
        /// </summary>
        public static void ResizeWorldCanvas(Canvas canvas, Vector2 size)
        {
            if (canvas == null) return;
            var rect = canvas.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            if (!canvas.TryGetComponent(out BoxCollider collider)) return;
            collider.size = new Vector3(size.x, size.y, 1f);
            // The rect's centre in its own space: zero for a centred pivot.
            collider.center = new Vector3((0.5f - rect.pivot.x) * size.x, (0.5f - rect.pivot.y) * size.y, 0f);
        }

        /// <summary>
        /// A world-space canvas that only shows something: no raycaster and no collider, so
        /// rays pass through it to whatever is behind, and <see cref="CanvasCreated"/> is not
        /// raised for it. For readouts such as the refusal toast.
        /// </summary>
        public static Canvas CreateWorldReadout(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = PixelsPerUnit / 10f;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.localScale = Vector3.one / PixelsPerUnit;
            return canvas;
        }

        public static RectTransform CreatePanel(string name, Transform parent, Color background)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = background;

            return go.GetComponent<RectTransform>();
        }

        public static HorizontalLayoutGroup CreateRow(string name, Transform parent, float spacing = 8f)
        {
            var rect = CreatePanel(name, parent, Color.clear);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            return layout;
        }

        public static VerticalLayoutGroup CreateColumn(string name, Transform parent, float spacing = 6f)
        {
            var rect = CreatePanel(name, parent, Color.clear);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperCenter;
            return layout;
        }

        public static TextMeshProUGUI CreateLabel(string name, Transform parent, string text, float fontSize = 28f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;

            return label;
        }

        /// <summary>The plate of a tier control: buttons, toggles, sliders.</summary>
        public static readonly Color ControlColor = new(0.16f, 0.17f, 0.20f, 0.94f);

        // A slider: the groove its handle runs in, the part of it up to the handle, the handle.
        private static readonly Color SliderGrooveColor = new(0.07f, 0.08f, 0.10f, 1f);
        private static readonly Color SliderFillColor = new(0.36f, 0.48f, 0.66f, 1f);
        private static readonly Color SliderHandleColor = new(0.92f, 0.94f, 0.97f, 1f);
        private const float SliderHandleWidth = 18f;

        public static Button CreateButton(string name, Transform parent, string text,
                                          Vector2 size, System.Action onClick = null)
        {
            var rect = CreatePanel(name, parent, ControlColor);
            rect.sizeDelta = size;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();

            var label = CreateLabel("Label", rect, text);
            Stretch(label.rectTransform);

            if (onClick != null) button.onClick.AddListener(() => onClick());
            return button;
        }

        /// <summary>
        /// A button that cycles through fixed choices, rendered as "label: value". One
        /// interactive element instead of a dropdown, which world-space uGUI lacks and
        /// which would cost two (open + pick).
        /// </summary>
        public static Button CreateCycle(string name, Transform parent, string label, string[] choices, int initial,
                                         Vector2 size, System.Action<int> onChanged)
        {
            int index = choices.Length == 0 ? -1 : Mathf.Clamp(initial, 0, choices.Length - 1);
            Button button = null;
            button = CreateButton(name, parent, Render(), size, () =>
            {
                if (choices.Length == 0) return;
                index = (index + 1) % choices.Length;
                SetButtonText(button, Render());
                onChanged?.Invoke(index);
            });
            return button;

            string Render() => index < 0 ? $"{label}: —" : $"{label}: {choices[index]}";
        }

        /// <summary>A two-state button, rendered as "label: on/off".</summary>
        public static Button CreateToggle(string name, Transform parent, string label, bool initial,
                                          Vector2 size, System.Action<bool> onChanged)
        {
            bool on = initial;
            Button button = null;
            button = CreateButton(name, parent, Render(), size, () =>
            {
                on = !on;
                SetButtonText(button, Render());
                onChanged?.Invoke(on);
            });
            return button;

            string Render() => $"{label}: {(on ? "on" : "off")}";
        }

        /// <summary>
        /// A slider for a value in a range, on one plate like a button: "label: value" above
        /// the groove the handle runs in. One interactive element (one <see cref="Selectable"/>).
        /// It is a uGUI <see cref="Slider"/>, so whatever presses and drags uGUI works it: the
        /// XR ray with the trigger held, a poke, the desktop mouse. A press anywhere on the
        /// plate puts the handle under the pointer; a drag moves it.
        /// </summary>
        /// <param name="format">How the value reads next to the label; two decimals when null.</param>
        /// <param name="step">The value moves in steps of this size from <paramref name="min"/>; zero leaves it continuous.</param>
        /// <param name="onChanged">Called with the new value each time it changes (never for the initial one).</param>
        public static Slider CreateSlider(string name, Transform parent, string label, float min, float max, float initial,
                                          Vector2 size, System.Action<float> onChanged,
                                          System.Func<float, string> format = null, float step = 0f)
        {
            var rect = CreatePanel(name, parent, ControlColor);
            rect.sizeDelta = size;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;

            // The upper part reads "label: value"; the lower part is the groove.
            const float split = 0.44f;
            var text = CreateLabel("Label", rect, "", 24f);
            text.raycastTarget = false;
            text.rectTransform.anchorMin = new Vector2(0f, split);
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;

            var groove = CreatePanel("Groove", rect, SliderGrooveColor);
            groove.anchorMin = Vector2.zero;
            groove.anchorMax = new Vector2(1f, split);
            groove.offsetMin = new Vector2(16f, 8f);
            groove.offsetMax = new Vector2(-16f, -2f);

            var fillArea = CreateArea("Fill Area", groove);
            var fill = CreatePanel("Fill", fillArea, SliderFillColor);
            fill.sizeDelta = Vector2.zero;

            // Inset by half a handle, so the handle stays inside the groove at both ends.
            var handleArea = CreateArea("Handle Slide Area", groove);
            handleArea.offsetMin = new Vector2(SliderHandleWidth * 0.5f, 0f);
            handleArea.offsetMax = new Vector2(-SliderHandleWidth * 0.5f, 0f);
            var handle = CreatePanel("Handle", handleArea, SliderHandleColor);
            handle.sizeDelta = new Vector2(SliderHandleWidth, 6f);

            var slider = rect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            // No keyboard or stick navigation: the desktop's arrow keys turn the view, they do not move a slider.
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.minValue = Mathf.Min(min, max);
            slider.maxValue = Mathf.Max(min, max);

            float Snap(float v)
            {
                if (step > 0f) v = slider.minValue + Mathf.Round((v - slider.minValue) / step) * step;
                return Mathf.Clamp(v, slider.minValue, slider.maxValue);
            }
            string Render(float v) => $"{label}: {(format != null ? format(v) : v.ToString("0.00"))}";

            float reported = Snap(initial);
            slider.SetValueWithoutNotify(reported);
            text.text = Render(reported);
            slider.onValueChanged.AddListener(raw =>
            {
                float value = Snap(raw);
                if (!Mathf.Approximately(value, raw)) slider.SetValueWithoutNotify(value);
                text.text = Render(value);
                if (Mathf.Approximately(value, reported)) return;
                reported = value;
                onChanged?.Invoke(value);
            });
            return slider;
        }

        private static RectTransform CreateArea(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            Stretch(rect);
            return rect;
        }

        /// <summary>Grey out a slider without hiding it; its value stays readable.</summary>
        public static void SetInteractable(Slider slider, bool interactable)
        {
            slider.interactable = interactable;
            var label = slider.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.color = interactable ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            if (slider.fillRect != null && slider.fillRect.TryGetComponent(out Image fill))
                fill.color = interactable ? SliderFillColor : new Color(SliderFillColor.r, SliderFillColor.g, SliderFillColor.b, 0.35f);
        }

        /// <summary>Interactive elements under a root: what Part 8's "~7 visible" counts.</summary>
        public static int CountInteractive(Transform root) =>
            root == null ? 0 : root.GetComponentsInChildren<UnityEngine.UI.Selectable>(false).Length;

        public static void SetButtonText(Button button, string text)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        /// <summary>Grey out a control without hiding it — disabled verbs stay visible.</summary>
        public static void SetInteractable(Button button, bool interactable)
        {
            button.interactable = interactable;

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
                label.color = interactable ? Color.white : new Color(1f, 1f, 1f, 0.35f);
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
