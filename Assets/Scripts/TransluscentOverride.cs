using System.Linq;
using System.Collections.Generic;
using UnityEngine;

public class TranslucentOverride : MonoBehaviour
{
    private const string BuildSafeMaterialResource = "GhostOverlay";

    [Header("Overlay Color/Alpha")]
    public Color overlayColor = new Color(0f, 0.6f, 1f, 0.35f); // solid color with translucency

    [Header("Provide your own material OR leave empty to auto-create")]
    public Material overlayMaterial;

    // Cache of original materials for each renderer
    private readonly Dictionary<Renderer, Material[]> _originals = new Dictionary<Renderer, Material[]>();
    private bool _isApplied;
    private Material _runtimeMat;

    public void SetTranslucent(bool on)
    {
        if (on == _isApplied) return;
        if (on) Apply();
        else Restore();
    }

    private void Apply()
    {
        var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers.Length == 0) return;

        // Create a runtime instance of the overlay material if needed.
        // If user didn’t assign one, try to choose a suitable shader and build it.
        if (_runtimeMat == null)
        {
            if (overlayMaterial != null)
            {
                _runtimeMat = Instantiate(overlayMaterial);
            }
            else
            {
                // A serialized material in Resources forces Unity's Android build pipeline to
                // retain both the shader and its transparent variant. Shader.Find works in the
                // Editor but can return a stripped shader/variant in a Quest player build.
                Material buildSafeTemplate = Resources.Load<Material>(BuildSafeMaterialResource);
                if (buildSafeTemplate != null)
                {
                    _runtimeMat = Instantiate(buildSafeTemplate);
                }
                else
                {
                    Shader shader =
                        Shader.Find("Universal Render Pipeline/Lit") ??
                        Shader.Find("HDRP/Lit") ??
                        Shader.Find("Standard");
                    if (shader == null)
                    {
                        Debug.LogError("TranslucentOverride: no ghost overlay material or compatible shader is available.");
                        return;
                    }

                    _runtimeMat = new Material(shader);
                }
            }

            ConfigureAsTransparent(_runtimeMat, overlayColor);
        }

        _originals.Clear();

        foreach (var r in renderers)
        {
            // Cache originals
            var mats = r.sharedMaterials;
            _originals[r] = mats;

            // Prepare an array of the same overlay mat repeated for each submesh
            var newArray = Enumerable.Repeat(_runtimeMat, Mathf.Max(1, mats.Length)).ToArray();
            r.sharedMaterials = newArray;
        }

        _isApplied = true;
    }

    private void Restore()
    {
        if (!_isApplied) return;

        foreach (var kvp in _originals)
        {
            var r = kvp.Key;
            if (r) r.sharedMaterials = kvp.Value;
        }
        _originals.Clear();

        _isApplied = false;
    }

    private void OnDestroy()
    {
        // Safety: restore if still applied
        if (_isApplied) Restore();
        if (_runtimeMat) Destroy(_runtimeMat);
    }

    /// <summary>
    /// Sets up a material to be transparent and uniformly colored across pipelines.
    /// </summary>
    private static void ConfigureAsTransparent(Material mat, Color color)
    {
        string name = mat.shader ? mat.shader.name : "";
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 50;

        // Keep ghosts translucent but obvious in-headset. A 0.5 alpha can be too subtle on
        // Quest/URP depending on passthrough brightness, lighting, and transparent sorting.
        color.a = Mathf.Clamp(color.a, 0.65f, 0.85f);

        bool isUrp = name.Contains("Universal") && name.Contains("Render") && name.Contains("Pipeline");

        if (isUrp)
        {
            // URP Lit/Unlit
            mat.SetFloat("_Surface", 1f);                 // Transparent
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetFloat("_AlphaClip", 0f);               // NO cutout
            mat.DisableKeyword("_ALPHATEST_ON");

            // Ensure standard alpha blending (not premultiplied)
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);

            mat.SetOverrideTag("RenderType", "Transparent");
        }
        else if (name.Contains("HDRP"))
        {
            // HDRP Lit
            mat.SetFloat("_SurfaceType", 1f);             // Transparent
            if (mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", 0f); // 0 = Alpha
            if (mat.HasProperty("_AlphaCutoffEnable")) mat.SetFloat("_AlphaCutoffEnable", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_CullMode")) mat.SetInt("_CullMode", (int)UnityEngine.Rendering.CullMode.Off);
        }
        else
        {
            // Built-in Standard: use Fade mode for smooth translucency.
            mat.SetFloat("_Mode", 2f); // 2 = Fade
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        }

        ApplyOverlayColor(mat, color);
        mat.enableInstancing = true;
    }

    private static void ApplyOverlayColor(Material mat, Color color)
    {
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_BaseColorFactor")) mat.SetColor("_BaseColorFactor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_EmissionColor"))
        {
            Color emission = new Color(color.r, color.g, color.b, 1f) * 1.5f;
            mat.SetColor("_EmissionColor", emission);
            mat.EnableKeyword("_EMISSION");
        }
    }


    public void Start()
    {
        SetTranslucent(true);
    }
}
