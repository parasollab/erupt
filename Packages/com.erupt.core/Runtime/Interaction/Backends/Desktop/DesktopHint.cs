using UnityEngine;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// The desktop keys, drawn in a corner of the Game view. <see cref="DesktopControls"/> adds
    /// it while the hint is wanted and removes it otherwise, so nothing pays for an OnGUI pass
    /// in a headset build.
    /// </summary>
    public class DesktopHint : MonoBehaviour
    {
        /// <summary>What to show; one line per group of keys.</summary>
        public string Text { get; set; } = "";

        private GUIStyle style;

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(Text)) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                    richText = false,
                    padding = new RectOffset(10, 10, 8, 8)
                };
                style.normal.textColor = Color.white;
            }
            // Readable on a small Game view and on a large one.
            style.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 55f), 11, 20);

            var content = new GUIContent(Text);
            Vector2 size = style.CalcSize(content);
            GUI.Box(new Rect(8f, Screen.height - size.y - 8f, size.x, size.y), content, style);
        }
    }
}
