using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Erupt.Ui
{
    /// <summary>
    /// Constructs world-space uGUI hierarchies in code.
    /// </summary>
    /// <remarks>
    /// World-space uGUI with TextMeshPro is the only UI path that survives the PolySpatial
    /// port — PolySpatial's supported-component table covers Canvas Renderer and
    /// TextMesh Pro but not UI Toolkit, and screen-space canvases do not work at all.
    /// See refactor/backlog.md B18.
    ///
    /// TextMeshPro is restricted to SDF with no custom shaders there, so nothing here
    /// assigns a text material.
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

        public static Button CreateButton(string name, Transform parent, string text,
                                          Vector2 size, System.Action onClick = null)
        {
            var rect = CreatePanel(name, parent, new Color(0.16f, 0.17f, 0.20f, 0.94f));
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
