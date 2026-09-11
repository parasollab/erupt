using System;

namespace Erupt.Ui
{
    /// <summary>Whether a verb can actually be invoked yet.</summary>
    public enum VerbAvailability
    {
        /// <summary>Backed by working code.</summary>
        Available,

        /// <summary>
        /// In the Part 2 table but not implemented. Shown disabled rather than hidden, so
        /// the menu reflects the intended vocabulary instead of quietly shrinking to
        /// whatever happens to be built.
        /// </summary>
        NotYetImplemented
    }

    /// <summary>Where a verb came from, so deviations from the guidelines stay visible.</summary>
    public enum VerbOrigin
    {
        /// <summary>Listed in the Guidelines Part 2 tier 2 table.</summary>
        Guidelines,

        /// <summary>Existing ERUPT functionality with no row in the Part 2 table.</summary>
        EruptAddition,
        /// <summary>Contributed by a plugin at registration; <see cref="Verb.PluginId"/> says which.</summary>
        Plugin
    }

    /// <summary>One entry on a contextual menu.</summary>
    public readonly struct Verb
    {
        public readonly string Id;
        public readonly string Label;
        public readonly VerbAvailability Availability;
        public readonly VerbOrigin Origin;
        /// <summary>Owning plugin for <see cref="VerbOrigin.Plugin"/> verbs; null otherwise.</summary>
        public readonly string PluginId;

        public Verb(string id, string label, VerbAvailability availability, VerbOrigin origin = VerbOrigin.Guidelines, string pluginId = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label;
            Availability = availability;
            Origin = origin;
            PluginId = pluginId;
        }

        public bool IsAvailable => Availability == VerbAvailability.Available;
    }
}
