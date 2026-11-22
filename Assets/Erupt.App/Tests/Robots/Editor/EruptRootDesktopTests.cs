using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Erupt.App.Robots.Tests
{
    /// <summary>
    /// What a first look in the editor depends on, checked on the assets: the ERUPT root prefab
    /// carries the desktop controls (so no scene wires them), the reference scenes and the
    /// Starter Scene sample use that root and take nothing off it, and the shipped robots are
    /// drawn with the project's render pipeline.
    /// </summary>
    public class EruptRootDesktopTests
    {
        private const string RootPrefab = "Packages/com.erupt.core/Prefabs/EruptCore.prefab";
        private const string Desktop = "Packages/com.erupt.core/Runtime/Interaction/Backends/Desktop/";

        private static string GuidOf(string path)
        {
            Match match = Regex.Match(File.ReadAllText(path + ".meta"), "guid: ([0-9a-f]{32})");
            Assert.IsTrue(match.Success, path + ".meta");
            return match.Groups[1].Value;
        }

        [Test]
        public void TheRootPrefab_CarriesTheDesktopControls_Wired()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefab);
            Assert.IsNotNull(prefab, RootPrefab);
            MonoBehaviour[] parts = prefab.GetComponents<MonoBehaviour>();
            Assert.IsFalse(parts.Any(p => p == null), "No missing script on the root.");

            MonoBehaviour controls = parts.Single(p => p.GetType().Name == "DesktopControls");
            MonoBehaviour mouse = parts.Single(p => p.GetType().Name == "DesktopMouseBackend");
            parts.Single(p => p.GetType().Name == "ObstacleDragBinding");
            var serialized = new SerializedObject(controls);
            Assert.AreSame(mouse, serialized.FindProperty("mouseBackend").objectReferenceValue, "The controls know their mouse backend.");
            Assert.AreSame(parts.Single(p => p.GetType().Name == "TierUiRig"), serialized.FindProperty("ui").objectReferenceValue);
            Assert.IsTrue(serialized.FindProperty("desktopControls").boolValue, "On by default.");
            Assert.IsTrue(serialized.FindProperty("showHint").boolValue);
            Assert.IsFalse(new SerializedObject(parts.Single(p => p.GetType().Name == "TierUiRig")).FindProperty("showUnimplementedVerbs").boolValue,
                "Verbs nothing implements are not on the menus by default.");

            // The desktop assembly is built for the headset too: the prefab must not carry a script the player lacks.
            string asmdef = File.ReadAllText(Desktop + "Erupt.Interaction.Backends.Desktop.asmdef");
            StringAssert.Contains("\"includePlatforms\": []", asmdef);
        }

        private static readonly string[] Scenes =
        {
            "Assets/Scenes/KitchenFR3.unity",
            "Assets/Scenes/MultiArmFR3.unity",
            "Packages/com.erupt.core/Samples~/StarterScene/StarterScene.unity"
        };

        [Test]
        public void TheScene_UsesTheRoot_AndTakesNothingOffIt([ValueSource(nameof(Scenes))] string scenePath)
        {
            Assume.That(File.Exists(scenePath), $"{scenePath} is not in this project.");
            string scene = File.ReadAllText(scenePath);
            string rootGuid = GuidOf(RootPrefab);

            // The root's prefab instance in the scene.
            Match instance = Regex.Match(scene, @"--- !u!1001 &\d+\nPrefabInstance:(?:(?!\n--- ).)*?m_SourcePrefab: \{fileID: \d+, guid: " + rootGuid, RegexOptions.Singleline);
            Assert.IsTrue(instance.Success, "The scene has the ERUPT root as a prefab instance.");
            StringAssert.Contains("m_RemovedComponents: []", instance.Value, "The scene removes no component of the root.");
            foreach (string script in new[] { "DesktopControls.cs", "DesktopMouseBackend.cs" })
            {
                string guid = GuidOf(Desktop + script);
                Assert.IsFalse(Regex.IsMatch(instance.Value, @"propertyPath: m_Enabled\n\s+value: 0") && instance.Value.Contains(guid),
                    $"The scene does not switch {script} off.");
                Assert.IsFalse(scene.Replace(instance.Value, "").Contains(guid), $"The scene has no {script} of its own: the root's serves.");
            }
            Assert.IsFalse(Regex.IsMatch(instance.Value, @"propertyPath: (desktopControls|showHint)\n\s+value: 0"), "The desktop controls are left on.");
        }

        private static System.Collections.Generic.IEnumerable<string> RobotPrefabs()
        {
            var paths = AssetDatabase.FindAssets("t:RobotProfile", new[] { "Packages" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Packages/com.erupt.robot."))
                .Select(path => path.Substring(0, path.Length - ".asset".Length) + ".prefab")
                .OrderBy(path => path, System.StringComparer.Ordinal).ToList();
            if (paths.Count == 0) paths.Add("");
            return paths;
        }

        // The Universal Render Pipeline leaves the materials of Collada meshes on the built-in
        // shader, which it draws magenta. ERUPT converts them at import (ColladaPipelineMaterials).
        [Test]
        public void TheRobot_IsDrawnWithTheProjectsRenderPipeline([ValueSource(nameof(RobotPrefabs))] string prefabPath)
        {
            Assume.That(!string.IsNullOrEmpty(prefabPath), "No robot package (com.erupt.robot.*) is in this project.");
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Assume.That(pipeline != null && pipeline.defaultShader != null && pipeline.defaultShader.FindPropertyIndex("_BaseColor") >= 0,
                "The project uses the built-in pipeline (or the pipeline is not known in this run).");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab, prefabPath);
            var wrong = prefab.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials.Select(material => (renderer, material)))
                .Where(pair => pair.material == null || pair.material.shader == null || pair.material.shader.FindPropertyIndex("_BaseColor") < 0)
                .Select(pair => $"{pair.renderer.name}: {(pair.material == null ? "no material" : pair.material.name + " (" + pair.material.shader.name + ")")}")
                .Distinct().Take(8).ToList();
            Assert.IsEmpty(wrong, "Parts of the robot would be drawn magenta:\n" + string.Join("\n", wrong));
        }
    }
}
