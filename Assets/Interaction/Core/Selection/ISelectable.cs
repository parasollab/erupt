using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>Something the user can select, and what kind of thing it is.</summary>
    public interface ISelectable
    {
        SelectionKind Kind { get; }
        GameObject GameObject { get; }

        /// <summary>Shown in the tier 2 menu header. Falls back to the object name.</summary>
        string DisplayName { get; }
    }
}
