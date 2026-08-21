namespace Erupt.Ui
{
    /// <summary>
    /// Anything the tier registry manages. Part 8 code review item 7: UI is registered
    /// with an explicit tier assignment, never instantiated ad hoc.
    /// </summary>
    public interface IUiElement
    {
        UiTier Tier { get; }

        /// <summary>Stable identity, unique within a tier.</summary>
        string Id { get; }
    }

    /// <summary>A control in the always-visible tier. At most four may exist.</summary>
    public interface ITierOneControl : IUiElement { }

    /// <summary>A configuration panel or library. Only one may be open at a time.</summary>
    public interface ISummonedPanel : IUiElement
    {
        bool IsOpen { get; }
        void Open();
        void Close();
    }
}
