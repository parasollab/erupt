using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// The colour swap the robot's highlights share: a material's base colour lives in
    /// <c>_BaseColor</c> (URP) or <c>_Color</c> (built-in), and a highlight sets whichever the
    /// shader has. The robot's highlights lay a property block over the material
    /// (<see cref="RendererTint"/>), which creates no material and so leaves nothing to clean up.
    /// </summary>
    public static class MaterialColor
    {
        public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        public static readonly int ColorId = Shader.PropertyToID("_Color");

        public static Color Get(Material material)
        {
            if (material.HasProperty(BaseColorId)) return material.GetColor(BaseColorId);
            return material.HasProperty(ColorId) ? material.GetColor(ColorId) : Color.white;
        }

        public static void Set(Material material, Color color)
        {
            if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, color);
            if (material.HasProperty(ColorId)) material.SetColor(ColorId, color);
        }

        /// <summary>The same swap as an override on top of the material, for <see cref="Renderer.SetPropertyBlock(MaterialPropertyBlock)"/>.</summary>
        public static void Set(MaterialPropertyBlock block, Color color)
        {
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
        }
    }
}
