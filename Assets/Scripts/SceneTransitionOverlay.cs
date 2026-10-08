using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class SceneTransitionOverlay : MonoBehaviour
{
    private const int OverlayLayer = 31;
    private const float TextDistance = 1.1f;
    private const int CompositorTextureSize = 1024;
    private const string LoadingWord = "Loading";
    private const string CompositorFallbackText = "Loading...";
    private const float DotIntervalSeconds = 0.45f;
    private const float PulseSpeed = 2.4f;

    // Fraction of the compositor texture height the rendered label should occupy,
    // and the widest it may grow before being scaled down to fit.
    private const float CompositorLabelHeight = 0.055f;
    private const float CompositorLabelMaxWidth = 0.7f;

    // Quest-system-style palette: near-black neutral space with a soft glow behind
    // the label so the screen reads as a lit stage rather than a flat color.
    private static readonly Color BackgroundCenter = new Color(0.145f, 0.165f, 0.215f, 1f);
    private static readonly Color BackgroundEdge = new Color(0.04f, 0.045f, 0.06f, 1f);
    private static readonly Color TextColor = new Color(0.95f, 0.96f, 0.98f, 1f);
    private static readonly Color AccentColor = new Color(0.30f, 0.62f, 1f, 1f);
    private static SceneTransitionOverlay s_Instance;

    private readonly Dictionary<Camera, CameraState> _cameraStates = new Dictionary<Camera, CameraState>();
    private TMP_Text _loadingLabel;
    private TextMesh _fallbackText;
    private Camera _mainCamera;
    private float _dotTimer;
    private int _visibleDotCount = 3;
#if ERUPT_USE_META_XR
    private OVROverlay _compositorOverlay;
    private Texture2D _compositorTexture;
#endif

    private struct CameraState
    {
        public CameraClearFlags clearFlags;
        public Color backgroundColor;
        public int cullingMask;
    }

    public static void Show()
    {
        EnsureInstance().SetVisible(true);
    }

    public static void Hide()
    {
        if (s_Instance != null)
        {
            s_Instance.SetVisible(false);
        }
    }

    private static SceneTransitionOverlay EnsureInstance()
    {
        if (s_Instance != null)
        {
            return s_Instance;
        }

        GameObject overlayObject = new GameObject("Scene Transition Overlay");
        DontDestroyOnLoad(overlayObject);
        s_Instance = overlayObject.AddComponent<SceneTransitionOverlay>();
        s_Instance.BuildOverlay();
        s_Instance.SetVisible(false);
        return s_Instance;
    }

    private void BuildOverlay()
    {
        gameObject.layer = OverlayLayer;

        GameObject textObject = new GameObject("Loading Text");
        textObject.layer = OverlayLayer;
        textObject.transform.SetParent(transform, false);
        textObject.transform.localPosition = Vector3.forward * TextDistance;
        textObject.transform.localRotation = Quaternion.identity;

        if (TMP_Settings.defaultFontAsset != null)
        {
            TextMeshPro label = textObject.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.color = TextColor;
            label.fontSize = 45f;
            label.characterSpacing = 4f;
            label.rectTransform.sizeDelta = new Vector2(200f, 20f);
            textObject.transform.localScale = Vector3.one * 0.01f;
            _loadingLabel = label;
        }
        else
        {
            // TMP essentials missing: fall back to the legacy TextMesh at a high font
            // resolution and small physical size so it stays smooth and comfortably
            // inside the headset's central field of view.
            _fallbackText = textObject.AddComponent<TextMesh>();
            _fallbackText.anchor = TextAnchor.MiddleCenter;
            _fallbackText.alignment = TextAlignment.Center;
            _fallbackText.color = TextColor;
            _fallbackText.fontSize = 128;
            _fallbackText.characterSize = 0.0035f;

            Font legacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (legacyFont != null)
            {
                _fallbackText.font = legacyFont;
                MeshRenderer textRenderer = textObject.GetComponent<MeshRenderer>();
                if (textRenderer != null)
                {
                    textRenderer.sharedMaterial = legacyFont.material;
                }
            }
        }

        ApplyLabelText();

#if ERUPT_USE_META_XR
        BuildCompositorOverlay();
#endif
    }

    private void LateUpdate()
    {
        if (!gameObject.activeSelf)
        {
            return;
        }

        Camera camera = GetMainCamera();
        if (camera != null)
        {
            transform.position = camera.transform.position;
            transform.rotation = camera.transform.rotation;
#if ERUPT_USE_META_XR
            if (transform.parent != camera.transform)
            {
                transform.SetParent(camera.transform, true);
            }
#endif
        }

        AnimateLoadingLabel();
        ApplyCameraOverrides();
    }

    private void AnimateLoadingLabel()
    {
        _dotTimer += Time.unscaledDeltaTime;
        if (_dotTimer >= DotIntervalSeconds)
        {
            _dotTimer -= DotIntervalSeconds;
            _visibleDotCount = (_visibleDotCount % 3) + 1;
            ApplyLabelText();
        }

        float pulse = Mathf.Lerp(0.72f, 1f, 0.5f * (1f + Mathf.Sin(Time.unscaledTime * PulseSpeed)));
        Color color = TextColor;
        color.a = pulse;
        if (_loadingLabel != null)
        {
            _loadingLabel.color = color;
        }
        else if (_fallbackText != null)
        {
            _fallbackText.color = color;
        }
    }

    private void ApplyLabelText()
    {
        // Pad the hidden dots with a fully transparent color so the visible text keeps a
        // constant width and stays centered while the ellipsis animates.
        string visibleDots = new string('.', _visibleDotCount);
        string hiddenDots = new string('.', 3 - _visibleDotCount);
        string text = hiddenDots.Length > 0
            ? $"{LoadingWord}{visibleDots}<color=#00000000>{hiddenDots}</color>"
            : $"{LoadingWord}{visibleDots}";

        if (_loadingLabel != null)
        {
            _loadingLabel.text = text;
        }
        else if (_fallbackText != null)
        {
            _fallbackText.richText = true;
            _fallbackText.text = text;
        }
    }

    private Camera GetMainCamera()
    {
        if (_mainCamera != null && _mainCamera.isActiveAndEnabled)
        {
            return _mainCamera;
        }

        _mainCamera = Camera.main;
        if (_mainCamera == null)
        {
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i].isActiveAndEnabled)
                {
                    _mainCamera = cameras[i];
                    break;
                }
            }
        }

        return _mainCamera;
    }

    private void ApplyCameraOverrides()
    {
        Camera[] cameras = Camera.allCameras;
        int overlayMask = 1 << OverlayLayer;

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera camera = cameras[i];
            if (camera == null || !camera.isActiveAndEnabled)
            {
                continue;
            }

            if (!_cameraStates.ContainsKey(camera))
            {
                _cameraStates[camera] = new CameraState
                {
                    clearFlags = camera.clearFlags,
                    backgroundColor = camera.backgroundColor,
                    cullingMask = camera.cullingMask
                };
            }

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundEdge;
            camera.cullingMask = overlayMask;
        }
    }

    private void RestoreCameraOverrides()
    {
        foreach (KeyValuePair<Camera, CameraState> entry in _cameraStates)
        {
            Camera camera = entry.Key;
            if (camera == null)
            {
                continue;
            }

            CameraState state = entry.Value;
            camera.clearFlags = state.clearFlags;
            camera.backgroundColor = state.backgroundColor;
            camera.cullingMask = state.cullingMask;
        }

        _cameraStates.Clear();
    }

    private void SetVisible(bool visible)
    {
        if (visible)
        {
            _dotTimer = 0f;
            _visibleDotCount = 3;
            ApplyLabelText();
            gameObject.SetActive(true);
#if ERUPT_USE_META_XR
            if (_compositorOverlay != null)
            {
                _compositorOverlay.hidden = false;
            }
#endif
            LateUpdate();
            return;
        }

#if ERUPT_USE_META_XR
        if (_compositorOverlay != null)
        {
            _compositorOverlay.hidden = true;
        }
#endif
        RestoreCameraOverrides();
        gameObject.SetActive(false);
    }

#if ERUPT_USE_META_XR
    private void BuildCompositorOverlay()
    {
        GameObject compositorObject = new GameObject("Compositor Loading Quad");
        compositorObject.transform.SetParent(transform, false);
        compositorObject.transform.localPosition = Vector3.forward;
        compositorObject.transform.localRotation = Quaternion.identity;
        compositorObject.transform.localScale = new Vector3(4f, 4f, 1f);

        _compositorTexture = new Texture2D(
            CompositorTextureSize,
            CompositorTextureSize,
            TextureFormat.RGBA32,
            false);
        _compositorTexture.name = "Runtime Loading Overlay";
        Color32[] pixels = new Color32[CompositorTextureSize * CompositorTextureSize];
        FillCompositorBackground(pixels, CompositorTextureSize, CompositorTextureSize);

        if (!TryCompositeRenderedLabel(pixels, CompositorTextureSize, CompositorTextureSize))
        {
            // No TMP font available: fall back to the hand-drawn stroke glyphs.
            DrawCompositorText(pixels, CompositorTextureSize, CompositorTextureSize, 1.75f, TextColor, 1f);
        }

        DrawCompositorAccentDots(pixels, CompositorTextureSize, CompositorTextureSize);

        _compositorTexture.SetPixels32(pixels);
        _compositorTexture.Apply(false, true);

        _compositorOverlay = compositorObject.AddComponent<OVROverlay>();
        _compositorOverlay.currentOverlayType = OVROverlay.OverlayType.Overlay;
        _compositorOverlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
        _compositorOverlay.compositionDepth = -100;
        _compositorOverlay.noDepthBufferTesting = true;
        _compositorOverlay.isDynamic = false;
        _compositorOverlay.textures = new Texture[] { _compositorTexture, _compositorTexture };
        _compositorOverlay.hidden = true;
    }

    private static void FillCompositorBackground(Color32[] pixels, int width, int height)
    {
        // Soft radial glow slightly above center, falling off to near-black edges.
        Vector2 glowCenter = new Vector2(0.5f, 0.54f);

        for (int y = 0; y < height; y++)
        {
            float normalizedY = y / (float)(height - 1);
            for (int x = 0; x < width; x++)
            {
                float normalizedX = x / (float)(width - 1);
                float centerDistance = Vector2.Distance(new Vector2(normalizedX, normalizedY), glowCenter) / 0.62f;
                float falloff = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(centerDistance));
                Color pixel = Color.Lerp(BackgroundCenter, BackgroundEdge, falloff);
                pixel.a = 1f;
                pixels[y * width + x] = pixel;
            }
        }
    }

    /// <summary>
    /// Renders the real TMP label with a one-shot orthographic camera and composites it
    /// into the compositor texture, so the splash shows genuine font rendering instead of
    /// hand-drawn strokes. Returns false when no TMP font asset is available.
    /// </summary>
    private static bool TryCompositeRenderedLabel(Color32[] pixels, int width, int height)
    {
        if (TMP_Settings.defaultFontAsset == null)
        {
            return false;
        }

        // Build the capture rig far below the floor so no live camera can see it during
        // the frame it exists.
        GameObject rigRoot = new GameObject("Loading Label Capture");
        rigRoot.transform.position = new Vector3(0f, -9999f, 0f);

        RenderTexture captureTarget = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        Texture2D capture = null;

        try
        {
            GameObject cameraObject = new GameObject("Capture Camera");
            cameraObject.layer = OverlayLayer;
            cameraObject.transform.SetParent(rigRoot.transform, false);
            Camera captureCamera = cameraObject.AddComponent<Camera>();
            captureCamera.enabled = false;
            captureCamera.orthographic = true;
            captureCamera.orthographicSize = 0.5f;
            captureCamera.nearClipPlane = 0.01f;
            captureCamera.farClipPlane = 10f;
            captureCamera.clearFlags = CameraClearFlags.SolidColor;
            captureCamera.backgroundColor = Color.clear;
            captureCamera.cullingMask = 1 << OverlayLayer;
            captureCamera.targetTexture = captureTarget;

            GameObject labelObject = new GameObject("Capture Label");
            labelObject.layer = OverlayLayer;
            labelObject.transform.SetParent(rigRoot.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, 0f, 2f);

            TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
            label.text = LoadingWord;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.fontSize = 45f;
            label.characterSpacing = 4f;
            label.rectTransform.sizeDelta = new Vector2(200f, 20f);
            label.ForceMeshUpdate();

            // Scale the label so its rendered height lands on the design target regardless
            // of TMP's point-to-unit conversion, clamped so long strings still fit.
            Bounds textBounds = label.textBounds;
            if (textBounds.size.y > 1e-5f && textBounds.size.x > 1e-5f)
            {
                float scale = Mathf.Min(
                    CompositorLabelHeight / textBounds.size.y,
                    CompositorLabelMaxWidth / textBounds.size.x);
                labelObject.transform.localScale = Vector3.one * scale;

                // Keep the visual center of the glyphs on the view center even when the
                // bounds are offset from the transform origin (ascenders vs descenders).
                Vector3 boundsOffset = textBounds.center * scale;
                labelObject.transform.localPosition = new Vector3(-boundsOffset.x, -boundsOffset.y, 2f);
            }

            // Camera.Render() is unsupported under scriptable render pipelines (URP);
            // render requests are the supported one-shot capture path there.
            RenderPipeline.StandardRequest renderRequest = new RenderPipeline.StandardRequest();
            if (RenderPipeline.SupportsRenderRequest(captureCamera, renderRequest))
            {
                renderRequest.destination = captureTarget;
                RenderPipeline.SubmitRenderRequest(captureCamera, renderRequest);
            }
            else
            {
                captureCamera.Render();
            }

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = captureTarget;
            capture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            capture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            capture.Apply(false);
            RenderTexture.active = previousActive;

            captureCamera.targetTexture = null;

            // The label was rendered pure white over a transparent black clear, so any
            // channel value directly encodes glyph coverage for that pixel.
            Color32[] labelPixels = capture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 labelPixel = labelPixels[i];
                int coverageByte = Mathf.Max(labelPixel.r, Mathf.Max(labelPixel.g, labelPixel.b));
                if (coverageByte == 0)
                {
                    continue;
                }

                float coverage = coverageByte / 255f;
                Color32 existing = pixels[i];
                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Lerp(existing.r, TextColor.r * 255f, coverage)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(existing.g, TextColor.g * 255f, coverage)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(existing.b, TextColor.b * 255f, coverage)),
                    255);
            }

            return true;
        }
        finally
        {
            RenderTexture.ReleaseTemporary(captureTarget);
            if (capture != null)
            {
                Destroy(capture);
            }

            Destroy(rigRoot);
        }
    }

    private static void DrawCompositorAccentDots(Color32[] pixels, int width, int height)
    {
        const float dotSpacing = 30f;
        const float dotRadius = 4.5f;

        float dotY = height * 0.5f - 62f;
        float centerX = width * 0.5f;
        float[] intensities = { 0.35f, 1f, 0.35f };

        for (int i = 0; i < intensities.Length; i++)
        {
            Vector2 dotCenter = new Vector2(centerX + (i - 1) * dotSpacing, dotY);
            DrawStroke(pixels, width, height, dotCenter, dotCenter, dotRadius, AccentColor, intensities[i]);
        }
    }

    private static void DrawCompositorText(
        Color32[] pixels,
        int width,
        int height,
        float strokeRadius,
        Color32 color,
        float intensity)
    {
        const float glyphHeight = 36f;
        const float glyphSpacing = 5f;

        float textWidth = glyphSpacing * (CompositorFallbackText.Length - 1);
        for (int i = 0; i < CompositorFallbackText.Length; i++)
        {
            textWidth += GetGlyphWidth(CompositorFallbackText[i]) * glyphHeight;
        }

        float characterX = (width - textWidth) * 0.5f;
        float startY = (height - glyphHeight) * 0.5f;

        for (int characterIndex = 0; characterIndex < CompositorFallbackText.Length; characterIndex++)
        {
            char character = CompositorFallbackText[characterIndex];
            float glyphWidth = GetGlyphWidth(character) * glyphHeight;
            DrawGlyph(
                pixels,
                width,
                height,
                character,
                new Rect(characterX, startY, glyphWidth, glyphHeight),
                strokeRadius,
                color,
                intensity);
            characterX += glyphWidth + glyphSpacing;
        }
    }

    private static float GetGlyphWidth(char character)
    {
        switch (character)
        {
            case 'i': return 0.25f;
            case '.': return 0.18f;
            case 'L': return 0.62f;
            default: return 0.68f;
        }
    }

    private static void DrawGlyph(
        Color32[] pixels,
        int width,
        int height,
        char character,
        Rect bounds,
        float strokeRadius,
        Color32 color,
        float intensity)
    {
        Vector2 Point(float x, float y)
        {
            return new Vector2(bounds.x + x * bounds.width, bounds.y + y * bounds.height);
        }

        switch (character)
        {
            case 'L':
                DrawStroke(pixels, width, height, Point(0.18f, 0.9f), Point(0.18f, 0.14f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.18f, 0.14f), Point(0.88f, 0.14f), strokeRadius, color, intensity);
                break;
            case 'o':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.5f, 0.43f), new Vector2(0.38f, 0.28f), strokeRadius, color, intensity);
                break;
            case 'a':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.45f, 0.43f), new Vector2(0.34f, 0.28f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.79f, 0.15f), Point(0.79f, 0.71f), strokeRadius, color, intensity);
                break;
            case 'd':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.43f, 0.43f), new Vector2(0.34f, 0.28f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.77f, 0.15f), Point(0.77f, 0.91f), strokeRadius, color, intensity);
                break;
            case 'i':
                DrawStroke(pixels, width, height, Point(0.5f, 0.15f), Point(0.5f, 0.68f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.5f, 0.87f), Point(0.5f, 0.87f), strokeRadius * 1.15f, color, intensity);
                break;
            case 'n':
                DrawStroke(pixels, width, height, Point(0.17f, 0.15f), Point(0.17f, 0.7f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.17f, 0.6f), Point(0.4f, 0.71f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.4f, 0.71f), Point(0.72f, 0.63f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.72f, 0.63f), Point(0.78f, 0.15f), strokeRadius, color, intensity);
                break;
            case 'g':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.43f, 0.5f), new Vector2(0.34f, 0.25f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.77f, 0.72f), Point(0.77f, 0.14f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.77f, 0.14f), Point(0.57f, 0.06f), strokeRadius, color, intensity);
                DrawStroke(pixels, width, height, Point(0.57f, 0.06f), Point(0.28f, 0.1f), strokeRadius, color, intensity);
                break;
            case '.':
                DrawStroke(pixels, width, height, Point(0.5f, 0.14f), Point(0.5f, 0.14f), strokeRadius * 1.15f, color, intensity);
                break;
        }
    }

    private static void DrawEllipse(
        Color32[] pixels,
        int width,
        int height,
        Rect bounds,
        Vector2 normalizedCenter,
        Vector2 normalizedRadius,
        float strokeRadius,
        Color32 color,
        float intensity)
    {
        const int segmentCount = 24;
        Vector2 center = new Vector2(
            bounds.x + normalizedCenter.x * bounds.width,
            bounds.y + normalizedCenter.y * bounds.height);
        Vector2 radius = new Vector2(
            normalizedRadius.x * bounds.width,
            normalizedRadius.y * bounds.height);

        Vector2 previous = center + new Vector2(radius.x, 0f);
        for (int segment = 1; segment <= segmentCount; segment++)
        {
            float angle = segment * Mathf.PI * 2f / segmentCount;
            Vector2 current = center + new Vector2(Mathf.Cos(angle) * radius.x, Mathf.Sin(angle) * radius.y);
            DrawStroke(pixels, width, height, previous, current, strokeRadius, color, intensity);
            previous = current;
        }
    }

    private static void DrawStroke(
        Color32[] pixels,
        int width,
        int height,
        Vector2 start,
        Vector2 end,
        float radius,
        Color32 color,
        float intensity)
    {
        const float edgeSoftness = 0.8f;
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.x, end.x) - radius - edgeSoftness));
        int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(start.x, end.x) + radius + edgeSoftness));
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.y, end.y) - radius - edgeSoftness));
        int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(start.y, end.y) + radius + edgeSoftness));

        Vector2 segment = end - start;
        float segmentLengthSquared = segment.sqrMagnitude;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 pixelCenter = new Vector2(x + 0.5f, y + 0.5f);
                float alongSegment = segmentLengthSquared > Mathf.Epsilon
                    ? Mathf.Clamp01(Vector2.Dot(pixelCenter - start, segment) / segmentLengthSquared)
                    : 0f;
                float distance = Vector2.Distance(pixelCenter, start + segment * alongSegment);
                float coverage = Mathf.Clamp01((radius + edgeSoftness - distance) / edgeSoftness) * intensity;
                if (coverage <= 0f)
                {
                    continue;
                }

                // Blend against whatever is already in the texture so strokes composite
                // correctly over the gradient background.
                int pixelIndex = y * width + x;
                Color32 existing = pixels[pixelIndex];
                pixels[pixelIndex] = new Color32(
                    (byte)Mathf.Max(existing.r, Mathf.RoundToInt(Mathf.Lerp(existing.r, color.r, coverage))),
                    (byte)Mathf.Max(existing.g, Mathf.RoundToInt(Mathf.Lerp(existing.g, color.g, coverage))),
                    (byte)Mathf.Max(existing.b, Mathf.RoundToInt(Mathf.Lerp(existing.b, color.b, coverage))),
                    255);
            }
        }
    }
#endif

    private void OnDestroy()
    {
        RestoreCameraOverrides();
#if ERUPT_USE_META_XR
        if (_compositorTexture != null)
        {
            Destroy(_compositorTexture);
        }
#endif
    }
}
