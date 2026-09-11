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
            foreach (var list in rows.Values)
                removed += list.RemoveAll(v => v.Origin == VerbOrigin.Plugin && v.PluginId == pluginId);
            if (removed > 0) Changed?.Invoke();
            return removed;
        }
    }
}
