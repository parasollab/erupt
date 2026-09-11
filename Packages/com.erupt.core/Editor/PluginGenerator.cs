using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Erupt.Editor
{
    /// <summary>What the user asked for. Derived names are computed once so every file agrees.</summary>
    public sealed class PluginSpec
    {
        public string PluginName;       // PascalCase, e.g. "Demo"
        public string PackageId;        // e.g. "com.erupt.plugin.demo"
        public string Namespace;        // e.g. "Erupt.Plugins.Demo"
        public PluginFamily Family;
        public string DisplayName;      // e.g. "Demo"

        public string PluginId => PluginName.ToLowerInvariant();
        public string AssemblyName => $"Erupt.Plugins.{PluginName}";

        public static PluginSpec Default(string pluginName, PluginFamily family) => new()
        {
            PluginName = pluginName,
            PackageId = $"com.erupt.plugin.{pluginName.ToLowerInvariant()}",
            Namespace = $"Erupt.Plugins.{pluginName}",
            Family = family,
            DisplayName = pluginName
        };

        public IReadOnlyDictionary<string, string> Tokens => new Dictionary<string, string>
        {
            ["PluginName"] = PluginName,
            ["PackageId"] = PackageId,
            ["Namespace"] = Namespace,
            ["Family"] = Family.ToString(),
            ["PluginId"] = PluginId,
            ["DisplayName"] = DisplayName,
            ["AssemblyName"] = AssemblyName
        };

        /// <summary>Null when valid, otherwise the first problem.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(PluginName)) return "Plugin name is required.";
            if (!Regex.IsMatch(PluginName, "^[A-Z][A-Za-z0-9]*$")) return "Plugin name must be PascalCase letters and digits (e.g. Demo, GraspNet).";
            if (!Regex.IsMatch(PackageId ?? "", "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)+$")) return "Package id must look like com.erupt.plugin.demo.";
            if (!Regex.IsMatch(Namespace ?? "", "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$")) return "Namespace must be a valid C# namespace.";
            return null;
        }
    }

    /// <summary>
    /// Renders a <see cref="PluginTemplate"/> into a package folder. Pure file work, no
    /// Unity API, so the file set is testable without the Editor.
    /// </summary>
    public static class PluginGenerator
    {
        private static readonly Regex Token = new("\\{\\{(\\w+)\\}\\}", RegexOptions.Compiled);
        private static readonly Regex NameToken = new("__(\\w+)__", RegexOptions.Compiled);

        /// <summary>Files the template would produce, relative to the package root.</summary>
        public static IReadOnlyList<string> Plan(PluginSpec spec, PluginTemplate template, string templatesRoot = PluginTemplate.TemplatesRoot) =>
            template.Files(templatesRoot).Select(f => RenderName(f.outputRelativePath, spec)).ToList();

        /// <summary>Write every file. Refuses to overwrite an existing package unless <paramref name="overwrite"/>.</summary>
        public static IReadOnlyList<string> Generate(PluginSpec spec, PluginTemplate template, string packageRoot,
                                                     bool overwrite = false, string templatesRoot = PluginTemplate.TemplatesRoot)
        {
            string problem = spec.Validate();
            if (problem != null) throw new ArgumentException(problem);
            if (Directory.Exists(packageRoot) && Directory.EnumerateFileSystemEntries(packageRoot).Any() && !overwrite)
                throw new IOException($"'{packageRoot}' already exists and is not empty.");

            var written = new List<string>();
            foreach (var (templatePath, outputRel) in template.Files(templatesRoot))
            {
                string rel = RenderName(outputRel, spec);
                string target = Path.Combine(packageRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, Render(File.ReadAllText(templatePath), spec));
                written.Add(rel);
            }
            return written;
        }

        /// <summary>
        /// Assembly names the spec would define that already exist under the given roots
        /// (Packages, Assets). Two packages generated from the same plugin name would define
        /// the same assemblies and neither would compile.
        /// </summary>
        public static IReadOnlyList<string> AssemblyConflicts(PluginSpec spec, params string[] roots)
        {
            var wanted = new[] { spec.AssemblyName, spec.AssemblyName + ".Messages", spec.AssemblyName + ".Tests" };
            var conflicts = new List<string>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var asmdef in Directory.EnumerateFiles(root, "*.asmdef", SearchOption.AllDirectories))
                {
                    var m = Regex.Match(File.ReadAllText(asmdef), @"""name""\s*:\s*""([^""]+)""");
                    if (m.Success && wanted.Contains(m.Groups[1].Value))
                        conflicts.Add($"{m.Groups[1].Value} ({asmdef.Replace('\\', '/')})");
                }
            }
            return conflicts;
        }

        public static string Render(string text, PluginSpec spec) =>
            Token.Replace(text, m => spec.Tokens.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        public static string RenderName(string path, PluginSpec spec) =>
            NameToken.Replace(path, m => spec.Tokens.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
