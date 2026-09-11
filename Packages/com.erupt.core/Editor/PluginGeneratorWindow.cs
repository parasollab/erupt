using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Erupt.Editor
{
    /// <summary>
    /// ERUPT > Plugins > Create Plugin...: scaffolds a plugin package from a family template.
    /// Files are written first; the prefab (and the optional scene instance) is created by
    /// <see cref="PluginGeneratorFinisher"/> once the new assembly has compiled.
    /// </summary>
    public class PluginGeneratorWindow : EditorWindow
    {
        private string pluginName = "Demo";
        private string packageId = "com.erupt.plugin.demo";
        private string ns = "Erupt.Plugins.Demo";
        private string displayName = "Demo";
        private PluginFamily family = PluginFamily.Planning;
        private bool addToScene = true;
        private bool namesFollowPluginName = true;
        private string message;

        [MenuItem("ERUPT/Plugins/Create Plugin...")]
        public static void Open()
        {
            var window = GetWindow<PluginGeneratorWindow>(true, "Create ERUPT Plugin");
            window.minSize = new Vector2(460f, 330f);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("New plugin package", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            pluginName = EditorGUILayout.TextField(new GUIContent("Plugin name", "PascalCase; becomes the class prefix and assembly name."), pluginName);
            if (EditorGUI.EndChangeCheck() && namesFollowPluginName)
            {
                var d = PluginSpec.Default(pluginName, family);
                packageId = d.PackageId; ns = d.Namespace; displayName = d.DisplayName;
            }

            EditorGUI.BeginChangeCheck();
            packageId = EditorGUILayout.TextField("Package id", packageId);
            ns = EditorGUILayout.TextField("Namespace", ns);
            displayName = EditorGUILayout.TextField("Display name", displayName);
            if (EditorGUI.EndChangeCheck()) namesFollowPluginName = false;

            family = (PluginFamily)EditorGUILayout.EnumPopup("Template family", family);
            EditorGUILayout.HelpBox(PluginTemplate.For(family).Description + $"\nBase class: {PluginTemplate.For(family).BaseClass}", MessageType.None);

            addToScene = EditorGUILayout.ToggleLeft("Add to open scene (instantiates the prefab next to the ERUPT root)", addToScene);

            var spec = Spec();
            string problem = spec.Validate();
            string root = PackageRoot(spec);
            bool exists = Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
            var conflicts = problem == null ? PluginGenerator.AssemblyConflicts(spec, "Packages", "Assets") : System.Array.Empty<string>();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", root);
            using (new EditorGUI.DisabledScope(true))
                foreach (var f in PluginGenerator.Plan(spec, PluginTemplate.For(family)))
                    EditorGUILayout.LabelField("  " + f);
            EditorGUILayout.LabelField($"  Prefabs/{spec.PluginName}Plugin.prefab   (after compilation)");

            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
            else if (exists) EditorGUILayout.HelpBox($"{root} already exists and is not empty.", MessageType.Error);
            else if (conflicts.Count > 0) EditorGUILayout.HelpBox("An assembly this plugin would define already exists — pick another plugin name:\n" + string.Join("\n", conflicts), MessageType.Error);
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);

            using (new EditorGUI.DisabledScope(problem != null || exists || conflicts.Count > 0))
            {
                if (GUILayout.Button("Create", GUILayout.Height(30f)))
                    Create(spec);
            }
        }

        private PluginSpec Spec() => new()
        {
            PluginName = pluginName?.Trim() ?? "",
            PackageId = packageId?.Trim() ?? "",
            Namespace = ns?.Trim() ?? "",
            Family = family,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? pluginName : displayName.Trim()
        };

        public static string PackageRoot(PluginSpec spec) => Path.Combine("Packages", spec.PackageId);

        private void Create(PluginSpec spec)
        {
            try
            {
                string root = PackageRoot(spec);
                var written = PluginGenerator.Generate(spec, PluginTemplate.For(spec.Family), root);
                PluginGeneratorFinisher.Schedule(spec, root, addToScene);
                message = $"Wrote {written.Count} files to {root}. The prefab{(addToScene ? " and scene instance" : "")} will be created when the new assembly has compiled.";
                Debug.Log($"[ERUPT] Generated plugin '{spec.PluginName}' ({spec.Family}) at {root}:\n  " + string.Join("\n  ", written));
                // A folder that appears under Packages/ while the Editor runs is only picked up
                // by a package resolve; an asset refresh alone leaves it invisible and uncompiled.
                UnityEditor.PackageManager.Client.Resolve();
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                message = null;
                EditorUtility.DisplayDialog("Create plugin failed", e.Message, "OK");
            }
        }
    }
}
