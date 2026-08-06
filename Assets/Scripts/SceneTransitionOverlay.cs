using System.Collections.Generic;
using UnityEngine;

public class SceneTransitionOverlay : MonoBehaviour
{
    private const int OverlayLayer = 31;
    private const float TextDistance = 1.1f;
    private const int CompositorTextureSize = 1024;
    private const string CompositorLoadingText = "Loading...";
    private static readonly Color LoadingGrey = new Color(0.45f, 0.45f, 0.45f, 1f);
    private static SceneTransitionOverlay s_Instance;

    private readonly Dictionary<Camera, CameraState> _cameraStates = new Dictionary<Camera, CameraState>();
    private TextMesh _loadingText;
    private Camera _mainCamera;
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

        _loadingText = textObject.AddComponent<TextMesh>();
        _loadingText.text = "Loading...";
        _loadingText.anchor = TextAnchor.MiddleCenter;
        _loadingText.alignment = TextAlignment.Center;
        _loadingText.color = Color.white;
        // Render at a higher font resolution while reducing its physical size. This keeps the
        // fallback text smooth and comfortably inside the headset's central field of view.
        _loadingText.fontSize = 128;
        _loadingText.characterSize = 0.0035f;

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

        ApplyCameraOverrides();
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
            camera.backgroundColor = LoadingGrey;
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
        Color32 color = LoadingGrey;
        Color32[] pixels = new Color32[CompositorTextureSize * CompositorTextureSize];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = color;
        }

        DrawCompositorText(pixels, CompositorTextureSize, CompositorTextureSize);
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

    private static void DrawCompositorText(Color32[] pixels, int width, int height)
    {
        const float glyphHeight = 36f;
        const float glyphSpacing = 5f;
        const float strokeRadius = 1.75f;

        float textWidth = glyphSpacing * (CompositorLoadingText.Length - 1);
        for (int i = 0; i < CompositorLoadingText.Length; i++)
        {
            textWidth += GetGlyphWidth(CompositorLoadingText[i]) * glyphHeight;
        }

        float characterX = (width - textWidth) * 0.5f;
        float startY = (height - glyphHeight) * 0.5f;
        Color32 textColor = new Color32(255, 255, 255, 255);

        for (int characterIndex = 0; characterIndex < CompositorLoadingText.Length; characterIndex++)
        {
            char character = CompositorLoadingText[characterIndex];
            float glyphWidth = GetGlyphWidth(character) * glyphHeight;
            DrawGlyph(
                pixels,
                width,
                height,
                character,
                new Rect(characterX, startY, glyphWidth, glyphHeight),
                strokeRadius,
                textColor);
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
        Color32 color)
    {
        Vector2 Point(float x, float y)
        {
            return new Vector2(bounds.x + x * bounds.width, bounds.y + y * bounds.height);
        }

        switch (character)
        {
            case 'L':
                DrawStroke(pixels, width, height, Point(0.18f, 0.9f), Point(0.18f, 0.14f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.18f, 0.14f), Point(0.88f, 0.14f), strokeRadius, color);
                break;
            case 'o':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.5f, 0.43f), new Vector2(0.38f, 0.28f), strokeRadius, color);
                break;
            case 'a':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.45f, 0.43f), new Vector2(0.34f, 0.28f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.79f, 0.15f), Point(0.79f, 0.71f), strokeRadius, color);
                break;
            case 'd':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.43f, 0.43f), new Vector2(0.34f, 0.28f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.77f, 0.15f), Point(0.77f, 0.91f), strokeRadius, color);
                break;
            case 'i':
                DrawStroke(pixels, width, height, Point(0.5f, 0.15f), Point(0.5f, 0.68f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.5f, 0.87f), Point(0.5f, 0.87f), strokeRadius * 1.15f, color);
                break;
            case 'n':
                DrawStroke(pixels, width, height, Point(0.17f, 0.15f), Point(0.17f, 0.7f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.17f, 0.6f), Point(0.4f, 0.71f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.4f, 0.71f), Point(0.72f, 0.63f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.72f, 0.63f), Point(0.78f, 0.15f), strokeRadius, color);
                break;
            case 'g':
                DrawEllipse(pixels, width, height, bounds, new Vector2(0.43f, 0.5f), new Vector2(0.34f, 0.25f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.77f, 0.72f), Point(0.77f, 0.14f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.77f, 0.14f), Point(0.57f, 0.06f), strokeRadius, color);
                DrawStroke(pixels, width, height, Point(0.57f, 0.06f), Point(0.28f, 0.1f), strokeRadius, color);
                break;
            case '.':
                DrawStroke(pixels, width, height, Point(0.5f, 0.14f), Point(0.5f, 0.14f), strokeRadius * 1.15f, color);
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
        Color32 color)
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
            DrawStroke(pixels, width, height, previous, current, strokeRadius, color);
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
        Color32 color)
    {
        const float edgeSoftness = 0.8f;
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.x, end.x) - radius - edgeSoftness));
        int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(start.x, end.x) + radius + edgeSoftness));
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.y, end.y) - radius - edgeSoftness));
        int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(start.y, end.y) + radius + edgeSoftness));

        Vector2 segment = end - start;
        float segmentLengthSquared = segment.sqrMagnitude;
        Color32 background = LoadingGrey;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 pixelCenter = new Vector2(x + 0.5f, y + 0.5f);
                float alongSegment = segmentLengthSquared > Mathf.Epsilon
                    ? Mathf.Clamp01(Vector2.Dot(pixelCenter - start, segment) / segmentLengthSquared)
                    : 0f;
                float distance = Vector2.Distance(pixelCenter, start + segment * alongSegment);
                float coverage = Mathf.Clamp01((radius + edgeSoftness - distance) / edgeSoftness);
                if (coverage <= 0f)
                {
                    continue;
                }

                int pixelIndex = y * width + x;
                Color32 existing = pixels[pixelIndex];
                pixels[pixelIndex] = new Color32(
                    (byte)Mathf.Max(existing.r, Mathf.RoundToInt(Mathf.Lerp(background.r, color.r, coverage))),
                    (byte)Mathf.Max(existing.g, Mathf.RoundToInt(Mathf.Lerp(background.g, color.g, coverage))),
                    (byte)Mathf.Max(existing.b, Mathf.RoundToInt(Mathf.Lerp(background.b, color.b, coverage))),
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
