using System.Collections.Generic;
using UnityEngine;

public class SceneTransitionOverlay : MonoBehaviour
{
    private const int OverlayLayer = 31;
    private const float TextDistance = 1.1f;
    private const int CompositorTextureSize = 1024;
    private const string CompositorLoadingText = "LOADING...";
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
        _loadingText.fontSize = 96;
        _loadingText.characterSize = 0.007f;

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
        const int glyphWidth = 5;
        const int glyphHeight = 7;
        const int glyphSpacing = 1;
        const int pixelScale = 7;

        int textWidth = ((glyphWidth + glyphSpacing) * CompositorLoadingText.Length - glyphSpacing) * pixelScale;
        int startX = (width - textWidth) / 2;
        int startY = (height - glyphHeight * pixelScale) / 2;
        Color32 textColor = new Color32(255, 255, 255, 255);

        for (int characterIndex = 0; characterIndex < CompositorLoadingText.Length; characterIndex++)
        {
            int[] rows = GetGlyphRows(CompositorLoadingText[characterIndex]);
            int characterX = startX + characterIndex * (glyphWidth + glyphSpacing) * pixelScale;

            for (int row = 0; row < glyphHeight; row++)
            {
                for (int column = 0; column < glyphWidth; column++)
                {
                    if ((rows[row] & (1 << (glyphWidth - 1 - column))) == 0)
                    {
                        continue;
                    }

                    FillPixelBlock(
                        pixels,
                        width,
                        height,
                        characterX + column * pixelScale,
                        startY + (glyphHeight - 1 - row) * pixelScale,
                        pixelScale,
                        textColor);
                }
            }
        }
    }

    private static void FillPixelBlock(
        Color32[] pixels,
        int width,
        int height,
        int startX,
        int startY,
        int size,
        Color32 color)
    {
        for (int y = startY; y < startY + size && y < height; y++)
        {
            for (int x = startX; x < startX + size && x < width; x++)
            {
                if (x >= 0 && y >= 0)
                {
                    pixels[y * width + x] = color;
                }
            }
        }
    }

    private static int[] GetGlyphRows(char character)
    {
        switch (character)
        {
            case 'A': return new[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 };
            case 'D': return new[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110 };
            case 'G': return new[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 };
            case 'I': return new[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11111 };
            case 'L': return new[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 };
            case 'N': return new[] { 0b10001, 0b11001, 0b11001, 0b10101, 0b10011, 0b10011, 0b10001 };
            case 'O': return new[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 };
            case '.': return new[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00110, 0b00110 };
            default: return new[] { 0, 0, 0, 0, 0, 0, 0 };
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
