using System;
using System.Collections.Generic;
using System.Linq;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// The live tier 2 vocabulary: the Guidelines Part 2 rows from <see cref="VerbTable"/>
    /// plus verbs plugins contribute at registration.
    /// </summary>
    /// <remarks>
    /// Plugin verbs carry <see cref="VerbOrigin.Plugin"/> and the plugin id, so the Part 8
    /// review question ("added a verb, not a menu?") can be answered from the registry.
    /// <see cref="SelectionKind"/> stays closed: a plugin can add verbs to a kind, never a kind.
    /// </remarks>
    public class VerbRegistry
    {
        private readonly Dictionary<SelectionKind, List<Verb>> rows = new();

        /// <summary>Fires whenever a row changes, so open menus can rebuild.</summary>
        public event Action Changed;

        /// <summary>A registry seeded with the Guidelines table.</summary>
        public static VerbRegistry FromTable()
        {
            var registry = new VerbRegistry();
            foreach (var kind in VerbTable.Kinds)
                registry.rows[kind] = VerbTable.For(kind).ToList();
            return registry;
        }

        public IReadOnlyList<Verb> For(SelectionKind kind) =>
            rows.TryGetValue(kind, out var verbs) ? verbs : Array.Empty<Verb>();

        public IEnumerable<Verb> AvailableFor(SelectionKind kind) => For(kind).Where(v => v.IsAvailable);

        public IEnumerable<SelectionKind> Kinds => rows.Keys;

        public bool Contains(SelectionKind kind, string verbId) => For(kind).Any(v => v.Id == verbId);

        // Verbs that are shown only for some selections of their kind, by (kind, verb id).
        private readonly Dictionary<(SelectionKind kind, string id), Func<ISelectable, bool>> conditions = new();

        /// <summary>
        /// The verbs of a kind that apply to one selection: the kind's row without the verbs
        /// whose condition (<see cref="ShowWhen"/>) says no for <paramref name="target"/>.
        /// </summary>
        public IReadOnlyList<Verb> For(SelectionKind kind, ISelectable target)
        {
            IReadOnlyList<Verb> row = For(kind);
            if (conditions.Count == 0 || target == null) return row;
            List<Verb> shown = null;
            for (int i = 0; i < row.Count; i++)
            {
                bool show = IsShownFor(kind, row[i].Id, target);
                if (!show && shown == null)
                {
                    shown = new List<Verb>(row.Count);
                    for (int j = 0; j < i; j++) shown.Add(row[j]);
                }
                else if (show && shown != null) shown.Add(row[i]);
            }
            return shown ?? row;
        }

        /// <summary>
        /// Show a verb only for the selections <paramref name="appliesTo"/> accepts (a verb that
        /// needs something the selected object may not have). The condition is asked when a
        /// menu is built; call <see cref="Refresh"/> when its answer may have changed. A
        /// condition that throws hides the verb.
        /// </summary>
        public void ShowWhen(SelectionKind kind, string verbId, Func<ISelectable, bool> appliesTo)
        {
            if (string.IsNullOrEmpty(verbId)) throw new ArgumentNullException(nameof(verbId));
            if (appliesTo == null) conditions.Remove((kind, verbId));
            else conditions[(kind, verbId)] = appliesTo;
            Changed?.Invoke();
        }

        /// <summary>Whether a verb of a kind is shown for a selection; true for a verb without a condition.</summary>
        public bool IsShownFor(SelectionKind kind, string verbId, ISelectable target)
        {
            if (!conditions.TryGetValue((kind, verbId), out Func<ISelectable, bool> appliesTo)) return true;
            try { return appliesTo(target); }
            catch (Exception) { return false; }
        }

        /// <summary>Have open menus build themselves again (the answer of a <see cref="ShowWhen"/> condition changed).</summary>
        public void Refresh() => Changed?.Invoke();

        /// <summary>
        /// Add a plugin verb to a kind's row. Refuses a duplicate id within the row: two
        /// verbs with one id would make <c>Bind</c> ambiguous.
        /// </summary>
        public Verb Register(SelectionKind kind, string verbId, string label, string pluginId)
        {
            if (kind == SelectionKind.None) throw new ArgumentException("Cannot register a verb for SelectionKind.None.", nameof(kind));
            if (string.IsNullOrEmpty(verbId)) throw new ArgumentNullException(nameof(verbId));
            if (string.IsNullOrEmpty(pluginId)) throw new ArgumentNullException(nameof(pluginId));
            if (Contains(kind, verbId))
                throw new InvalidOperationException($"Verb '{verbId}' already exists for {kind}; a plugin may bind it but not add it twice.");

            var verb = new Verb(verbId, label, VerbAvailability.Available, VerbOrigin.Plugin, pluginId);
            if (!rows.TryGetValue(kind, out var list)) rows[kind] = list = new List<Verb>();
            list.Add(verb);
            Changed?.Invoke();
            return verb;
        }

        /// <summary>Remove every verb a plugin contributed (on unregister).</summary>
        public int RemoveAllFrom(string pluginId)
        {
            int removed = 0;
            foreach (var row in rows)
            {
                foreach (Verb verb in row.Value)
                    if (verb.Origin == VerbOrigin.Plugin && verb.PluginId == pluginId) conditions.Remove((row.Key, verb.Id));
                removed += row.Value.RemoveAll(v => v.Origin == VerbOrigin.Plugin && v.PluginId == pluginId);
            }
            if (removed > 0) Changed?.Invoke();
            return removed;
        }
    }
}
