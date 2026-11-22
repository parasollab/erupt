using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// Assembles tiers 1, 2 and 3 and is the <see cref="IUiHost"/> plugins contribute to.
    /// </summary>
    /// <remarks>
    /// Since plugin-refactor Phase 3 the tier UI is the only UI; the legacy wrist menu and
    /// its toggle are gone. <c>useTierUi</c> remains as a debugging switch that hides the
    /// tiers.
    /// </remarks>
    public class TierUiRig : MonoBehaviour, IUiHost
    {
        [Header("Switch")]
        [Tooltip("Off hides tiers 1–3 (debugging only; the legacy wrist menu is gone since Phase 3).")]
        [SerializeField] private bool useTierUi = true;

        [Header("Placement")]
        [SerializeField] private Transform tierOneAnchor;
        [SerializeField] private Transform tierThreeAnchor;

        [Header("Summoning")]
        [Tooltip("Tier 3 toggles on the router's Activate intent (the controller Menu button, the hand menu gesture, the desktop panel key) and on tier 1's Menu button. Found in the scene if empty.")]
        [SerializeField] private InteractionRouter router;

        [Tooltip("Place tier 3 in front of the user's head each time it opens. Off leaves it wherever its anchor is.")]
        [SerializeField] private bool placeInFrontOfUser = true;

        [Tooltip("How far in front of the head tier 3 opens, in metres.")]
        [SerializeField] private float summonDistance = 1.1f;

        [Tooltip("Vertical offset of the panel centre from eye level, in metres. Slightly below is most comfortable.")]
        [SerializeField] private float summonHeightOffset = -0.1f;

        [Header("Tier 2")]
        [Tooltip("List the verbs nothing implements yet as disabled buttons. Off (the default) leaves them out of the menus; they stay in the verb table.")]
        [SerializeField] private bool showUnimplementedVerbs;

        public TierOneBar TierOne { get; private set; }
        public ContextualMenuView TierTwo { get; private set; }
        public TabbedPanelView TierThree { get; private set; }

        /// <summary>
        /// The readout that shows why an interaction was refused, at the place it happened.
        /// Not a tier: it has no controls and is on screen only for a moment.
        /// </summary>
        public RefusalToast Refusals { get; private set; }

        /// <summary>
        /// The tier 3 Settings tab, which components contribute their sections to. Null before
        /// the rig has woken; the tab itself exists only while it has a section.
        /// </summary>
        public SettingsTab Settings { get; private set; }

        public UiTierRegistry Registry => TierOne != null ? TierOne.Registry : null;
        public UndoStack UndoStack => TierOne != null ? TierOne.UndoStack : pendingUndo;

        public bool UseTierUi => useTierUi;

        /// <summary>
        /// Whether the tier 2 menus list the verbs that cannot be pressed (the ones the
        /// guidelines name and nothing implements yet, or that nothing has bound) as disabled
        /// buttons. Off by default: a menu shows what can be done.
        /// </summary>
        public bool ShowUnimplementedVerbs
        {
            get => showUnimplementedVerbs;
            set
            {
                showUnimplementedVerbs = value;
                if (TierTwo != null) TierTwo.Model.ListsUnavailableVerbs = value;
            }
        }

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
            TierTwo.Model.ListsUnavailableVerbs = showUnimplementedVerbs;
            TierThree = CreateChild<TabbedPanelView>("Tier 3", tierThreeAnchor);
            Settings = new SettingsTab(TierThree);

            // Tier 1's Menu button does what the controller's Menu button does.
            if (TierOne.Menu != null) TierOne.Menu.Pressed += ToggleTierThree;

            // Tier 3 registers so the one-open-at-a-time invariant runs through the same
            // registry that enforces the tier 1 cap.
            Registry?.RegisterPanel(TierThree);

            Refusals = CreateChild<RefusalToast>("Refusal Toast", null);

            Apply();
        }

        private void OnEnable()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (router != null) router.Activate += OnActivate;
            if (Refusals != null && router != null) Refusals.SetRouter(router);
        }

        private void OnDisable()
        {
            if (router != null) router.Activate -= OnActivate;
        }

        private void OnActivate(InteractionIntent intent) => ToggleTierThree();

        /// <summary>Open tier 3 on its current (or first) tab, or close it if open.</summary>
        public void ToggleTierThree()
        {
            if (TierThree == null || !useTierUi) return;
            if (TierThree.IsOpen) { Registry?.Close(TierThree); return; }
            string tab = TierThree.ActiveTab?.Id ?? (TierThree.Tabs.Count > 0 ? TierThree.Tabs[0].Id : null);
            if (tab == null) return;
            SummonTab(tab);
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

            foreach (var widget in widgets)
                if (widget.GameObject != null) widget.GameObject.SetActive(useTierUi);
        }

        /// <summary>Open tier 3 on a given tab, closing whatever else was open.</summary>
        public void SummonTab(string tabId)
        {
            if (TierThree == null) return;

            bool wasOpen = TierThree.IsOpen;
            TierThree.SelectTab(tabId);
            Registry?.Open(TierThree);

            // Only on the closed→open edge: switching tabs on an open panel must not yank
            // it out from under the user's pointer.
            if (!wasOpen) PlaceTierThree();
        }

        /// <summary>
        /// Move tier 3 in front of the user's head. Guidelines Part 6: tier 3 panels are
        /// grabbable world-space panels, so this only sets where the panel opens; the
        /// user can carry it elsewhere afterwards.
        /// </summary>
        public void PlaceTierThree()
        {
            if (!placeInFrontOfUser || TierThree == null) return;

            Pose? elsewhere = summonPlacement?.Invoke();
            if (elsewhere.HasValue)
            {
                TierThree.transform.SetPositionAndRotation(elsewhere.Value.position, elsewhere.Value.rotation);
                return;
            }

            Camera cam = Camera.main;
            if (cam == null) return;   // no head to place in front of; the anchor stands

            Pose pose = PlacementFor(cam.transform.position, cam.transform.forward, summonDistance, summonHeightOffset);
            TierThree.transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        private Func<Pose?> summonPlacement;

        /// <summary>
        /// Let a backend say where tier 3 opens (the desktop view puts it square in front of
        /// its camera, whatever the camera's pitch). The function is asked each time the panel
        /// opens; when it returns nothing, or after it is set back to null, the panel opens in
        /// front of the head, level with the horizon.
        /// </summary>
        public void UseSummonPlacement(Func<Pose?> placement) => summonPlacement = placement;

        /// <summary>
        /// Where a summoned panel goes for a head at <paramref name="headPosition"/> looking
        /// along <paramref name="headForward"/>: level with the horizon, so it does not tilt
        /// when the user is looking down at the table, and facing the user.
        /// </summary>
        public static Pose PlacementFor(Vector3 headPosition, Vector3 headForward, float distance, float heightOffset)
        {
            Vector3 flat = FlatForward(headForward);

            // A world-space canvas reads correctly when its +Z points away from the viewer,
            // which is the convention ContextualMenuView already uses.
            return new Pose(PlacementPositionFor(headPosition, headForward, distance, heightOffset),
                            Quaternion.LookRotation(flat, Vector3.up));
        }

        /// <summary>The panel centre: <paramref name="distance"/> ahead along the level gaze, offset vertically.</summary>
        public static Vector3 PlacementPositionFor(Vector3 headPosition, Vector3 headForward, float distance, float heightOffset) =>
            headPosition + FlatForward(headForward) * distance + Vector3.up * heightOffset;

        /// <summary>The gaze with its pitch removed; world forward when looking straight up or down.</summary>
        public static Vector3 FlatForward(Vector3 headForward)
        {
            Vector3 flat = Vector3.ProjectOnPlane(headForward, Vector3.up);
            return flat.sqrMagnitude < 1e-6f ? Vector3.forward : flat.normalized;
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

        public bool RemoveTab(string id) => TierThree != null && TierThree.RemoveTab(id);

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
