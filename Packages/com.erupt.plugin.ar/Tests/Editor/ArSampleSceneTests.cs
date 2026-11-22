using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;

namespace Erupt.Plugins.Ar.Editor.Tests
{
    /// <summary>
    /// The AR sample scene, checked as it lies in the package (a sample is not imported until
    /// the user imports it, so it cannot be opened here): everything it refers to exists, it
    /// holds what the README says, the references wired by hand point at objects of the right
    /// kind, and the package's requirements are the ones the project manifest carries.
    /// </summary>
    public class ArSampleSceneTests
    {
        private const string Package = "Packages/com.erupt.plugin.ar";
        private const string Sample = Package + "/Samples~/ArScene";
        private const string ScenePath = Sample + "/ArScene.unity";

        private static readonly Regex Guid = new Regex("guid: ([0-9a-f]{32})");

        private static string Scene => File.ReadAllText(ScenePath);

        // guid -> file, for the assets that ship inside the sample folder.
        private static Dictionary<string, string> SampleAssets() =>
            Directory.EnumerateFiles(Sample, "*.meta")
                .ToDictionary(meta => Guid.Match(File.ReadAllText(meta)).Groups[1].Value, meta => meta.Substring(0, meta.Length - ".meta".Length).Replace('\\', '/'));

        private static string PathOf(string guid, Dictionary<string, string> own) =>
            own.TryGetValue(guid, out string path) ? path : AssetDatabase.GUIDToAssetPath(guid);

        [Test]
        public void TheSample_IsDeclared_AndItsFilesAreThere()
        {
            string manifest = File.ReadAllText(Package + "/package.json");
            StringAssert.Contains("\"path\": \"Samples~/ArScene\"", manifest);
            Assert.IsTrue(File.Exists(ScenePath), ScenePath);
            Assert.IsTrue(File.Exists(ScenePath + ".meta"), "The scene ships with its .meta, so its GUID is the same in every project.");
            Assert.IsTrue(File.Exists(Sample + "/README.md"));
            foreach (string asset in Directory.EnumerateFiles(Sample).Where(f => !f.EndsWith(".meta") && !f.EndsWith(".md")))
                Assert.IsTrue(File.Exists(asset + ".meta"), asset + " has no .meta");
        }

        [Test]
        public void EverythingTheSceneRefersTo_Exists()
        {
            Dictionary<string, string> own = SampleAssets();
            var missing = new List<string>();
            foreach (string file in Directory.EnumerateFiles(Sample).Where(f => f.EndsWith(".unity") || f.EndsWith(".mat")))
                foreach (string guid in Guid.Matches(File.ReadAllText(file)).Cast<Match>().Select(m => m.Groups[1].Value).Distinct())
                {
                    // Unity's built-in resources.
                    if (guid == "0000000000000000e000000000000000" || guid == "0000000000000000f000000000000000") continue;
                    string path = PathOf(guid, own);
                    if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) missing.Add($"{Path.GetFileName(file)}: {guid}");
                }
            Assert.IsEmpty(missing, "The sample refers to assets that are not in the project (a missing script, prefab or material):\n" + string.Join("\n", missing));
        }

        [Test]
        public void TheScene_HoldsWhatItsReadmeSays()
        {
            Dictionary<string, string> own = SampleAssets();
            var paths = Guid.Matches(Scene).Cast<Match>().Select(m => PathOf(m.Groups[1].Value, own)).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var used = paths.Select(Path.GetFileName).ToHashSet();

            foreach (string expected in new[]
                     {
                         // the ERUPT root and the plugins
                         "EruptCore.prefab", "MoveItPlugin.prefab", "MtcPlugin.prefab", "DemonstrationCollectionPlugin.prefab",
                         // the AR module: plugin + tracker, the robot slot's anchor, the room anchors
                         "ArPlugin.prefab", "MarkerAnchor.cs", "SceneAnchorCollisionBridge.cs",
                         // passthrough, the passthrough camera and the room
                         "OVRManager.cs", "OVRPassthroughLayer.cs", "PassthroughCameraAccess.cs", "MRUK.cs"
                     })
                CollectionAssert.Contains(used, expected);

            // Passthrough is on: the camera clears to transparent black and the Meta layer is enabled.
            StringAssert.Contains("isInsightPassthroughEnabled: 1", Scene);
            StringAssert.Contains("requestPassthroughCameraAccessPermissionOnStartup: 1", Scene);
            StringAssert.Contains("requestScenePermissionOnStartup: 1", Scene);
            StringAssert.IsMatch(@"m_ClearFlags: 2\s+m_BackGroundColor: \{r: 0, g: 0, b: 0, a: 0\}", Scene);

            // No robot comes with it (a plugin never names a robot package): the slot is empty.
            // (The package name is put together here so that this file does not name a robot package either.)
            string robotPackages = "Packages/com.erupt." + "robot.";
            Assert.IsEmpty(paths.Where(path => path.StartsWith(robotPackages, StringComparison.Ordinal)).ToList(), "The sample uses an asset of a robot package.");
            Assert.AreEqual(1, Regex.Matches(Scene, @"\n  tagId: \d+").Count, "One marker anchor, on the robot slot.");
        }

        [Test]
        public void TheTrackersReferences_PointAtObjectsOfTheRightKind()
        {
            // The prefab instance's tracker is wired to the scene's passthrough camera (a
            // component) and to the rig's camera offset (a Transform). A reference of the wrong
            // class is nulled by the editor and is a wild pointer in a player build.
            string scene = Scene;
            Match camera = Regex.Match(scene, @"propertyPath: passthroughCamera\s+value:\s+objectReference: \{fileID: (-?\d+)\}");
            Match space = Regex.Match(scene, @"propertyPath: trackingSpace\s+value:\s+objectReference: \{fileID: (-?\d+)\}");
            Assert.IsTrue(camera.Success && camera.Groups[1].Value != "0", "The tracker has no passthrough camera.");
            Assert.IsTrue(space.Success && space.Groups[1].Value != "0", "The tracker has no tracking space.");
            StringAssert.Contains($"--- !u!114 &{camera.Groups[1].Value}\n", scene, "passthroughCamera has to be a component (class 114).");
            StringAssert.Contains($"--- !u!4 &{space.Groups[1].Value}\n", scene, "trackingSpace has to be a Transform (class 4).");

            // ...and that component is the PassthroughCameraAccess.
            int at = scene.IndexOf($"--- !u!114 &{camera.Groups[1].Value}\n", StringComparison.Ordinal);
            string component = scene.Substring(at, Math.Min(1200, scene.Length - at));
            StringAssert.EndsWith("PassthroughCameraAccess.cs", AssetDatabase.GUIDToAssetPath(Guid.Match(component).Groups[1].Value));
        }

        [Test]
        public void WhatTheModuleAsksTheProjectToInstall_IsWhatThisProjectHas()
        {
            // The two lines the README says to delete together with the package folder.
            string manifest = File.ReadAllText(Package + "/package.json");
            string project = File.ReadAllText("Packages/manifest.json");
            var required = Regex.Matches(manifest, "\"name\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"source\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>().ToList();
            CollectionAssert.AreEquivalent(new[] { "jp.keijiro.apriltag", "com.meta.xr.mrutilitykit" }, required.Select(r => r.Groups[1].Value).ToList());
            foreach (Match requirement in required)
            {
                Match inProject = Regex.Match(project, "\"" + Regex.Escape(requirement.Groups[1].Value) + "\"\\s*:\\s*\"([^\"]+)\"");
                Assert.IsTrue(inProject.Success, $"The project manifest does not have {requirement.Groups[1].Value}.");
                Assert.AreEqual(inProject.Groups[1].Value, requirement.Groups[2].Value, requirement.Groups[1].Value);
            }
            StringAssert.Contains("#", required.First(r => r.Groups[1].Value == "jp.keijiro.apriltag").Groups[2].Value, "The fork is pinned to a commit.");
        }

        [Test]
        public void ThePrintableTags_AreThere_AtTheSizeTheirNamesSay()
        {
            string tags = Package + "/Documentation~/tags";
            string[] files = Directory.EnumerateFiles(tags, "tag41_12_id*_100mm.svg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.GreaterOrEqual(files.Length, 4);
            foreach (string file in files)
            {
                string svg = File.ReadAllText(file);
                // A 100 mm tag: cells of 2 cm, so every black cell is a 2 x 2 cm square, and the size is stated on the sheet.
                StringAssert.Contains("width=\"2.0000\" height=\"2.0000\" fill=\"#000\"", svg);
                StringAssert.Contains("Tag Size = 0.100 m", svg);
                StringAssert.Contains("inner square of 5 cells (10.0 cm), not the printed pattern (18.0 cm)", svg);
            }
            Assert.IsTrue(File.Exists(Package + "/Documentation~/make_apriltag_svg.py"));
        }
    }
}
