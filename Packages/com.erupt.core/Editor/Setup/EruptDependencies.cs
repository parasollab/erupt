using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Erupt.Editor.Setup
{
    /// <summary>A package an ERUPT package needs that its <c>package.json</c> cannot declare as a dependency.</summary>
    [Serializable]
    public class EruptRequirement
    {
        [Tooltip("Package name, e.g. com.unity.robotics.ros-tcp-connector.")]
        public string name = "";
        [Tooltip("What to put in the project manifest: a git URL, or a version for a registry package.")]
        public string source = "";
        [Tooltip("Why the package is needed, in a few words.")]
        public string reason = "";
        /// <summary>The ERUPT package that asks for it.</summary>
        [NonSerialized] public string askedBy = "";
    }

    /// <summary>
    /// The packages ERUPT needs that the Package Manager does not install by itself. A package
    /// that comes from git cannot have git dependencies, so <c>com.erupt.core</c> cannot declare
    /// the ROS-TCP-Connector and URDF-Importer forks in its <c>dependencies</c>. Instead every
    /// ERUPT package lists what it needs under <c>"erupt": { "requires": [ ... ] }</c> in its
    /// <c>package.json</c>, and this class reads those lists from the ERUPT packages in the
    /// project, says which are missing and adds them to the project manifest.
    /// An optional package asks the same way for what only it needs (the AR module: the AprilTag
    /// fork and Meta's MR Utility Kit), so a project without that package gets none of it.
    /// </summary>
    /// <remarks>
    /// This assembly references no other package and has no define constraint, so it compiles
    /// and runs in a project where the requirements are still missing. ERUPT's other assemblies
    /// are constrained on the packages they need and stay out of the way until those are there.
    /// From the command line: <c>-executeMethod Erupt.Editor.Setup.EruptDependencies.AddMissingFromCommandLine</c>.
    /// </remarks>
    [InitializeOnLoad]
    public static class EruptDependencies
    {
        private const string AskedKey = "Erupt.Setup.AskedAboutDependencies";

        [Serializable]
        private class Manifest
        {
            public string name = "";
            public EruptSection erupt = new EruptSection();
        }

        [Serializable]
        private class EruptSection
        {
            public EruptRequirement[] requires = Array.Empty<EruptRequirement>();
        }

        static EruptDependencies()
        {
            // Once per editor session, after the editor has finished loading.
            if (Application.isBatchMode || SessionState.GetBool(AskedKey, false)) return;
            EditorApplication.delayCall += Offer;
        }

        /// <summary>The ERUPT packages in the project (name starts with <c>com.erupt.</c>).</summary>
        public static List<PackageInfo> EruptPackages() =>
            PackageInfo.GetAllRegisteredPackages().Where(p => p.name.StartsWith("com.erupt.", StringComparison.Ordinal)).ToList();

        /// <summary>Everything the ERUPT packages in the project ask for, without duplicates.</summary>
        public static List<EruptRequirement> Required()
        {
            var required = new Dictionary<string, EruptRequirement>();
            foreach (PackageInfo package in EruptPackages())
                foreach (EruptRequirement requirement in ReadRequirements(Path.Combine(package.resolvedPath, "package.json")))
                {
                    requirement.askedBy = package.name;
                    if (!required.ContainsKey(requirement.name)) required[requirement.name] = requirement;
                }
            return required.Values.OrderBy(r => r.name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The requirements that are not in the project.</summary>
        public static List<EruptRequirement> Missing()
        {
            var installed = new HashSet<string>(PackageInfo.GetAllRegisteredPackages().Select(p => p.name));
            return Required().Where(r => !installed.Contains(r.name)).ToList();
        }

        /// <summary>The <c>erupt.requires</c> list of a package manifest; empty when the file has none.</summary>
        public static List<EruptRequirement> ReadRequirements(string packageJsonPath)
        {
            var result = new List<EruptRequirement>();
            if (!File.Exists(packageJsonPath)) return result;
            Manifest manifest;
            try { manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(packageJsonPath)); }
            catch (ArgumentException) { return result; }
            foreach (EruptRequirement requirement in manifest?.erupt?.requires ?? Array.Empty<EruptRequirement>())
                if (requirement != null && !string.IsNullOrWhiteSpace(requirement.name) && !string.IsNullOrWhiteSpace(requirement.source))
                    result.Add(requirement);
            return result;
        }

        /// <summary>What the Package Manager is asked to add for a requirement: <c>name@source</c>.</summary>
        public static string Identifier(EruptRequirement requirement) => $"{requirement.name}@{requirement.source}";

        /// <summary>
        /// Add the missing requirements to the project manifest. The Package Manager then
        /// fetches them and the editor recompiles; <paramref name="done"/> gets null on success
        /// and the error otherwise. Returns false when nothing was missing.
        /// </summary>
        public static bool AddMissing(Action<string> done = null)
        {
            List<EruptRequirement> missing = Missing();
            if (missing.Count == 0) return false;

            AddAndRemoveRequest request = Client.AddAndRemove(missing.Select(Identifier).ToArray());
            void Poll()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Poll;
                string error = request.Status == StatusCode.Success ? null : request.Error?.message ?? "The Package Manager could not add the packages.";
                if (error == null) Debug.Log("[ERUPT] Added to the project manifest: " + string.Join(", ", missing.Select(r => r.name)) + ".");
                else Debug.LogError("[ERUPT] Could not add " + string.Join(", ", missing.Select(r => r.name)) + ": " + error);
                done?.Invoke(error);
            }
            EditorApplication.update += Poll;
            return true;
        }

        /// <summary>
        /// Batch entry point: adds what is missing and waits for the Package Manager and the
        /// recompile that follows. Run it without <c>-quit</c>; it closes the editor itself
        /// (exit code 1 when the packages could not be added).
        /// </summary>
        public static void AddMissingFromCommandLine()
        {
            List<EruptRequirement> missing = Missing();
            if (missing.Count == 0)
            {
                Debug.Log("[ERUPT] Nothing to add: every package the ERUPT packages need is in the project.");
                EditorApplication.Exit(0);
                return;
            }
            Debug.Log("[ERUPT] Adding: " + string.Join(", ", missing.Select(Identifier)));
            AddMissing(error => { if (error != null) EditorApplication.Exit(1); });
            // The editor recompiles once the packages are there; leave when that is done.
            ProjectSetup.LeaveWhenIdle();
        }

        private static void Offer()
        {
            SessionState.SetBool(AskedKey, true);
            List<EruptRequirement> missing;
            try { missing = Missing(); }
            catch (Exception e) { Debug.LogWarning("[ERUPT] Could not check which packages ERUPT needs: " + e.Message); return; }
            if (missing.Count == 0)
            {
                // The packages are there; without the ROS2 define ERUPT's ROS assemblies are still left out.
                if (!ProjectSetup.Ros2IsSetForTheActiveTarget() &&
                    EditorUtility.DisplayDialog("ERUPT: one more step",
                        "ERUPT talks ROS 2, and the ROS-TCP-Connector does so only with the scripting define ROS2. It is not set for this " +
                        "build target, so the parts of ERUPT that talk to ROS are not compiled yet.\n\nProject Setup sets it, together with " +
                        "the tags and the other settings ERUPT needs.",
                        "Open Project Setup", "Not now"))
                    ProjectSetupWindow.Open();
                return;
            }

            string list = string.Join("\n", missing.Select(r => $"• {r.name}{(string.IsNullOrEmpty(r.reason) ? "" : " (" + r.reason + ")")}"));
            bool add = EditorUtility.DisplayDialog("ERUPT needs more packages",
                "The ERUPT packages in this project need packages that the Package Manager does not install with them (a package " +
                "that comes from git cannot bring its own git dependencies):\n\n" +
                list + "\n\nAdd them to this project's manifest (Packages/manifest.json)? " +
                "Until they are there, most of ERUPT is not compiled.",
                "Add", "Not now");
            if (add) AddMissing(error => { if (error == null) Debug.Log("[ERUPT] Next: ERUPT > Project Setup (tags, the ROS2 scripting define, XR settings)."); });
            else Debug.Log("[ERUPT] Not added: " + string.Join(", ", missing.Select(Identifier)) + ". ERUPT > Project Setup offers them again.");
        }
    }
}
