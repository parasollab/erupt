using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Erupt.Ui
{
    /// <summary>One tab in the tier 3 panel.</summary>
    public class PanelTab
    {
        public string Id { get; }
        public string Label { get; }
        public RectTransform Content { get; internal set; }
        internal Button Header;

        public PanelTab(string id, string label)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label;
        }
    }

    /// <summary>
    /// Tier 3: configuration and libraries, summoned and closed by default.
    /// </summary>
    /// <remarks>
    /// Deliberately a single panel with tabs rather than several separate panels.
    /// Guidelines Part 6 turns tier 3 into one visionOS window with tabs parkable on a
    /// wall; building it as N panels on Quest would force a restructure at port time.
    /// It also makes Part 2's one-open-at-a-time invariant structural — there is only
    /// one panel, so two cannot be open.
    ///
    /// Part 2 also requires that a first-time user can complete every core task without
    /// ever opening tier 3. Nothing here enforces that; it is a property of what gets
    /// put in the tabs, and belongs in the design review.
    /// </remarks>
    public class TabbedPanelView : MonoBehaviour, ISummonedPanel
    {
        [SerializeField] private Vector2 panelSize = new(900f, 640f);
        [SerializeField] private Vector2 tabSize = new(220f, 64f);

        private readonly List<PanelTab> tabs = new();
        private RectTransform tabStrip;
        private RectTransform contentArea;

        public UiTier Tier => UiTier.Summoned;
        public string Id => "tier3";

        public Canvas Canvas { get; private set; }
        public bool IsOpen { get; private set; }
        public PanelTab ActiveTab { get; private set; }
        public IReadOnlyList<PanelTab> Tabs => tabs;

        public event Action<PanelTab> TabChanged;

        private void Awake() => Build();

        private void Build()
        {
            if (Canvas != null) return;

            Canvas = UiBuilder.CreateWorldCanvas("Tier3", transform, panelSize);

            var backdrop = UiBuilder.CreatePanel("Backdrop", Canvas.transform, new Color(0.10f, 0.11f, 0.13f, 0.96f));
            UiBuilder.Stretch(backdrop);

            var column = UiBuilder.CreateColumn("Layout", backdrop, 12f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());

            tabStrip = UiBuilder.CreateRow("Tabs", column.transform).GetComponent<RectTransform>();
            contentArea = UiBuilder.CreatePanel("Content", column.transform, Color.clear);

            var element = contentArea.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 1f;

            // Closed by default. Part 2: "none open by default".
            Canvas.gameObject.SetActive(false);
        }

        /// <summary>
        /// Add a tab and return its content area for the caller to populate.
        /// </summary>
        public PanelTab AddTab(string id, string label)
        {
            Build();

            if (tabs.Exists(t => t.Id == id))
                throw new UiTierViolationException($"Duplicate tier 3 tab id '{id}'.");

            var tab = new PanelTab(id, label);

            var content = UiBuilder.CreatePanel($"Content_{id}", contentArea, Color.clear);
            UiBuilder.Stretch(content);
            content.gameObject.SetActive(false);
            tab.Content = content;

            tab.Header = UiBuilder.CreateButton($"Tab_{id}", tabStrip, label, tabSize, () => SelectTab(id));

            tabs.Add(tab);

            if (ActiveTab == null) SelectTab(id);
            else RefreshTabHeaders();

            return tab;
        }

        public void SelectTab(string id)
        {
            PanelTab target = tabs.Find(t => t.Id == id);
            if (target == null || ReferenceEquals(target, ActiveTab)) return;

            foreach (var tab in tabs)
                if (tab.Content != null) tab.Content.gameObject.SetActive(ReferenceEquals(tab, target));

            ActiveTab = target;
            RefreshTabHeaders();
            TabChanged?.Invoke(ActiveTab);
        }

        // The active tab's header is not pressable, so it reads as selected and does not
        // count against the visible interactive-element budget.
        private void RefreshTabHeaders()
        {
            foreach (var tab in tabs)
                if (tab.Header != null)
                    UiBuilder.SetInteractable(tab.Header, !ReferenceEquals(tab, ActiveTab));
        }

        public void Open()
        {
            Build();
            IsOpen = true;
            Canvas.gameObject.SetActive(true);
        }

        public void Close()
        {
            IsOpen = false;
            if (Canvas != null) Canvas.gameObject.SetActive(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }
    }
}
