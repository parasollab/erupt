using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace Erupt.Boundary.Tests
{
    /// <summary>
    /// The one rule the package split exists to enforce: core never points at a plugin or
    /// at the reference app. Plugins may point at core and at the plugins they declare.
    /// The lab-only plugin is not published, so nothing else may point at it either.
    /// Four conventions ride along: every ERUPT type lives in an <c>Erupt.*</c> namespace, the
    /// static ROS bus provider is read only by the files on an explicit allowlist, plugins
    /// take robots from the registry instead of looking them up in the scene, and joint
    /// positions are written in one file, behind the joint limits.
    /// </summary>
    public class AssemblyBoundaryTests
    {
        private const string CorePackage = "Packages/com.erupt.core";

        // Assembled at runtime so this file does not match the CI grep for these names.
        private static readonly string LabAssembly = "Erupt.Plugins." + "Lab";
        private static readonly string LabPackage = "Packages/com.erupt.plugin." + "lab";

        private static bool IsLab(string name) =>
            name == LabAssembly || name.StartsWith(LabAssembly + ".", StringComparison.Ordinal);

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
            string[] forbidden = { prefix + "Moveit", prefix + "MoveitTaskConstructorMsgs", prefix + "EruptInterfaces" };

            var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(path => (path, text: File.ReadAllText(path)))
                .SelectMany(f => forbidden.Where(n => f.text.Contains(n)).Select(n => $"{f.path}: {n}"))
                .ToList();

            Assert.IsEmpty(hits, "Core sources name plugin message types:\n" + string.Join("\n", hits));
        }

        [Test]
        public void NoOtherAssembly_ReferencesTheLabPlugin()
        {
            var violations = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .Where(a => a.name.StartsWith("Erupt.", StringComparison.Ordinal) && !IsLab(a.name))
                .SelectMany(a => a.assemblyReferences.Select(r => (from: a.name, to: r.name)))
                .Where(e => IsLab(e.to))
                .Distinct()
                .Select(e => $"{e.from} -> {e.to}")
                .ToList();

            Assert.IsEmpty(violations, "Assemblies outside the lab package reference it:\n" + string.Join("\n", violations));
        }

        [Test]
        public void NoSceneOrPrefabOutsideTheLabPackage_UsesALabAsset()
        {
            // A checkout without the lab package has nothing to check.
            if (!Directory.Exists(LabPackage)) return;
            var hits = UsesOfAssetsOf(LabPackage);
            Assert.IsEmpty(hits, "Scenes, prefabs or assets outside the lab package use its scripts:\n" + string.Join("\n", hits));
        }

        // Scenes, prefabs and assets outside a package that refer to an asset of that package (by GUID).
        private static System.Collections.Generic.List<string> UsesOfAssetsOf(string package)
        {
            var guids = Directory.EnumerateFiles(package, "*.meta", SearchOption.AllDirectories)
                .SelectMany(File.ReadLines)
                .Where(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Select(line => line.Substring("guid: ".Length).Trim())
                .ToList();
            Assert.IsNotEmpty(guids, $"{package} has no .meta files.");

            string[] serialized = { ".unity", ".prefab", ".asset" };
            return new[] { "Assets", "Packages" }
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => serialized.Contains(Path.GetExtension(path)))
                .Where(path => !path.StartsWith(package + "/", StringComparison.Ordinal))
                .Select(path => (path, text: File.ReadAllText(path)))
                .SelectMany(f => guids.Where(g => f.text.Contains(g)).Select(g => $"{f.path}: guid {g}"))
                .ToList();
        }

        // ---- the AR module: optional, known to nobody, the only user of AprilTag and the MR Utility Kit ----

        // Assembled at runtime so this file does not match its own checks or the CI greps.
        private static readonly string ArAssembly = "Erupt.Plugins." + "Ar";
        private static readonly string ArPackage = "Packages/com.erupt.plugin." + "ar";
        private static readonly string[] ArOnlyAssemblies = { "AprilTag" + ".Runtime", "meta.xr." + "mrutilitykit" };

        private static bool IsAr(string name) =>
            name == ArAssembly || name.StartsWith(ArAssembly + ".", StringComparison.Ordinal);

        [Test]
        public void NoOtherAssembly_ReferencesTheArModule_AndOnlyItReferencesAprilTagAndTheMrUtilityKit()
        {
            // A project gets the AR module only if it wants AR: deleting the package (and the two
            // manifest lines it asks for) must leave everything else compiling. So nothing points
            // at the module, and nothing but the module points at what only the module needs.
            var edges = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .Where(a => a.name.StartsWith("Erupt.", StringComparison.Ordinal) && !IsAr(a.name))
                .SelectMany(a => a.assemblyReferences.Select(r => (from: a.name, to: r.name)))
                .Distinct()
                .ToList();
            Assert.IsNotEmpty(edges, "No Erupt assemblies found; is the package imported?");

            var violations = edges
                .Where(e => IsAr(e.to) || ArOnlyAssemblies.Contains(e.to))
                .Select(e => $"{e.from} -> {e.to}")
                .ToList();
            Assert.IsEmpty(violations, "Assemblies outside the AR module reference it, AprilTag or the MR Utility Kit:\n" + string.Join("\n", violations));
        }

        [Test]
        public void NoSourceOutsideTheArPackage_NamesItOrWhatOnlyItNeeds()
        {
            // By name as well as by assembly reference: a manifest that lists the module as a
            // dependency, or code that uses the passthrough camera, would not survive its removal.
            string module = "com.erupt.plugin." + "ar";
            var names = new Regex(@"\b" + Regex.Escape(ArAssembly) + @"\b|" + Regex.Escape(module) + @"\b|\busing\s+" + "AprilTag" + @"\b|Meta\.XR\." +
                                  "MRUtilityKit" + @"|\bPassthrough" + @"CameraAccess\b|jp\.keijiro\." + "apriltag");
            string[] sources = { ".cs", ".asmdef", ".txt" };

            var hits = Directory.EnumerateDirectories("Packages", "com.erupt.*")
                .Append(Path.Combine("Assets", "Erupt.App"))
                .Select(root => root.Replace('\\', '/'))
                .Where(root => root != ArPackage && Directory.Exists(root))
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => sources.Contains(Path.GetExtension(path)) || Path.GetFileName(path) == "package.json")
                .Where(path => names.IsMatch(File.ReadAllText(path)))
                .ToList();
            Assert.IsEmpty(hits, "Sources outside the AR package name it, AprilTag, the MR Utility Kit or the passthrough camera:\n" + string.Join("\n", hits));
        }

        [Test]
        public void NoSceneOrPrefabOutsideTheArPackage_UsesAnArAsset()
        {
            // A checkout without the AR module has nothing to check.
            if (!Directory.Exists(ArPackage)) return;
            var hits = UsesOfAssetsOf(ArPackage);
            Assert.IsEmpty(hits, "Scenes, prefabs or assets outside the AR package use its scripts or prefabs:\n" + string.Join("\n", hits));
        }

        [Test]
        public void EveryEruptType_IsInAnEruptNamespace()
        {
            // Generated ROS message bindings keep the RosMessageTypes namespaces the generator gives them.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("Erupt.", StringComparison.Ordinal))
                .Where(a => !a.GetName().Name.EndsWith(".Messages", StringComparison.Ordinal))
                .ToList();

            string[] expected = { "Erupt.Interaction.Core", "Erupt.Ui", "Erupt.Ros", "Erupt.Robot", "Erupt.Environment", "Erupt.Plugins" };
            var loaded = assemblies.Select(a => a.GetName().Name).ToList();
            CollectionAssert.IsSubsetOf(expected, loaded, "Core runtime assemblies are not loaded; nothing would be checked.");

            var violations = assemblies
                .SelectMany(a => LoadableTypes(a).Select(t => (assembly: a.GetName().Name, type: t)))
                .Where(e => !e.type.IsNested)
                .Where(e => !e.type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                .Where(e => !e.type.Name.StartsWith("<", StringComparison.Ordinal))
                .Where(e => !InEruptNamespace(e.type.Namespace))
                .Select(e => $"{e.assembly}: {e.type.FullName}")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(violations, "Types outside an Erupt.* namespace:\n" + string.Join("\n", violations));
        }

        private static bool InEruptNamespace(string ns) =>
            ns != null && (ns == "Erupt" || ns.StartsWith("Erupt.", StringComparison.Ordinal));

        private static Type[] LoadableTypes(System.Reflection.Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        [Test]
        public void TheStaticRosBus_IsReadOnlyByTheAllowlist()
        {
            // Components get their bus from the plugin context (a robot's own traffic from
            // RobotInstance.Ros). Only the host reads the static provider, when it builds that context.
            string[] allowed =
            {
                "Packages/com.erupt.core/Runtime/Plugins/PluginHost.cs"
            };

            // Assembled at runtime so this file does not match its own check or the CI grep.
            var use = new Regex(@"\bRosBus\s*\.\s*" + "Instance" + @"\b");

            var sources = Directory.EnumerateDirectories("Packages", "com.erupt.*")
                .Append("Assets")
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".cs.txt", StringComparison.Ordinal))
                .ToList();
            Assert.IsNotEmpty(sources, "No ERUPT sources found; is the working directory the project root?");

            var hits = sources
                .Where(path => !allowed.Contains(path))
                .Where(path => use.IsMatch(File.ReadAllText(path)))
                .ToList();

            Assert.IsEmpty(hits, "The static ROS bus is read outside the allowlist (take the bus from IEruptContext.Ros):\n" + string.Join("\n", hits));
        }

        [Test]
        public void Plugins_TakeRobotsFromTheRegistry_NotFromTheScene()
        {
            // A plugin works on the robots the context gives it (IEruptContext.Robots); a scene
            // lookup would always find the first robot, whichever one the plugin is serving.
            string robotTypes = "DirectArticulationIKController|RobotInstance|JointTrajectoryPlayer|RobotGhosts|RobotInteractionController";
            var lookup = new Regex(@"Find(First|Any)?Objects?(By|Of)Type\s*<\s*(" + robotTypes + @")\s*>");

            var hits = Directory.EnumerateDirectories("Packages", "com.erupt.plugin.*")
                .Select(root => Path.Combine(root, "Runtime"))
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => lookup.IsMatch(File.ReadAllText(path)))
                .ToList();

            Assert.IsEmpty(hits, "Plugin code looks robots up in the scene (use IEruptContext.Robots):\n" + string.Join("\n", hits));
        }

        [Test]
        public void JointPositions_AreWrittenInOneRuntimeFile()
        {
            // Joints are teleported, and PhysX does not apply joint limits to a teleport: the
            // limits hold only because every write goes through JointWriter, which clamps to
            // the robot's limit model. A second place that assigns jointPosition is a way
            // around the limits.
            const string writer = "Packages/com.erupt.core/Runtime/Robot/JointWriter.cs";
            var write = new Regex(@"\.jointPosition\s*=[^=]|\.SetJointPositions\s*\(");

            var hits = Directory.EnumerateDirectories("Packages", "com.erupt.*")
                .Select(root => Path.Combine(root, "Runtime"))
                .Append(Path.Combine("Assets", "Erupt.App"))
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => write.IsMatch(File.ReadAllText(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(new[] { writer }, hits,
                "Joint positions are written outside JointWriter (write through IRobotModel.ApplyJointState or JointWriter.ApplyJointPosition):\n"
                + string.Join("\n", hits));
        }

        // ---- robot packages: assets only, known to nobody ----

        private static string[] RobotPackages() =>
            Directory.Exists("Packages")
                ? Directory.EnumerateDirectories("Packages", "com.erupt." + "robot.*").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

        [Test]
        public void RobotPackages_HoldAssetsOnly()
        {
            // A robot package is a robot's description, prefabs and profile. Code that a robot
            // needs belongs in core, where every robot gets it.
            string[] code = { ".cs", ".asmdef", ".asmref", ".dll" };
            var hits = RobotPackages()
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => code.Contains(Path.GetExtension(path)))
                .ToList();
            Assert.IsEmpty(hits, "Robot packages contain code:\n" + string.Join("\n", hits));
        }

        [Test]
        public void CoreAndPlugins_NeverReferenceARobotPackage()
        {
            // Core and the plugins work with whatever robot is in the scene. Neither their
            // code nor their prefabs, samples and assets may name a robot package or use one
            // of its assets. Assembled at runtime so this file does not match its own check.
            string name = "com.erupt." + "robot.";
            string[] roots = Directory.EnumerateDirectories("Packages", "com.erupt.*")
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.StartsWith("Packages/" + name, StringComparison.Ordinal))
                .ToArray();
            Assert.IsNotEmpty(roots);

            string[] sources = { ".cs", ".asmdef", ".json", ".txt" };
            var named = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => sources.Contains(Path.GetExtension(path)))
                .Where(path => File.ReadAllText(path).Contains(name))
                .ToList();
            Assert.IsEmpty(named, "Core or plugin sources name a robot package:\n" + string.Join("\n", named));

            var robotGuids = RobotPackages()
                .SelectMany(root => Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories))
                .SelectMany(File.ReadLines)
                .Where(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Select(line => line.Substring("guid: ".Length).Trim())
                .ToHashSet();
            if (robotGuids.Count == 0) return;   // a checkout without robot packages

            string[] serialized = { ".unity", ".prefab", ".asset", ".mat" };
            var guid = new Regex("guid: ([0-9a-f]{32})");
            var used = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => serialized.Contains(Path.GetExtension(path)))
                .Where(path => guid.Matches(File.ReadAllText(path)).Cast<Match>().Any(m => robotGuids.Contains(m.Groups[1].Value)))
                .ToList();
            Assert.IsEmpty(used, "Core or plugin assets use an asset of a robot package:\n" + string.Join("\n", used));
        }

        [Test]
        public void RobotPackages_DependOnCoreAlone_AtCoresVersion()
        {
            string Version(string manifest) => Regex.Match(File.ReadAllText(manifest), "\"version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string core = Version(Path.Combine(CorePackage, "package.json"));
            foreach (string root in RobotPackages())
            {
                string manifest = File.ReadAllText(Path.Combine(root, "package.json"));
                Assert.AreEqual(core, Version(Path.Combine(root, "package.json")), $"{root}: version");
                string dependencies = Regex.Match(manifest, "\"dependencies\"\\s*:\\s*\\{([^}]*)\\}").Groups[1].Value;
                var names = Regex.Matches(dependencies, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>().ToList();
                Assert.AreEqual(1, names.Count, $"{root}: a robot package depends on com.erupt.core and nothing else.");
                Assert.AreEqual("com.erupt.core", names[0].Groups[1].Value, root);
                Assert.AreEqual(core, names[0].Groups[2].Value, $"{root}: the version of com.erupt.core it asks for");
                foreach (string file in new[] { "README.md", "CHANGELOG.md", "LICENSE.md", "THIRD-PARTY-NOTICES.md" })
                    Assert.IsTrue(File.Exists(Path.Combine(root, file)), $"{root}/{file}");
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "Licenses")) && Directory.EnumerateFiles(Path.Combine(root, "Licenses"), "*.txt", SearchOption.AllDirectories).Any(),
                    $"{root}: the licence texts of the description packages it redistributes.");
            }
        }

        [Test]
        public void WhatTheEruptPackagesAskTheProjectToInstall_IsWhatThisProjectHas()
        {
            // A git package cannot declare git dependencies, so an ERUPT package lists what it
            // needs under "erupt.requires" and ERUPT offers to add it (core: the two forks; the
            // AR module: AprilTag and the MR Utility Kit). The sources there and in this
            // project's manifest have to be the same commits and versions.
            string project = File.ReadAllText(Path.Combine("Packages", "manifest.json"));
            var asked = new System.Collections.Generic.List<string>();
            foreach (string package in Directory.EnumerateDirectories("Packages", "com.erupt.*").Select(p => p.Replace('\\', '/')))
            {
                string manifest = File.ReadAllText(Path.Combine(package, "package.json"));
                foreach (Match requirement in Regex.Matches(manifest, "\"name\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"source\"\\s*:\\s*\"([^\"]+)\""))
                {
                    string name = requirement.Groups[1].Value, source = requirement.Groups[2].Value;
                    asked.Add($"{package}: {name}");
                    Match inProject = Regex.Match(project, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]+)\"");
                    Assert.IsTrue(inProject.Success, $"The project manifest does not have {name}, which {package} asks for.");
                    Assert.AreEqual(inProject.Groups[1].Value, source, $"{name}: {package} asks for another source than the project uses.");
                }
            }
            Assert.IsTrue(asked.Any(a => a.StartsWith(CorePackage + ":", StringComparison.Ordinal)), "com.erupt.core lists no requirements under erupt.requires.");
        }

        [Test]
        public void NoScriptUnderAssets_FallsIntoAssemblyCSharp()
        {
            // Vendor folders ship their own scripts; the project's own code must all be
            // inside an asmdef so it cannot silently grow a new flat assembly.
            string[] vendor = { "Assets/LudicWorlds", "Assets/TextMesh Pro", "Assets/Samples" };
            var strays = Directory.EnumerateFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.StartsWith("Assets/Erupt.App/"))
                .Where(p => !vendor.Any(v => p.StartsWith(v + "/")))
                .ToList();

            Assert.IsEmpty(strays, "Scripts outside Erupt.App would compile into Assembly-CSharp:\n" + string.Join("\n", strays));
        }
    }
}
