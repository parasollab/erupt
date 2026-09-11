using System;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>An in-world widget a plugin places; tier 1 is closed, so this is the only "always there" surface.</summary>
    public interface IWorldWidget
    {
        string Id { get; }
        GameObject GameObject { get; }
    }

    /// <summary>
    /// What a plugin may do to the UI: add a verb to a row, implement a guideline verb,
    /// add a tier 3 tab, summon it, or place a world widget. Nothing else — tier 1 is
    /// capped and there is no way to add a menu. Implemented by <see cref="TierUiRig"/>.
    /// </summary>
    public interface IUiHost
    {
        VerbRegistry Verbs { get; }

        /// <summary>Add a plugin verb to a kind's row and bind its handler.</summary>
        Verb RegisterVerb(SelectionKind kind, string verbId, string label, Action<ISelectable> handler, string pluginId);

        /// <summary>Implement a verb that already exists in the row (a Guidelines verb).</summary>
        void BindVerb(string verbId, Action<ISelectable> handler);

        /// <summary>Add a tier 3 tab and build its content.</summary>
        PanelTab AddTab(string id, string label, Action<RectTransform> build);

        void SummonTab(string id);

        void RegisterWidget(IWorldWidget widget);
        void UnregisterWidget(IWorldWidget widget);
    }
}
