using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace Erupt.Boundary.Tests
{
    /// <summary>
    /// The one rule the package split exists to enforce: core never points at a plugin or
    /// at the reference app. Plugins may point at core and at the plugins they declare.
    /// </summary>
    public class AssemblyBoundaryTests
    {
        private const string CorePackage = "Packages/com.erupt.core";

        private static bool IsCore(string name) =>
            name.StartsWith("Erupt.", StringComparison.Ordinal)
            && !name.StartsWith("Erupt.Plugins.", StringComparison.Ordinal)
            && !name.StartsWith("Erupt.App", StringComparison.Ordinal);

        private static bool IsForbiddenForCore(string name) =>
            name.StartsWith("Erupt.Plugins.", StringComparison.Ordinal)
            || name.StartsWith("Erupt.App", StringComparison.Ordinal);

        [Test]
        public void CoreAssemblies_NeverReferencePluginsOrApp()
        {
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .Where(a => IsCore(a.name))
                .ToList();

            Assert.IsNotEmpty(assemblies, "No Erupt core assemblies found; is the package imported?");

            var violations = assemblies
                .SelectMany(a => a.assemblyReferences.Select(r => (from: a.name, to: r.name)))
                .Where(e => IsForbiddenForCore(e.to))
                .Distinct()
                .Select(e => $"{e.from} -> {e.to}")
                .ToList();

            Assert.IsEmpty(violations, "Core assemblies reference plugin/app code:\n" + string.Join("\n", violations));
        }

        [Test]
        public void CorePackage_NeverNamesPluginMessageTypes()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), CorePackage);
            Assume.That(Directory.Exists(root), $"{CorePackage} not found at {root}");

            // Assembled at runtime so this file does not match its own check.
            string prefix = "RosMessage" + "Types.";
            string[] forbidden = { prefix + "Moveit", prefix + "MoveitTaskConstructorMsgs", prefix + "StudyInterfaces" };

            var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(path => (path, text: File.ReadAllText(path)))
                .SelectMany(f => forbidden.Where(n => f.text.Contains(n)).Select(n => $"{f.path}: {n}"))
                .ToList();

            Assert.IsEmpty(hits, "Core sources name plugin message types:\n" + string.Join("\n", hits));
        }

        [Test]
        public void NoScriptUnderAssets_FallsIntoAssemblyCSharp()
        {
            // Vendor folders ship their own scripts; the project's own code must all be
            // inside an asmdef so it cannot silently grow a new flat assembly.
            string[] vendor = { "Assets/OpenCVForUnity", "Assets/_Heathen Engineering", "Assets/LudicWorlds", "Assets/TextMesh Pro", "Assets/Samples" };
            var strays = Directory.EnumerateFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.StartsWith("Assets/Erupt.App/"))
                .Where(p => !vendor.Any(v => p.StartsWith(v + "/")))
                .ToList();

            Assert.IsEmpty(strays, "Scripts outside Erupt.App would compile into Assembly-CSharp:\n" + string.Join("\n", strays));
        }
    }
}
