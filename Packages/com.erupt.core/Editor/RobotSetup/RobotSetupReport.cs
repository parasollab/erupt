using System.Collections.Generic;
using System.Text;

namespace Erupt.Editor
{
    public enum RobotSetupLevel
    {
        /// <summary>Something that was found or decided.</summary>
        Found,
        /// <summary>Something the user should look at; the robot is usable.</summary>
        Warning,
        /// <summary>Something that is wrong; the robot will not work as it is.</summary>
        Problem,
    }

    public readonly struct RobotSetupEntry
    {
        public readonly RobotSetupLevel Level;
        public readonly string Text;

        public RobotSetupEntry(RobotSetupLevel level, string text)
        {
            Level = level;
            Text = text;
        }
    }

    /// <summary>
    /// What Robot Setup found, what it guessed and what is wrong, in the order it happened.
    /// Written next to the robot as <c>&lt;Name&gt;.setup.md</c>.
    /// </summary>
    public sealed class RobotSetupReport
    {
        public string Title = "Robot Setup";
        public readonly List<RobotSetupEntry> Entries = new List<RobotSetupEntry>();
        /// <summary>Asset paths written by this run.</summary>
        public readonly List<string> Written = new List<string>();

        public void Found(string text) => Entries.Add(new RobotSetupEntry(RobotSetupLevel.Found, text));
        public void Warn(string text) => Entries.Add(new RobotSetupEntry(RobotSetupLevel.Warning, text));
        public void Problem(string text) => Entries.Add(new RobotSetupEntry(RobotSetupLevel.Problem, text));

        public bool HasProblems => Count(RobotSetupLevel.Problem) > 0;

        public int Count(RobotSetupLevel level)
        {
            int count = 0;
            foreach (RobotSetupEntry entry in Entries)
                if (entry.Level == level) count++;
            return count;
        }

        public List<string> Texts(RobotSetupLevel level)
        {
            var texts = new List<string>();
            foreach (RobotSetupEntry entry in Entries)
                if (entry.Level == level) texts.Add(entry.Text);
            return texts;
        }

        /// <summary>One line: how many problems and warnings.</summary>
        public string Summary
        {
            get
            {
                int problems = Count(RobotSetupLevel.Problem), warnings = Count(RobotSetupLevel.Warning);
                if (problems == 0 && warnings == 0) return "No problems found.";
                return $"{problems} problem{(problems == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}.";
            }
        }

        public string ToMarkdown()
        {
            var text = new StringBuilder();
            text.Append("# ").Append(Title).Append("\n\n");
            text.Append("Written by `ERUPT > Robots > Robot Setup…`. Running it again on the same output folder rewrites this file.\n\n");
            text.Append(Summary).Append("\n");

            Section(text, "Problems", RobotSetupLevel.Problem, "These have to be fixed before the robot works.");
            Section(text, "Warnings", RobotSetupLevel.Warning, "The robot is usable; check that these are what you want.");
            Section(text, "Found", RobotSetupLevel.Found, null);

            if (Written.Count > 0)
            {
                text.Append("\n## Written\n\n");
                foreach (string path in Written) text.Append("- `").Append(path).Append("`\n");
            }
            return text.ToString();
        }

        private void Section(StringBuilder text, string heading, RobotSetupLevel level, string lead)
        {
            List<string> lines = Texts(level);
            if (lines.Count == 0) return;
            text.Append("\n## ").Append(heading).Append("\n\n");
            if (lead != null) text.Append(lead).Append("\n\n");
            foreach (string line in lines) text.Append("- ").Append(line).Append("\n");
        }
    }
}
