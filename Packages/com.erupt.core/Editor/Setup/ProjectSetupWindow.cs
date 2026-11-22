using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Erupt.Editor.Setup
{
    /// <summary>ERUPT &gt; Project Setup: the list <see cref="ProjectSetup"/> checks, with a button to put right what it can.</summary>
    public class ProjectSetupWindow : EditorWindow
    {
        private List<SetupItem> items;
        private Vector2 scroll;

        [MenuItem("ERUPT/Project Setup", priority = -100)]
        public static void Open()
        {
            var window = GetWindow<ProjectSetupWindow>(true, "ERUPT Project Setup");
            window.minSize = new Vector2(560f, 420f);
            window.items = ProjectSetup.Run(false);
        }

        private void OnEnable() => items ??= ProjectSetup.Run(false);

        private void OnGUI()
        {
            EditorGUILayout.LabelField("What this project needs before ERUPT works in it", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (SetupItem item in items)
            {
                MessageType type = item.State == SetupState.NeedsYou ? MessageType.Warning : MessageType.Info;
                string state = item.State == SetupState.Fine ? "Fine" : item.State == SetupState.Fixed ? "Fixed" : "Needs you";
                EditorGUILayout.HelpBox($"{state}: {item.Title}\n{item.Detail}", type);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check Again", GUILayout.Height(26f))) items = ProjectSetup.Run(false);
                GUI.enabled = items.Any(i => i.CanFix && i.State == SetupState.NeedsYou);
                if (GUILayout.Button(new GUIContent("Fix", "Put right everything that can be put right from here."), GUILayout.Height(26f)))
                    items = ProjectSetup.Run(true);
                GUI.enabled = true;
            }
            EditorGUILayout.LabelField(
                $"{items.Count(i => i.State == SetupState.Fine)} fine, {items.Count(i => i.State == SetupState.Fixed)} fixed, {items.Count(i => i.State == SetupState.NeedsYou)} need you.",
                EditorStyles.miniLabel);
        }
    }
}
