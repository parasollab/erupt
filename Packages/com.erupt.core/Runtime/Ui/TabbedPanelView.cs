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
        public string Label { get; private set; }
        public RectTransform Content { get; internal set; }
        internal Button Header;
        internal Action Reordered;
        private int order = DefaultOrder;

        /// <summary>The order of a tab whose owner sets none.</summary>
        public const int DefaultOrder = 100;

        /// <summary>
        /// Where the tab stands in the strip: lower numbers are further left, and tabs with
        /// the same number stand in the order they were added. The leftmost tab is the one
        /// the panel shows until the user picks another. Set it so that the strip reads the
        /// same in every session, whatever order the plugins start in.
        /// </summary>
        public int Order
        {
            get => order;
            set
            {
                if (order == value) return;
                order = value;
                Reordered?.Invoke();
            }
        }

        public PanelTab(string id, string label)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label;
        }

        /// <summary>Change the text on the tab's header (a tab that follows the active robot names it).</summary>
        public void SetLabel(string label)
        {
            Label = label;
            if (Header != null) UiBuilder.SetButtonText(Header, label);
        }

        /// <summary>
        /// Throw the tab's content away and build it again. A tab made without a panel (a
        /// headless host in tests) has no content; <paramref name="build"/> then gets null.
        /// </summary>
        public void Rebuild(Action<RectTransform> build)
        {
            // Not while the panel's scene is being unloaded: a rebuild asked for on the way out
            // (the active robot changes as the robots go) would detach the old controls into the
            // dying scene and build new ones in it ("Some objects were not cleaned up...").
            // The same goes for a panel that has already been destroyed (a tab that never had a
            // panel, in a headless host, is a different thing: its content is a plain null).
            if (!ReferenceEquals(Content, null) && (Content == null || !Content.gameObject.scene.isLoaded)) return;
            if (Content != null)
            {
                for (int i = Content.childCount - 1; i >= 0; i--)
                {
                    // Detached first: Destroy only takes effect at the end of the frame, and the
                    // old controls must not be counted or laid out next to the new ones.
                    Transform child = Content.GetChild(i);
                    child.gameObject.SetActive(false);
                    child.SetParent(null, false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            build?.Invoke(Content);
        }
    }

    /// <summary>
    /// Tier 3: configuration and libraries, summoned and closed by default.
    /// </summary>
    /// <remarks>
    /// Deliberately a single panel with tabs rather than several separate panels: it
    /// makes Part 2's one-open-at-a-time invariant structural — there is only one panel,
    /// so two cannot be open.
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
        private readonly List<PanelTab> added = new();
        private bool tabChosen;
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
            // The strip shares the panel's width among however many tabs there are; a title
            // that no longer fits its header is drawn smaller instead of over its neighbours.
            var title = tab.Header.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (title != null)
            {
                title.enableAutoSizing = true;
                title.fontSizeMax = title.fontSize;
                title.fontSizeMin = 14f;
                title.margin = new Vector4(6f, 0f, 6f, 0f);
            }

            tabs.Add(tab);
            added.Add(tab);
            tab.Reordered = Arrange;
            Arrange();

            return tab;
        }

        /// <summary>
        /// Take a tab out of the panel: its header and its content go. When it was the tab
        /// on show, the leftmost of the others is shown; a panel left without tabs closes.
        /// False when there is no such tab.
        /// </summary>
        public bool RemoveTab(string id)
        {
            PanelTab tab = tabs.Find(t => t.Id == id);
            if (tab == null) return false;
            tabs.Remove(tab);
            added.Remove(tab);
            tab.Reordered = null;

            // Detached first (Destroy takes effect at the end of the frame), unless the panel
            // itself is on its way out with its scene: then everything goes together.
            bool leaving = !gameObject.scene.isLoaded;
            if (tab.Header != null) Discard(tab.Header.gameObject, leaving);
            if (tab.Content != null) Discard(tab.Content.gameObject, leaving);
            tab.Header = null;
            tab.Content = null;

            if (ReferenceEquals(tab, ActiveTab))
            {
                ActiveTab = null;
                tabChosen = false;
                if (leaving) return true;
                if (tabs.Count > 0) Activate(tabs[0]);
                else
                {
                    TabChanged?.Invoke(null);
                    Close();
                }
            }
            else if (!leaving) RefreshTabHeaders();
            return true;
        }

        private static void Discard(GameObject go, bool leaving)
        {
            if (!leaving)
            {
                go.SetActive(false);
                go.transform.SetParent(null, false);
            }
            Destroy(go);
        }

        // Tabs stand by their Order, then by when they were added. Until a tab has been
        // picked the leftmost one is shown, so that neither the strip nor the tab a first
        // summon opens on depends on the order the plugins happened to start in.
        private void Arrange()
        {
            tabs.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : added.IndexOf(a).CompareTo(added.IndexOf(b));
            });
            for (int i = 0; i < tabs.Count; i++)
                if (tabs[i].Header != null) tabs[i].Header.transform.SetSiblingIndex(i);

            if (!tabChosen && tabs.Count > 0 && !ReferenceEquals(tabs[0], ActiveTab)) Activate(tabs[0]);
            else RefreshTabHeaders();
        }

        public void SelectTab(string id)
        {
            PanelTab target = tabs.Find(t => t.Id == id);
            if (target == null) return;
            tabChosen = true;
            if (!ReferenceEquals(target, ActiveTab)) Activate(target);
        }

        private void Activate(PanelTab target)
        {
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
