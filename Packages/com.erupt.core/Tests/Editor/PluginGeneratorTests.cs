using System.IO;
using System.Linq;
using NUnit.Framework;
using Erupt.Editor;

namespace Erupt.Editor.Tests
{
    /// <summary>
    /// The generator produces the file set a real plugin has, with every token resolved, and
    /// the shipped MoveIt / MTC packages match the Planning template's shape.
    /// </summary>
    public class PluginGeneratorTests
    {
        private string temp;

        [SetUp]
        public void SetUp()
        {
            temp = Path.Combine(Path.GetTempPath(), "erupt-generator-" + Path.GetRandomFileName());
            Directory.CreateDirectory(temp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        private static readonly string[] CommonFiles =
        {
            "package.json", "README.md", "MESSAGES.md",
            "Runtime/{0}.asmdef", "Runtime/Messages/{0}.Messages.asmdef", "Runtime/Messages/README.md",
            "Tests/{0}.Tests.asmdef",
            "Runtime/{0}Plugin.cs", "Tests/{0}SmokeTests.cs"
        };

        [TestCase(PluginFamily.Blank)]
        [TestCase(PluginFamily.Planning)]
        [TestCase(PluginFamily.Demonstration)]
        public void Generate_WritesTheExpectedFileSet_WithNoTokensLeft(PluginFamily family)
        {
            var spec = PluginSpec.Default("Demo", family);
            string root = Path.Combine(temp, spec.PackageId);

            var written = PluginGenerator.Generate(spec, PluginTemplate.For(family), root);

            CollectionAssert.AreEquivalent(CommonFiles.Select(f => string.Format(f, "Demo")), written);
            foreach (var rel in written)
            {
                string text = File.ReadAllText(Path.Combine(root, rel));
                StringAssert.DoesNotContain("{{", text, rel);
                StringAssert.DoesNotContain("__PluginName__", rel);
            }
            StringAssert.Contains($": {PluginTemplate.For(family).BaseClass}", File.ReadAllText(Path.Combine(root, "Runtime/DemoPlugin.cs")));
            StringAssert.Contains("\"com.erupt.plugin.demo\"", File.ReadAllText(Path.Combine(root, "package.json")));
            StringAssert.Contains("\"Erupt.Plugins.Demo\"", File.ReadAllText(Path.Combine(root, "Runtime/Demo.asmdef")));
            StringAssert.Contains("namespace Erupt.Plugins.Demo", File.ReadAllText(Path.Combine(root, "Runtime/DemoPlugin.cs")));
            StringAssert.Contains("=> \"demo\"", File.ReadAllText(Path.Combine(root, "Runtime/DemoPlugin.cs")));
        }

        [Test]
        public void Generate_RefusesANonEmptyTarget_AndBadNames()
        {
            var spec = PluginSpec.Default("Demo", PluginFamily.Blank);
            string root = Path.Combine(temp, spec.PackageId);
            PluginGenerator.Generate(spec, PluginTemplate.Blank, root);
            Assert.Throws<IOException>(() => PluginGenerator.Generate(spec, PluginTemplate.Blank, root));

            Assert.IsNotNull(PluginSpec.Default("demo", PluginFamily.Blank).Validate(), "lower-case start");
            Assert.IsNotNull(PluginSpec.Default("My Plugin", PluginFamily.Blank).Validate(), "space");
            Assert.IsNull(PluginSpec.Default("GraspNet2", PluginFamily.Blank).Validate());
        }

        [Test]
        public void AssemblyConflicts_FindsAnExistingPackageWithTheSameAssemblies()
        {
            var spec = PluginSpec.Default("Demo", PluginFamily.Blank);
            PluginGenerator.Generate(spec, PluginTemplate.Blank, Path.Combine(temp, "com.erupt.plugin.demo"));

            var other = PluginSpec.Default("Demo", PluginFamily.Planning);
            other.PackageId = "com.erupt.plugin.demo2";     // different package, same plugin name
            var conflicts = PluginGenerator.AssemblyConflicts(other, temp);

            Assert.AreEqual(3, conflicts.Count, "runtime, messages and tests assemblies all clash");
            StringAssert.Contains("Erupt.Plugins.Demo", conflicts[0]);
            Assert.IsEmpty(PluginGenerator.AssemblyConflicts(PluginSpec.Default("Other", PluginFamily.Blank), temp));
            Assert.IsNotEmpty(PluginGenerator.AssemblyConflicts(PluginSpec.Default("MoveIt", PluginFamily.Planning), "Packages"),
                "The shipped MoveIt plugin already defines Erupt.Plugins.MoveIt.");
        }

        [Test]
        public void Plan_MatchesWhatGenerateWrites()
        {
            var spec = PluginSpec.Default("Demo", PluginFamily.Planning);
            var planned = PluginGenerator.Plan(spec, PluginTemplate.Planning);
            var written = PluginGenerator.Generate(spec, PluginTemplate.Planning, Path.Combine(temp, "p"));
            CollectionAssert.AreEquivalent(planned, written);
        }

        // The template is the shape of a real plugin: the shipped packages have every file the
        // Planning template produces (modulo the plugin name and the extra components they grew).
        [TestCase("Packages/com.erupt.plugin.moveit", "MoveIt", "Erupt.Plugins.MoveIt")]
        [TestCase("Packages/com.erupt.plugin.mtc", "Mtc", "Erupt.Plugins.Mtc")]
        public void ShippedPlanningPlugins_HaveTheTemplateShape(string package, string name, string assembly)
        {
            Assert.IsTrue(File.Exists(Path.Combine(package, "package.json")));
            Assert.IsTrue(File.Exists(Path.Combine(package, "README.md")));
            Assert.IsTrue(File.Exists(Path.Combine(package, "MESSAGES.md")));
            Assert.IsTrue(File.Exists(Path.Combine(package, "Runtime", assembly + ".asmdef")), "runtime asmdef");
            Assert.IsTrue(File.Exists(Path.Combine(package, "Runtime", name + "Plugin.cs")), "plugin class");
            Assert.IsTrue(File.Exists(Path.Combine(package, "Runtime/Messages", assembly + ".Messages.asmdef")), "messages asmdef");
            Assert.IsTrue(File.Exists(Path.Combine(package, "Tests", assembly + ".Tests.asmdef")), "tests asmdef");
            Assert.IsTrue(Directory.Exists(Path.Combine(package, "Prefabs")), "prefab folder");
            StringAssert.Contains(": PlanningPlugin", File.ReadAllText(Path.Combine(package, "Runtime", name + "Plugin.cs")));
        }
    }
}
