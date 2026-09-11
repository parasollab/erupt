using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Erupt.Editor
{
    /// <summary>The plugin families a template exists for. One base class each.</summary>
    public enum PluginFamily
    {
        Blank,
        Planning,
        Demonstration
    }

    /// <summary>
    /// Describes one plugin family's template: the files under
    /// <c>Editor/Templates/Common</c> plus <c>Editor/Templates/&lt;Family&gt;</c>. Template file
    /// names use <c>__PluginName__</c> tokens and end in <c>.txt</c> (so Unity never compiles
    /// them); contents use <c>{{PluginName}}</c>, <c>{{PackageId}}</c>, <c>{{Namespace}}</c>,
    /// <c>{{Family}}</c>, <c>{{PluginId}}</c>, <c>{{DisplayName}}</c>, <c>{{AssemblyName}}</c>.
    /// </summary>
    public sealed class PluginTemplate
    {
        public const string TemplatesRoot = "Packages/com.erupt.core/Editor/Templates";

        public PluginFamily Family { get; }
        public string Description { get; }
        public string BaseClass { get; }

        private PluginTemplate(PluginFamily family, string description, string baseClass)
        {
            Family = family;
            Description = description;
            BaseClass = baseClass;
        }

        public static readonly PluginTemplate Blank = new(PluginFamily.Blank,
            "A plugin with the bare contract: register, unregister, react to modes.", "EruptPluginBehaviour");
        public static readonly PluginTemplate Planning = new(PluginFamily.Planning,
            "A planner: set goal, plan, preview, execute; contributes the end-effector and trajectory verbs and a settings tab.", "PlanningPlugin");
        public static readonly PluginTemplate Demonstration = new(PluginFamily.Demonstration,
            "Learning from demonstration: records interaction samples in Teach mode and publishes them.", "DemonstrationPlugin");

        public static IReadOnlyList<PluginTemplate> All { get; } = new[] { Blank, Planning, Demonstration };

        public static PluginTemplate For(PluginFamily family) => All.First(t => t.Family == family);

        /// <summary>(template file, output path relative to the package) pairs, Common first.</summary>
        public IEnumerable<(string templatePath, string outputRelativePath)> Files(string templatesRoot = TemplatesRoot)
        {
            foreach (var dir in new[] { Path.Combine(templatesRoot, "Common"), Path.Combine(templatesRoot, Family.ToString()) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    yield return (file, rel.Substring(0, rel.Length - ".txt".Length));
                }
            }
        }
    }
}
