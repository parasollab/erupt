using System;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// The one selection concept. Tier 2 menus key off <see cref="Current"/>'s kind.
    /// </summary>
    /// <remarks>
    /// Hover is tracked separately from selection: pointing at an object is not selecting
    /// it. Quest controllers and hands only ever drive selection.
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

        // A selected or hovered object that is destroyed (a plan's handle when its plan is
        // dropped, an obstacle removed from the ROS side) stops being the selection: everything
        // that follows the selection would otherwise keep reading a dead object, every frame.
        private void LateUpdate()
        {
            if (IsDestroyed(Current)) ClearSelection();
            if (IsDestroyed(Hovered)) SetHover(null);
        }

        /// <summary>True for a selectable whose Unity object has been destroyed (it is not a C# null).</summary>
        public static bool IsDestroyed(ISelectable selectable) =>
            selectable is UnityEngine.Object unityObject && unityObject == null;

        private void OnDestroy()
        {
            SelectionChanged = null;
            HoverChanged = null;
        }
    }
}
