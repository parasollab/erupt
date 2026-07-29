using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

public static class VisionOSSampleControlsUI
{
    public static readonly Color PanelColor = Color.white;
    public static readonly Color TextColor = new Color(0.19607843f, 0.19607843f, 0.19607843f, 1f);
    public static readonly Color AccentTextColor = new Color(0.12f, 0.36f, 0.5f, 1f);
    public static readonly Color DisabledTextColor = new Color(0.45f, 0.45f, 0.45f, 1f);

    public static bool ShouldUseRuntimeUGUI()
    {
#if UNITY_EDITOR
        return EditorUserBuildSettings.activeBuildTarget.ToString() == "VisionOS";
#elif UNITY_VISIONOS
        return true;
#else
        return false;
#endif
    }

    public static Canvas EnsureCanvas(
        Transform parent,
        string canvasName,
        Vector2 pixelSize,
        Vector2 sizeMeters,
        Vector3 offsetMeters,
        int sortingOrder)
    {
        Transform existing = parent.Find(canvasName);
        GameObject canvasObject = existing != null ? existing.gameObject : new GameObject(canvasName);
        canvasObject.layer = parent.gameObject.layer;
        canvasObject.transform.SetParent(parent, false);
        canvasObject.transform.localRotation = Quaternion.identity;

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        if (canvas == null)
            canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        if (scaler == null)
            scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;
        scaler.referencePixelsPerUnit = 100f;
        scaler.dynamicPixelsPerUnit = 1f;

        GraphicRaycaster raycaster = canvasObject.GetComponent<GraphicRaycaster>();
        if (raycaster == null)
            raycaster = canvasObject.AddComponent<GraphicRaycaster>();
        raycaster.enabled = true;

        var rect = canvasObject.GetComponent<RectTransform>();
        rect.sizeDelta = pixelSize;

        ApplyWorldSize(parent, canvasObject.transform, pixelSize, sizeMeters, offsetMeters);
        EnsureBoxCollider(canvasObject, rect.sizeDelta, depth: 10f);

        if (canvas.worldCamera == null && Camera.main != null)
            canvas.worldCamera = Camera.main;

        return canvas;
    }

    public static void ApplyWorldSize(
        Transform parent,
        Transform target,
        Vector2 pixelSize,
        Vector2 sizeMeters,
        Vector3 offsetMeters)
    {
        float metersPerPixel = Mathf.Max(
            sizeMeters.x / Mathf.Max(1f, pixelSize.x),
            sizeMeters.y / Mathf.Max(1f, pixelSize.y));

        float parentScale = GetUniformScale(parent.lossyScale);
        target.localPosition = offsetMeters / parentScale;
        target.localScale = Vector3.one * (metersPerPixel / parentScale);
    }

    public static GameObject CreateVerticalPanel(
        Transform parent,
        string name,
        Vector2 pixelSize,
        bool addBackground = true)
    {
        var panel = new GameObject(name);
        panel.layer = parent.gameObject.layer;
        panel.transform.SetParent(parent, false);

        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = pixelSize;
        rect.anchoredPosition = new Vector2(0f, pixelSize.y * 0.5f);

        if (addBackground)
            AddImage(panel, PanelColor, raycastTarget: true);

        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(50, 50, 25, 35);
        layout.spacing = 25f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return panel;
    }

    public static GameObject CreateGridPanel(
        Transform parent,
        string name,
        Vector2 pixelSize,
        int columns = 2,
        bool addBackground = true)
    {
        var panel = new GameObject(name);
        panel.layer = parent.gameObject.layer;
        panel.transform.SetParent(parent, false);

        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = pixelSize;
        rect.anchoredPosition = new Vector2(0f, pixelSize.y * 0.5f);

        if (addBackground)
            AddImage(panel, PanelColor, raycastTarget: true);

        var grid = panel.AddComponent<GridLayoutGroup>();
        grid.padding = new RectOffset(25, 25, 25, 35);
        grid.cellSize = new Vector2((pixelSize.x - 50f - 25f * (columns - 1)) / columns, 100f);
        grid.spacing = new Vector2(25f, 25f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;

        var fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return panel;
    }

    public static Button CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, float width = 1000f, float height = 100f, int fontSize = 35)
    {
        var buttonObject = new GameObject(label + " Button");
        buttonObject.layer = parent.gameObject.layer;
        buttonObject.transform.SetParent(parent, false);

        var rect = buttonObject.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(width, height);

        var image = AddImage(buttonObject, Color.white, raycastTarget: true);
        image.type = Image.Type.Sliced;

        var button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        if (onClick != null)
            button.onClick.AddListener(onClick);

        var text = CreateText(buttonObject.transform, label, fontSize, TextAnchor.MiddleCenter, TextColor);
        text.raycastTarget = false;

        var layout = buttonObject.AddComponent<LayoutElement>();
        layout.preferredWidth = width;
        layout.preferredHeight = height;
        layout.minHeight = height;

        return button;
    }

    public static Text CreateText(Transform parent, string text, int fontSize, TextAnchor alignment, Color? color = null, float height = 80f)
    {
        var textObject = new GameObject("Text");
        textObject.layer = parent.gameObject.layer;
        textObject.transform.SetParent(parent, false);

        var rect = textObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.sizeDelta = new Vector2(0f, height);

        var label = textObject.AddComponent<Text>();
        label.text = text;
        label.font = GetBuiltinFont();
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = color ?? TextColor;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        var layout = textObject.AddComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;

        return label;
    }

    public static Toggle CreateToggle(Transform parent, string label, bool value, UnityEngine.Events.UnityAction<bool> onChanged, float height = 100f)
    {
        var toggleObject = new GameObject(label + " Toggle");
        toggleObject.layer = parent.gameObject.layer;
        toggleObject.transform.SetParent(parent, false);

        var rect = toggleObject.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(1000f, height);

        var image = AddImage(toggleObject, Color.white, raycastTarget: true);
        var toggle = toggleObject.AddComponent<Toggle>();
        toggle.targetGraphic = image;
        toggle.isOn = value;
        if (onChanged != null)
            toggle.onValueChanged.AddListener(onChanged);

        var text = CreateText(toggleObject.transform, BuildToggleLabel(label, value), 35, TextAnchor.MiddleCenter, TextColor, height);
        text.raycastTarget = false;
        toggle.onValueChanged.AddListener(isOn => text.text = BuildToggleLabel(label, isOn));

        var layout = toggleObject.AddComponent<LayoutElement>();
        layout.preferredHeight = height;
        layout.minHeight = height;

        return toggle;
    }

    public static Slider CreateSlider(Transform parent, float minValue, float maxValue, float value, UnityEngine.Events.UnityAction<float> onChanged)
    {
        var row = CreateHorizontalRow(parent, "Slider Row", 1000f, 100f);
        CreateText(row.transform, "Speed", 35, TextAnchor.MiddleLeft, TextColor, 100f).GetComponent<LayoutElement>().preferredWidth = 220f;

        var sliderObject = new GameObject("Slider");
        sliderObject.layer = parent.gameObject.layer;
        sliderObject.transform.SetParent(row.transform, false);
        var rect = sliderObject.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(730f, 80f);

        var layout = sliderObject.AddComponent<LayoutElement>();
        layout.preferredWidth = 730f;
        layout.preferredHeight = 80f;

        var slider = sliderObject.AddComponent<Slider>();
        slider.minValue = minValue;
        slider.maxValue = maxValue;
        slider.value = value;
        if (onChanged != null)
            slider.onValueChanged.AddListener(onChanged);

        var background = CreateStretchChild(sliderObject.transform, "Background");
        background.GetComponent<RectTransform>().anchorMin = new Vector2(0f, 0.4f);
        background.GetComponent<RectTransform>().anchorMax = new Vector2(1f, 0.6f);
        AddImage(background, new Color(0.78f, 0.78f, 0.78f, 1f), raycastTarget: false);

        var fill = CreateStretchChild(background.transform, "Fill");
        var fillImage = AddImage(fill, new Color(0.2f, 0.62f, 0.82f, 1f), raycastTarget: false);

        var handle = new GameObject("Handle");
        handle.layer = parent.gameObject.layer;
        handle.transform.SetParent(sliderObject.transform, false);
        var handleRect = handle.AddComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(40f, 70f);
        var handleImage = AddImage(handle, TextColor, raycastTarget: true);

        slider.fillRect = fill.GetComponent<RectTransform>();
        slider.handleRect = handleRect;
        slider.targetGraphic = handleImage;

        return slider;
    }

    public static GameObject CreateHorizontalRow(Transform parent, string name, float width = 1000f, float height = 100f)
    {
        var row = new GameObject(name);
        row.layer = parent.gameObject.layer;
        row.transform.SetParent(parent, false);

        var rect = row.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(width, height);

        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;
        layout.spacing = 25f;

        var layoutElement = row.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = height;
        layoutElement.minHeight = height;

        return row;
    }

    public static void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Object.Destroy(parent.GetChild(i).gameObject);
        }
    }

    public static Image AddImage(GameObject target, Color color, bool raycastTarget)
    {
        var image = target.GetComponent<Image>();
        if (image == null)
            image = target.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    public static void EnsureBoxCollider(GameObject target, Vector2 size, float depth)
    {
        var collider = target.GetComponent<BoxCollider>();
        if (collider == null)
            collider = target.AddComponent<BoxCollider>();
        collider.size = new Vector3(size.x, size.y, depth);
    }

    public static float GetUniformScale(Vector3 scale)
    {
        return Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
    }

    public static Font GetBuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    static GameObject CreateStretchChild(Transform parent, string name)
    {
        var child = new GameObject(name);
        child.layer = parent.gameObject.layer;
        child.transform.SetParent(parent, false);
        var rect = child.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return child;
    }

    static string BuildToggleLabel(string label, bool enabled)
    {
        return $"{(enabled ? "Disable" : "Enable")} {label}";
    }
}
