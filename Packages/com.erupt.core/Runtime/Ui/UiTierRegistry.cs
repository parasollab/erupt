using System;
using System.Collections.Generic;
using System.Linq;

namespace Erupt.Ui
{
    /// <summary>
    /// Owns the tier invariants from Guidelines Part 2, in code rather than by convention.
    /// </summary>
    public class UiTierRegistry
    {
        /// <summary>Guidelines Part 2: "Hard cap of four controls."</summary>
        public const int TierOneCap = 4;

        private readonly List<ITierOneControl> tierOne = new();
        private readonly List<ISummonedPanel> summoned = new();

        public IReadOnlyList<ITierOneControl> TierOneControls => tierOne;
        public IReadOnlyList<ISummonedPanel> SummonedPanels => summoned;

        public ISummonedPanel OpenPanel => summoned.FirstOrDefault(p => p.IsOpen);

        public event Action<ISummonedPanel> PanelOpened;
        public event Action<ISummonedPanel> PanelClosed;

        /// <summary>
        /// Add a persistent control. Throws past the cap — adding a fifth is a design
        /// error, and Part 2 says it belongs in tier 2 or tier 3 instead.
        /// </summary>
        public void RegisterTierOne(ITierOneControl control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));

            if (control.Tier != UiTier.Persistent)
                throw new UiTierViolationException(
                    $"'{control.Id}' registered as tier 1 but declares {control.Tier}.");

            if (tierOne.Any(c => c.Id == control.Id))
                throw new UiTierViolationException($"Duplicate tier 1 control id '{control.Id}'.");

            if (tierOne.Count >= TierOneCap)
                throw new UiTierViolationException(
                    $"Tier 1 is capped at {TierOneCap} controls (Guidelines Part 2). " +
                    $"Cannot add '{control.Id}' alongside " +
                    $"[{string.Join(", ", tierOne.Select(c => c.Id))}]. " +
                    "It belongs in tier 2 or tier 3.");

            tierOne.Add(control);
        }

        public void UnregisterTierOne(ITierOneControl control) => tierOne.Remove(control);

        public void RegisterPanel(ISummonedPanel panel)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));

            if (panel.Tier != UiTier.Summoned)
                throw new UiTierViolationException(
                    $"'{panel.Id}' registered as a summoned panel but declares {panel.Tier}.");

            if (summoned.Any(p => p.Id == panel.Id))
                throw new UiTierViolationException($"Duplicate tier 3 panel id '{panel.Id}'.");

            summoned.Add(panel);
        }

        public void UnregisterPanel(ISummonedPanel panel) => summoned.Remove(panel);

        /// <summary>
        /// Open one panel, closing any other. Part 2: one open at a time, none by default.
        /// </summary>
        public void Open(ISummonedPanel panel)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));
            if (!summoned.Contains(panel))
                throw new UiTierViolationException($"Panel '{panel.Id}' was never registered.");

            foreach (var other in summoned)
            {
                if (ReferenceEquals(other, panel) || !other.IsOpen) continue;
                other.Close();
                PanelClosed?.Invoke(other);
            }

            if (panel.IsOpen) return;
            panel.Open();
            PanelOpened?.Invoke(panel);
        }

        public void Close(ISummonedPanel panel)
        {
            if (panel == null || !panel.IsOpen) return;
            panel.Close();
            PanelClosed?.Invoke(panel);
        }

        public void CloseAll()
        {
            foreach (var panel in summoned.Where(p => p.IsOpen).ToList())
            {
                panel.Close();
                PanelClosed?.Invoke(panel);
            }
        }
    }
}
