using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Ui
{
    /// <summary>
    /// Tier 3 "Settings" tab: how the app behaves for this user (the strength of the
    /// collision haptics, and whatever else is a preference rather than a part of the
    /// task). It stands last in the strip.
    /// </summary>
    /// <remarks>
    /// The tab is made of sections, and whoever owns a setting contributes its section
    /// (<see cref="AddSection"/>): the component the setting belongs to, when it is in the
    /// scene. Nothing is wired per scene, and a scene without that component has no dead
    /// controls. The tab exists only while it has a section.
    ///
    /// Guidelines Part 2: tier 3 is configuration, and no core task needs it. Everything
    /// here has a default that works.
    /// </remarks>
    public sealed class SettingsTab
    {
        public const string TabId = "settings";
        public const string Title = "Settings";

        /// <summary>Where the tab stands in the strip (<see cref="PanelTab.Order"/>): after every plugin's.</summary>
        public const int TabOrder = 1000;

        private sealed class Section
        {
            public string Id;
            public Action<Transform> Build;
        }

        private readonly TabbedPanelView panel;
        private readonly List<Section> sections = new();
        private string only;

        /// <summary>The tab; null while there is no section.</summary>
        public PanelTab Tab { get; private set; }

        /// <summary>The ids of the sections, in the order they were added.</summary>
        public IEnumerable<string> Sections
        {
            get { foreach (Section section in sections) yield return section.Id; }
        }

        public int SectionCount => sections.Count;

        /// <summary>Interactive elements on the tab (Guidelines Part 8 counts them).</summary>
        public int InteractiveElementCount => Tab != null ? UiBuilder.CountInteractive(Tab.Content) : 0;

        /// <summary>The section that has the tab to itself (<see cref="ShowOnly"/>), or null when all are shown.</summary>
        public string Showing => only;

        internal SettingsTab(TabbedPanelView panel)
        {
            this.panel = panel;
        }

        /// <summary>
        /// Add a section. <paramref name="build"/> fills it: it gets a column to put its
        /// controls in (<see cref="UiBuilder"/>), and is called again whenever the tab is
        /// rebuilt. A section with the same id is replaced.
        /// </summary>
        public void AddSection(string id, Action<Transform> build)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            if (build == null) throw new ArgumentNullException(nameof(build));

            Section existing = sections.Find(s => s.Id == id);
            if (existing != null) existing.Build = build;
            else sections.Add(new Section { Id = id, Build = build });

            if (Tab == null)
            {
                if (panel == null) return;
                Tab = panel.AddTab(TabId, Title);
                Tab.Order = TabOrder;
            }
            Tab.Rebuild(BuildAll);
        }

        /// <summary>
        /// Give one section the whole tab while it shows something that needs the room (a
        /// keypad), and the tab back to all of them with null. The other sections are not
        /// built meanwhile; they come back as they were. The section that asked ends it: by
        /// the control that started it, and when the user leaves the tab.
        /// </summary>
        public void ShowOnly(string id)
        {
            if (id != null && !sections.Exists(s => s.Id == id)) id = null;
            if (only == id) return;
            only = id;
            if (Tab != null) Tab.Rebuild(BuildAll);
        }

        /// <summary>Take a section away; the tab goes with the last one. False when there is no such section.</summary>
        public bool RemoveSection(string id)
        {
            if (sections.RemoveAll(s => s.Id == id) == 0) return false;
            if (only == id) only = null;
            if (Tab == null) return true;
            // On the way out with the scene there is nothing to rebuild.
            if (panel == null || !panel.gameObject.scene.isLoaded) return true;
            if (sections.Count == 0)
            {
                panel.RemoveTab(TabId);
                Tab = null;
            }
            else Tab.Rebuild(BuildAll);
            return true;
        }

        private void BuildAll(RectTransform content)
        {
            if (content == null) return;
            var column = UiBuilder.CreateColumn("Settings", content, 8f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            foreach (Section section in sections)
            {
                if (only != null && section.Id != only) continue;
                try { section.Build(column.transform); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
