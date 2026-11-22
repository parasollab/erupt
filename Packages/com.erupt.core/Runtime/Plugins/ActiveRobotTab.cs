using System;
using UnityEngine;
using Erupt.Robot;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// A tier 3 tab that shows the active robot's session. There is one tab per plugin however
    /// many robots the scene has: its content is rebuilt when the active robot changes, and its
    /// title names that robot while there is more than one ("MoveIt · left"). With one robot it
    /// reads as the plugin's name alone.
    /// </summary>
    public sealed class ActiveRobotTab : IDisposable
    {
        private readonly IRobotRegistry robots;
        private readonly string title;
        private readonly Action<RectTransform> build;

        public PanelTab Tab { get; }

        /// <param name="build">Fills the tab for the robot that is active when it is called; called again on every rebuild.</param>
        public ActiveRobotTab(IUiHost ui, IRobotRegistry robots, string id, string title, Action<RectTransform> build)
        {
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            this.robots = robots;
            this.title = title;
            this.build = build;
            Tab = ui.AddTab(id, TitleFor(title, robots), build);
            if (robots == null) return;
            robots.ActiveChanged += OnActiveChanged;
            robots.Added += OnCountChanged;
            robots.Removed += OnCountChanged;
        }

        /// <summary>The title of something that belongs to the active robot: the robot's id is appended while the scene has several robots.</summary>
        public static string TitleFor(string title, IRobotRegistry robots) =>
            TitleFor(title, robots, robots?.Active);

        /// <summary>As <see cref="TitleFor(string, IRobotRegistry)"/>, for a given robot.</summary>
        public static string TitleFor(string title, IRobotRegistry robots, RobotInstance robot) =>
            robots != null && robots.All.Count > 1 && robot != null ? $"{title} · {robot.Id}" : title;

        /// <summary>Rebuild the content for the robot that is active now.</summary>
        public void Rebuild()
        {
            Tab?.SetLabel(TitleFor(title, robots));
            Tab?.Rebuild(build);
        }

        public void Dispose()
        {
            if (robots == null) return;
            robots.ActiveChanged -= OnActiveChanged;
            robots.Added -= OnCountChanged;
            robots.Removed -= OnCountChanged;
        }

        private void OnActiveChanged(RobotInstance _) => Rebuild();

        // A second robot arriving (or the second-last leaving) changes whether the title names one.
        private void OnCountChanged(RobotInstance _) => Tab?.SetLabel(TitleFor(title, robots));
    }
}
