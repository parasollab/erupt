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
    public class TierUiRig : MonoBehaviour
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
        public UndoStack UndoStack => TierOne != null ? TierOne.UndoStack : null;

        public bool UseTierUi => useTierUi;

        private void Awake()
        {
            TierOne = CreateChild<TierOneBar>("Tier 1", tierOneAnchor);
            TierTwo = CreateChild<ContextualMenuView>("Tier 2", null);
            TierThree = CreateChild<TabbedPanelView>("Tier 3", tierThreeAnchor);

            // Tier 3 registers so the one-open-at-a-time invariant runs through the same
            // registry that enforces the tier 1 cap.
            Registry?.RegisterPanel(TierThree);

            Apply();
        }

        private T CreateChild<T>(string name, Transform anchor) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(anchor != null ? anchor : transform, false);
            return go.AddComponent<T>();
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
        }

        /// <summary>Open tier 3 on a given tab, closing whatever else was open.</summary>
        public void SummonTab(string tabId)
        {
            if (TierThree == null) return;

            TierThree.SelectTab(tabId);
            Registry?.Open(TierThree);
        }
    }
}
