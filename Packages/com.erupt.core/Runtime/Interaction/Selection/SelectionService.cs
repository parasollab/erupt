using System;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// The one selection concept. Tier 2 menus key off <see cref="Current"/>'s kind.
    /// </summary>
    /// <remarks>
    /// Hover is tracked separately from selection because Guidelines Part 6 wants tier 2
    /// to appear near a gazed object on visionOS without a deliberate ray-select. Quest
    /// controllers only ever drive selection, but the distinction has to exist now or the
    /// port has to restructure it later.
    /// </remarks>
    public class SelectionService : MonoBehaviour
    {
        public ISelectable Current { get; private set; }
        public ISelectable Hovered { get; private set; }

        public SelectionKind CurrentKind => Current?.Kind ?? SelectionKind.None;

        /// <summary>Fires with null when the selection is cleared.</summary>
        public event Action<ISelectable> SelectionChanged;

        /// <summary>Fires with null when nothing is hovered.</summary>
        public event Action<ISelectable> HoverChanged;

        public void Select(ISelectable selectable)
        {
            if (ReferenceEquals(Current, selectable)) return;
            Current = selectable;
            SelectionChanged?.Invoke(Current);
        }

        public void ClearSelection() => Select(null);

        public void SetHover(ISelectable selectable)
        {
            if (ReferenceEquals(Hovered, selectable)) return;
            Hovered = selectable;
            HoverChanged?.Invoke(Hovered);
        }

        /// <summary>Resolve a hit GameObject to a selectable, searching parents.</summary>
        public static ISelectable Resolve(GameObject go) =>
            go == null ? null : go.GetComponentInParent<ISelectable>();

        private void OnDestroy()
        {
            SelectionChanged = null;
            HoverChanged = null;
        }
    }
}
