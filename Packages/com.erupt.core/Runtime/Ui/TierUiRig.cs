using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// Assembles tiers 1, 2 and 3 and switches between the new UI and the legacy menu.
    /// </summary>
    /// <remarks>
    /// The legacy wrist menu and the tier UI are both present, one active at a time, so
    /// the new UI can be compared against the old and fallen back from instantly. The
    /// legacy object is only disabled, never removed — it lives in a prefab shared with
    /// 15 other scenes, and disabling a prefab instance's child is a scene-local override.
    ///
    /// Once the tier UI is trusted, this component and the toggle go away.
    /// </remarks>
    public class TierUiRig : MonoBehaviour, IUiHost
    {
        [Header("Switch")]
        [Tooltip("On: tier 1/2/3. Off: the original wrist menu.")]
        [SerializeField] private bool useTierUi = true;

        [Tooltip("The legacy wrist menu object, disabled while the tier UI is active.")]
        [SerializeField] private GameObject legacyMenu;

        [Header("Placement")]
        [SerializeField] private Transform tierOneAnchor;
        [SerializeField] private Transform tierThreeAnchor;

        public TierOneBar TierOne { get; private set; }
        public ContextualMenuView TierTwo { get; private set; }
        public TabbedPanelView TierThree { get; private set; }

        public UiTierRegistry Registry => TierOne != null ? TierOne.Registry : null;
        public UndoStack UndoStack => TierOne != null ? TierOne.UndoStack : pendingUndo;

        public bool UseTierUi => useTierUi;

        /// <summary>The live vocabulary: Guidelines rows plus plugin verbs.</summary>
        public VerbRegistry Verbs { get; } = VerbRegistry.FromTable();
        public IReadOnlyList<IWorldWidget> Widgets => widgets;

        private readonly List<IWorldWidget> widgets = new();
        private UndoStack pendingUndo;

        /// <summary>
        /// Give the rig the app's command history. Effective when called before Awake
        /// (the plugin host does so on an inactive rig, or the scene wiring assigns it);
        /// after that the bar keeps the stack it built with.
        /// </summary>
        public void UseUndoStack(UndoStack stack)
        {
            pendingUndo = stack;
            if (TierOne != null) TierOne.UseUndoStack(stack);
        }

        private void Awake()
        {
            TierOne = CreateChild<TierOneBar>("Tier 1", tierOneAnchor, bar => bar.UseUndoStack(pendingUndo));
            TierTwo = CreateChild<ContextualMenuView>("Tier 2", null);
            TierTwo.Model.SetRegistry(Verbs);
            TierThree = CreateChild<TabbedPanelView>("Tier 3", tierThreeAnchor);

            // Tier 3 registers so the one-open-at-a-time invariant runs through the same
            // registry that enforces the tier 1 cap.
            Registry?.RegisterPanel(TierThree);

            Apply();
        }

        private T CreateChild<T>(string name, Transform anchor, Action<T> beforeAwake = null) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(anchor != null ? anchor : transform, false);
            if (beforeAwake == null) return go.AddComponent<T>();

            // Inactive while configuring, so Awake sees the configuration.
            go.SetActive(false);
            var component = go.AddComponent<T>();
            beforeAwake(component);
            go.SetActive(true);
            return component;
        }

        /// <summary>Switch between the tier UI and the legacy menu.</summary>
        public void SetUseTierUi(bool value)
        {
            useTierUi = value;
            Apply();
        }

        public void Toggle() => SetUseTierUi(!useTierUi);

        private void Apply()
        {
            if (TierOne != null) TierOne.gameObject.SetActive(useTierUi);
            if (TierTwo != null) TierTwo.gameObject.SetActive(useTierUi);

            // Tier 3 stays closed either way; Part 2 says none open by default.
            if (TierThree != null)
            {
                TierThree.gameObject.SetActive(useTierUi);
                TierThree.Close();
            }

            if (legacyMenu != null) legacyMenu.SetActive(!useTierUi);
            foreach (var widget in widgets)
                if (widget.GameObject != null) widget.GameObject.SetActive(useTierUi);
        }

        /// <summary>Open tier 3 on a given tab, closing whatever else was open.</summary>
        public void SummonTab(string tabId)
        {
            if (TierThree == null) return;

            TierThree.SelectTab(tabId);
            Registry?.Open(TierThree);
        }

        // --- IUiHost ---------------------------------------------------------

        public Verb RegisterVerb(SelectionKind kind, string verbId, string label, Action<ISelectable> handler, string pluginId)
        {
            Verb verb = Verbs.Register(kind, verbId, label, pluginId);
            if (handler != null) TierTwo?.Bind(verbId, handler);
            return verb;
        }

        public void BindVerb(string verbId, Action<ISelectable> handler)
        {
            if (TierTwo == null) throw new InvalidOperationException("Tier 2 does not exist yet; bind after the rig has woken.");
            TierTwo.Bind(verbId, handler);
        }

        public PanelTab AddTab(string id, string label, Action<RectTransform> build)
        {
            if (TierThree == null) throw new InvalidOperationException("Tier 3 does not exist yet; add tabs after the rig has woken.");
            PanelTab tab = TierThree.AddTab(id, label);
            build?.Invoke(tab.Content);
            return tab;
        }

        public void RegisterWidget(IWorldWidget widget)
        {
            if (widget == null) throw new ArgumentNullException(nameof(widget));
            if (widgets.Exists(w => w.Id == widget.Id))
                throw new UiTierViolationException($"Duplicate world widget id '{widget.Id}'.");
            widgets.Add(widget);
            if (widget.GameObject != null) widget.GameObject.SetActive(useTierUi);
        }

        public void UnregisterWidget(IWorldWidget widget) => widgets.Remove(widget);
    }
}
