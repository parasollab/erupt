using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Erupt.Editor.Setup
{
    public enum SetupState
    {
        /// <summary>Nothing to do.</summary>
        Fine,
        /// <summary>It was wrong and Project Setup put it right.</summary>
        Fixed,
        /// <summary>It is wrong, and it is for the user to decide or to do.</summary>
        NeedsYou,
    }

    /// <summary>One thing Project Setup looks at.</summary>
    public sealed class SetupItem
    {
        public string Title = "";
        public SetupState State = SetupState.Fine;
        public string Detail = "";
        /// <summary>Running Project Setup with fixing on would put it right.</summary>
        public bool CanFix;

        public override string ToString() =>
            $"{(State == SetupState.Fine ? "fine" : State == SetupState.Fixed ? "fixed" : "needs you")}: {Title}{(string.IsNullOrEmpty(Detail) ? "" : " - " + Detail)}";
    }

    /// <summary>
    /// ERUPT &gt; Project Setup: what a project needs before ERUPT works in it, checked and where
    /// possible put right. The packages ERUPT cannot declare as dependencies, the tags its code
    /// uses, the <c>ROS2</c> scripting define (without it the ROS-TCP-Connector speaks the ROS 1
    /// protocol and never connects), TextMesh Pro's essential resources, the input system and
    /// the XR settings. Each item ends up fine, fixed, or needing the user.
    /// </summary>
    /// <remarks>
    /// The XR packages are reached by reflection, so this assembly needs none of them.
    /// From the command line: <c>-executeMethod Erupt.Editor.Setup.ProjectSetup.RunFromCommandLine [-check]</c>
    /// (without <c>-quit</c>; it closes the editor when the Package Manager is done).
    /// </remarks>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        private const string LeaveKey = "Erupt.Setup.LeaveWhenIdle";

        static ProjectSetup()
        {
            // A batch run that added packages goes through a domain reload, which forgets
            // what was waiting to close the editor; the session remembers.
            if (Application.isBatchMode && SessionState.GetBool(LeaveKey, false)) LeaveWhenIdle();
        }

        // Closes the editor once nothing is being compiled, imported or fetched any more.
        internal static void LeaveWhenIdle()
        {
            SessionState.SetBool(LeaveKey, true);
            double quietSince = EditorApplication.timeSinceStartup;
            void Leave()
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) { quietSince = EditorApplication.timeSinceStartup; return; }
                if (EditorApplication.timeSinceStartup - quietSince < 5.0) return;
                EditorApplication.update -= Leave;
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
            }
            EditorApplication.update += Leave;
        }

        /// <summary>Tags ERUPT's code sets or compares, and the one the URDF Importer puts on robots.</summary>
        public static readonly string[] Tags = { "Selectable", "SnapSurface", "robot" };

        /// <summary>The scripting define that makes the ROS-TCP-Connector speak ROS 2.</summary>
        public const string Ros2Define = "ROS2";

        /// <summary>The build targets ERUPT runs under: the editor's Play mode and the headset.</summary>
        public static readonly NamedBuildTarget[] BuildTargets = { NamedBuildTarget.Standalone, NamedBuildTarget.Android };

        private const string OpenXrPackage = "com.unity.xr.openxr";
        private const string OpenXrLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string TmpEssentials = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";

        // OpenXR features ERUPT's input backends rely on, by type name, with the build targets they matter for.
        private static readonly (string type, string label, BuildTargetGroup[] groups)[] OpenXrFeatures =
        {
            ("OculusTouchControllerProfile", "Oculus Touch Controller Profile", new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android }),
            ("HandTracking", "Hand Tracking Subsystem", new[] { BuildTargetGroup.Android }),
            ("MetaHandTrackingAim", "Meta Hand Tracking Aim", new[] { BuildTargetGroup.Android }),
            ("MetaQuestFeature", "Meta Quest Support", new[] { BuildTargetGroup.Android }),
        };

        /// <summary>Look at everything; with <paramref name="fix"/>, put right what can be put right.</summary>
        public static List<SetupItem> Run(bool fix)
        {
            var items = new List<SetupItem>();
            foreach (Func<bool, SetupItem> check in new Func<bool, SetupItem>[]
                     { Packages, ProjectTags, Ros2, TextMeshPro, InputHandling, RenderPipeline, RigSamples, OpenXrInstalled, XrLoaders, XrFeatures })
            {
                try { items.Add(check(fix)); }
                catch (Exception e)
                {
                    items.Add(new SetupItem { Title = check.Method.Name, State = SetupState.NeedsYou, Detail = $"The check failed: {e.GetType().Name}: {e.Message}" });
                }
            }
            if (fix) AssetDatabase.SaveAssets();
            return items;
        }

        // ---- packages ----

        private static SetupItem Packages(bool fix)
        {
            var item = new SetupItem { Title = "Packages ERUPT needs" };
            List<EruptRequirement> missing = EruptDependencies.Missing();
            if (missing.Count == 0)
            {
                item.Detail = string.Join(", ", EruptDependencies.Required().Select(r => r.name).DefaultIfEmpty("none asked for"));
                return item;
            }
            string names = string.Join(", ", missing.Select(r => $"{r.name} (for {r.askedBy})"));
            item.CanFix = true;
            if (fix && EruptDependencies.AddMissing())
            {
                item.State = SetupState.Fixed;
                item.Detail = $"Adding {names} to the project manifest; the editor fetches them and recompiles. Run Project Setup again afterwards.";
            }
            else
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Missing: {names}. Fix adds them to Packages/manifest.json.";
            }
            return item;
        }

        // ---- tags ----

        private static SetupItem ProjectTags(bool fix)
        {
            var item = new SetupItem { Title = "Tags" };
            List<string> missing = Tags.Where(tag => !UnityEditorInternal.InternalEditorUtility.tags.Contains(tag)).ToList();
            if (missing.Count == 0) { item.Detail = string.Join(", ", Tags); return item; }
            item.CanFix = true;
            if (!fix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Missing: {string.Join(", ", missing)}. ERUPT tags the objects it makes selectable and looks for snap surfaces by tag; an undefined tag is an error at run time.";
                return item;
            }
            foreach (string tag in missing) UnityEditorInternal.InternalEditorUtility.AddTag(tag);
            item.State = SetupState.Fixed;
            item.Detail = $"Added {string.Join(", ", missing)}.";
            return item;
        }

        // ---- ROS 2 ----

        private static SetupItem Ros2(bool fix)
        {
            var item = new SetupItem { Title = $"Scripting define {Ros2Define} (Standalone and Android)" };
            List<NamedBuildTarget> missing = Ros2Targets().Where(target => !Defines(target).Contains(Ros2Define)).ToList();
            if (missing.Count == 0) { item.Detail = "Set: Play mode in the editor and the headset build speak ROS 2."; return item; }
            item.CanFix = true;
            string where = string.Join(" and ", missing.Select(t => t.TargetName));
            if (!fix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Not set for {where}. Without it the ROS-TCP-Connector speaks the ROS 1 protocol there and never connects to a ROS 2 endpoint, " +
                              "and ERUPT's assemblies that talk to ROS are not compiled.";
                return item;
            }
            foreach (NamedBuildTarget target in missing)
                PlayerSettings.SetScriptingDefineSymbols(target, Defines(target).Append(Ros2Define).ToArray());
            item.State = SetupState.Fixed;
            item.Detail = $"Set for {where}; the scripts recompile.";
            return item;
        }

        /// <summary>True when the build target the editor is working under has the <c>ROS2</c> define.</summary>
        public static bool Ros2IsSetForTheActiveTarget() =>
            Defines(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget))).Contains(Ros2Define);

        // Standalone and Android, and the target the editor is working under when it is another one.
        private static List<NamedBuildTarget> Ros2Targets()
        {
            var targets = new List<NamedBuildTarget>(BuildTargets);
            NamedBuildTarget active = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            if (!targets.Contains(active)) targets.Add(active);
            return targets;
        }

        private static string[] Defines(NamedBuildTarget target)
        {
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            return defines ?? Array.Empty<string>();
        }

        // ---- TextMesh Pro ----

        private static SetupItem TextMeshPro(bool fix)
        {
            var item = new SetupItem { Title = "TextMesh Pro essential resources" };
            if (Resources.Load("TMP Settings") != null) { item.Detail = "Imported."; return item; }
            bool available = File.Exists(Path.GetFullPath(TmpEssentials));
            item.CanFix = available;
            if (!fix || !available)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = available
                    ? "Not imported: ERUPT's menus have no font. Fix imports them (Window > TextMeshPro > Import TMP Essential Resources)."
                    : "Not imported, and the package that carries them (com.unity.ugui) was not found. Import them with Window > TextMeshPro > Import TMP Essential Resources.";
                return item;
            }
            AssetDatabase.ImportPackage(TmpEssentials, false);
            item.State = SetupState.Fixed;
            item.Detail = "Imported into Assets/TextMesh Pro.";
            return item;
        }

        // ---- input ----

        private static SetupItem InputHandling(bool fix)
        {
            var item = new SetupItem { Title = "Active input handling" };
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            SerializedProperty handler = settings.FindProperty("activeInputHandler");
            if (handler == null) { item.State = SetupState.NeedsYou; item.Detail = "Could not read it; set Player > Active Input Handling to Input System Package or Both."; return item; }
            // 0 = the old Input Manager, 1 = the Input System package, 2 = both.
            if (handler.intValue != 0) { item.Detail = handler.intValue == 1 ? "Input System Package." : "Both."; return item; }
            item.CanFix = true;
            if (!fix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = "The old Input Manager only: ERUPT reads controllers through the Input System package. Fix sets it to Both.";
                return item;
            }
            handler.intValue = 2;
            settings.ApplyModifiedPropertiesWithoutUndo();
            item.State = SetupState.Fixed;
            item.Detail = "Set to Both. Restart the editor for it to take effect.";
            return item;
        }

        // ---- render pipeline ----

        private static SetupItem RenderPipeline(bool fix)
        {
            var item = new SetupItem { Title = "Render pipeline" };
            UnityEngine.Rendering.RenderPipelineAsset pipeline = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            if (pipeline != null && pipeline.GetType().FullName.Contains("Universal"))
            {
                item.Detail = "Universal Render Pipeline.";
                return item;
            }
            // ERUPT's own code works under any pipeline; some shipped materials do not.
            item.State = SetupState.NeedsYou;
            item.Detail = $"{(pipeline == null ? "The built-in pipeline" : pipeline.GetType().Name)}. ERUPT itself works under it. The materials the URDF Importer made for " +
                          "colours a robot's URDF defines (in the robot packages) are Universal Render Pipeline materials and show pink under another pipeline: " +
                          "convert them (Edit > Rendering > Materials) or assign your own, or use the Universal Render Pipeline.";
            return item;
        }

        // ---- samples the Starter Scene's rig is built from ----

        // (package, the folder its samples are imported under, sample name)
        private static readonly (string package, string folder, string sample)[] Samples =
        {
            ("com.unity.xr.interaction.toolkit", "XR Interaction Toolkit", "Starter Assets"),
            ("com.unity.xr.interaction.toolkit", "XR Interaction Toolkit", "Hands Interaction Demo"),
            ("com.unity.xr.hands", "XR Hands", "HandVisualizer"),
        };

        private static SetupItem RigSamples(bool fix)
        {
            var item = new SetupItem { Title = "Samples the Starter Scene's XR rig is built from" };
            // Imported under Assets/Samples/<package>/<version>/<sample>, whichever version it was imported from.
            bool Imported((string package, string folder, string sample) s) =>
                Directory.Exists(Path.Combine("Assets", "Samples", s.folder)) &&
                Directory.GetDirectories(Path.Combine("Assets", "Samples", s.folder)).Any(version => Directory.Exists(Path.Combine(version, s.sample)));

            var missing = Samples.Where(s => !Imported(s)).ToList();
            if (missing.Count == 0) { item.Detail = string.Join(", ", Samples.Select(s => s.sample)) + "."; return item; }
            string names = string.Join(", ", missing.Select(s => $"{s.sample} ({s.folder})"));
            item.CanFix = true;
            if (!fix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Not imported: {names}. The Starter Scene's rig uses their input actions, controller and hand prefabs. Fix imports them " +
                              "(Package Manager > the package > Samples). A project with its own rig does not need them.";
                return item;
            }

            var failed = new List<string>();
            foreach ((string package, string folder, string sample) in missing)
            {
                UnityEditor.PackageManager.UI.Sample found = UnityEditor.PackageManager.UI.Sample.FindByPackage(package, "")
                    .FirstOrDefault(candidate => candidate.displayName == sample);
                if (string.IsNullOrEmpty(found.displayName) ||
                    !found.Import(UnityEditor.PackageManager.UI.Sample.ImportOptions.HideImportWindow | UnityEditor.PackageManager.UI.Sample.ImportOptions.OverridePreviousImports))
                    failed.Add($"{sample} ({folder})");
            }
            if (failed.Count > 0)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Could not import {string.Join(", ", failed)}; import them in the Package Manager window (the package > Samples).";
                return item;
            }
            item.State = SetupState.Fixed;
            item.Detail = $"Imported {names} into Assets/Samples.";
            return item;
        }

        // ---- XR ----

        private static bool HasPackage(string name) => PackageInfo.GetAllRegisteredPackages().Any(p => p.name == name);

        private static SetupItem OpenXrInstalled(bool fix)
        {
            var item = new SetupItem { Title = "OpenXR plug-in" };
            if (HasPackage(OpenXrPackage)) { item.Detail = OpenXrPackage + " is in the project."; return item; }
            item.CanFix = true;
            if (!fix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"{OpenXrPackage} is not in the project: without an XR plug-in nothing reaches the headset. Fix adds it. (The desktop mouse backend works without it.)";
                return item;
            }
            Client.Add(OpenXrPackage);
            item.State = SetupState.Fixed;
            item.Detail = $"Adding {OpenXrPackage}; run Project Setup again when the editor has recompiled, for the XR settings below.";
            return item;
        }

        private static SetupItem XrLoaders(bool fix)
        {
            var item = new SetupItem { Title = "OpenXR is the XR plug-in (Standalone and Android)" };
            const string byHand = "Tick OpenXR for the desktop and the Android tab in Project Settings > XR Plug-in Management.";
            Type perTarget = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
            Type store = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            if (!HasPackage(OpenXrPackage) || perTarget == null || store == null || FindType(OpenXrLoader) == null)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = "The OpenXR plug-in is not installed (or not compiled yet); see above, then run Project Setup again.";
                return item;
            }

            MethodInfo isAssigned = store.GetMethod("IsLoaderAssigned", new[] { typeof(string), typeof(BuildTargetGroup) });
            var missing = new List<BuildTargetGroup>();
            foreach (BuildTargetGroup group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
                if (isAssigned == null || !(bool)isAssigned.Invoke(null, new object[] { OpenXrLoader, group })) missing.Add(group);
            if (missing.Count == 0) { item.Detail = "Assigned for both."; return item; }

            item.CanFix = isAssigned != null;
            string where = string.Join(" and ", missing);
            if (!fix || !item.CanFix)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Not assigned for {where}. {(item.CanFix ? "Fix assigns it." : byHand)}";
                return item;
            }

            try
            {
                // The settings object of XR Plug-in Management, made the way its settings page makes it.
                object settings = perTarget.GetMethod("GetOrCreate", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, null);
                MethodInfo assign = store.GetMethods().First(m => m.Name == "AssignLoader" && m.GetParameters().Length == 3);
                foreach (BuildTargetGroup group in missing)
                {
                    if (!(bool)perTarget.GetMethod("HasSettingsForBuildTarget").Invoke(settings, new object[] { group }))
                        perTarget.GetMethod("CreateDefaultSettingsForBuildTarget").Invoke(settings, new object[] { group });
                    if (!(bool)perTarget.GetMethod("HasManagerSettingsForBuildTarget").Invoke(settings, new object[] { group }))
                        perTarget.GetMethod("CreateDefaultManagerSettingsForBuildTarget").Invoke(settings, new object[] { group });
                    object manager = perTarget.GetMethod("ManagerSettingsForBuildTarget").Invoke(settings, new object[] { group });
                    if (!(bool)assign.Invoke(null, new[] { manager, OpenXrLoader, group }))
                        throw new InvalidOperationException($"XR Plug-in Management did not accept the OpenXR loader for {group}.");
                }
                EditorUtility.SetDirty((UnityEngine.Object)settings);
                item.State = SetupState.Fixed;
                item.Detail = $"Assigned for {where}.";
            }
            catch (Exception e)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Could not assign it for {where} ({(e.InnerException ?? e).Message}). {byHand}";
            }
            return item;
        }

        private static SetupItem XrFeatures(bool fix)
        {
            var item = new SetupItem { Title = "OpenXR features: controller profile, hand tracking, Meta Quest support" };
            const string byHand = "Enable them in Project Settings > XR Plug-in Management > OpenXR.";
            Type settingsType = FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            Type helpers = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
            if (settingsType == null || helpers == null)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = "The OpenXR plug-in is not installed (or not compiled yet); see above, then run Project Setup again.";
                return item;
            }

            var off = new List<string>();
            var absent = new List<string>();
            var enabledNow = new List<string>();
            MethodInfo forGroup = settingsType.GetMethod("GetSettingsForBuildTargetGroup", BindingFlags.Static | BindingFlags.Public);
            foreach (BuildTargetGroup group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                if (fix) helpers.GetMethod("RefreshFeatures", new[] { typeof(BuildTargetGroup) })?.Invoke(null, new object[] { group });
                object settings = forGroup?.Invoke(null, new object[] { group });
                // GetFeatures() without arguments; it has generic namesakes.
                MethodInfo getFeatures = settingsType.GetMethods().FirstOrDefault(m => m.Name == "GetFeatures" && !m.IsGenericMethod && m.GetParameters().Length == 0);
                IEnumerable features = settings != null ? getFeatures?.Invoke(settings, null) as IEnumerable : null;
                var byType = new Dictionary<string, object>();
                if (features != null)
                    foreach (object feature in features)
                        if (feature != null) byType[feature.GetType().Name] = feature;

                foreach ((string type, string label, BuildTargetGroup[] groups) in OpenXrFeatures)
                {
                    if (!groups.Contains(group)) continue;
                    string what = $"{label} ({group})";
                    if (!byType.TryGetValue(type, out object feature)) { absent.Add(what); continue; }
                    PropertyInfo enabled = feature.GetType().GetProperties().First(property => property.Name == "enabled" && property.PropertyType == typeof(bool));
                    if ((bool)enabled.GetValue(feature)) continue;
                    if (!fix) { off.Add(what); continue; }
                    enabled.SetValue(feature, true);
                    EditorUtility.SetDirty((UnityEngine.Object)feature);
                    enabledNow.Add(what);
                }
            }

            if (off.Count == 0 && absent.Count == 0 && enabledNow.Count == 0) { item.Detail = "Enabled."; return item; }
            item.CanFix = off.Count > 0;
            if (absent.Count > 0)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = (enabledNow.Count > 0 ? $"Enabled {string.Join(", ", enabledNow)}. " : "") +
                              $"Not available in this project: {string.Join(", ", absent)}. Hand tracking comes with com.unity.xr.hands once the OpenXR plug-in is installed; " +
                              "if it was just added, run Project Setup again. " + byHand;
            }
            else if (off.Count > 0)
            {
                item.State = SetupState.NeedsYou;
                item.Detail = $"Off: {string.Join(", ", off)}. Fix enables them.";
            }
            else
            {
                item.State = SetupState.Fixed;
                item.Detail = $"Enabled {string.Join(", ", enabledNow)}.";
            }
            return item;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        // ---- command line ----

        /// <summary>
        /// Batch entry point: fixes what can be fixed (only checks with <c>-check</c>) and logs
        /// each item. When packages were added it waits for the Package Manager before closing
        /// the editor; run it again afterwards for what depends on those packages.
        /// </summary>
        public static void RunFromCommandLine()
        {
            bool fix = Array.IndexOf(Environment.GetCommandLineArgs(), "-check") < 0;
            List<SetupItem> items = Run(fix);
            foreach (SetupItem item in items) Debug.Log("[ERUPT setup] " + item);
            int needs = items.Count(i => i.State == SetupState.NeedsYou);
            Debug.Log($"[ERUPT setup] {items.Count(i => i.State == SetupState.Fine)} fine, {items.Count(i => i.State == SetupState.Fixed)} fixed, {needs} need you.");

            // Package requests finish on later editor updates, and adding a package recompiles.
            LeaveWhenIdle();
        }
    }
}
