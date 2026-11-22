using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace Erupt.Editor.Setup
{
    /// <summary>
    /// Gives the materials of Collada (<c>.dae</c>) models the lit shader of the render
    /// pipeline the project uses. Robot descriptions ship their visual meshes as Collada
    /// files. Unity's model importer makes their materials for the built-in pipeline, and
    /// the Universal Render Pipeline converts the materials of FBX and OBJ models at import
    /// but not those of Collada models: under it a robot made from such meshes is drawn in
    /// the magenta of a shader the pipeline cannot use. This does for <c>.dae</c> what the
    /// pipeline does for <c>.fbx</c>: colour, transparency, colour texture and gloss.
    /// </summary>
    /// <remarks>
    /// Nothing happens under the built-in pipeline, or under a pipeline whose default shader
    /// has no <c>_BaseColor</c>. The materials stay embedded in the model under the same
    /// names, so prefabs made from the model before keep their references. The assembly this
    /// lives in references no package, so the conversion is in place from the first import.
    /// A project that changes its render pipeline afterwards reimports its <c>.dae</c> models
    /// (right-click > Reimport) to get the other pipeline's materials.
    /// </remarks>
    public sealed class ColladaPipelineMaterials : AssetPostprocessor
    {
        private const uint Version = 1;

        public override uint GetVersion() => Version;

        // After the pipeline's own preprocessors (the Universal one runs at -980 and leaves .dae alone).
        public override int GetPostprocessOrder() => -900;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        private static readonly int Surface = Shader.PropertyToID("_Surface");

        public void OnPreprocessMaterialDescription(MaterialDescription description, Material material, AnimationClip[] clips)
        {
            if (!string.Equals(Path.GetExtension(assetPath), ".dae", System.StringComparison.OrdinalIgnoreCase)) return;

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return;                      // built-in: the importer's material is right
            Shader shader = pipeline.defaultShader;
            if (shader == null || shader.FindPropertyIndex("_BaseColor") < 0) return;

            material.shader = shader;

            // Opacity, as the FBX SDK reports it for Collada.
            float opacity = 1f;
            if (!description.TryGetProperty("Opacity", out opacity))
            {
                opacity = 1f;
                if (description.TryGetProperty("TransparencyFactor", out float transparency))
                    opacity = transparency == 1f ? 1f : 1f - transparency;
                if (opacity == 1f && description.TryGetProperty("TransparentColor", out Vector4 transparentColor))
                    opacity = transparentColor.x == 1f ? 1f : 1f - transparentColor.x;
            }
            bool transparent = opacity < 1f;
            if (material.HasProperty(Surface))
            {
                material.SetFloat(Surface, transparent ? 1f : 0f);
                material.SetOverrideTag("RenderType", transparent ? "Transparent" : "");
                material.SetFloat("_SrcBlend", (float)BlendMode.One);
                material.SetFloat("_DstBlend", transparent ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
                material.SetFloat("_ZWrite", transparent ? 0f : 1f);
                if (transparent)
                {
                    material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.renderQueue = (int)RenderQueue.Transparent;
                }
                else
                {
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.renderQueue = -1;
                }
            }

            if (description.TryGetProperty("DiffuseColor", out TexturePropertyDescription texture) && texture.texture != null)
            {
                Color tint = Color.white;
                if (description.TryGetProperty("DiffuseFactor", out float factor)) tint *= factor;
                tint.a = opacity;
                if (material.HasProperty(BaseMap))
                {
                    material.SetTexture(BaseMap, texture.texture);
                    material.SetTextureOffset(BaseMap, texture.offset);
                    material.SetTextureScale(BaseMap, texture.scale);
                }
                material.SetColor(BaseColor, tint);
            }
            else if (description.TryGetProperty("DiffuseColor", out Vector4 diffuse))
            {
                Color color = diffuse;
                color.a = opacity;
                material.SetColor(BaseColor, PlayerSettings.colorSpace == ColorSpace.Linear ? color.gamma : color);
            }

            if (material.HasProperty(Smoothness))
                material.SetFloat(Smoothness, description.TryGetProperty("Shininess", out float shininess) ? Mathf.Sqrt(Mathf.Max(0f, shininess) * 0.01f) : 0f);
        }
    }
}
