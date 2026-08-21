using System.Collections.Generic;
using System.Linq;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// The tier 2 vocabulary, one row per selection kind. Guidelines Part 2.
    /// </summary>
    /// <remarks>
    /// Adding a feature should mean adding an entry here, not adding a menu — that is the
    /// whole point of the tier 2 table.
    ///
    /// Three verbs are marked <see cref="VerbOrigin.EruptAddition"/>: snap to surface,
    /// duplicate, and preview. They are existing ERUPT functionality with no row in
    /// Part 2's table. They are kept rather than dropped, and marked rather than
    /// smuggled in, so the divergence from the guidelines stays visible.
    /// </remarks>
    public static class VerbTable
    {
        private static readonly Dictionary<SelectionKind, Verb[]> Rows = new()
        {
            [SelectionKind.Obstacle] = new[]
            {
                Available("resize",     "Resize"),
                Pending  ("reshape",    "Reshape"),
                Pending  ("paint-cost", "Paint Cost"),
                Pending  ("lock",       "Lock"),
                Available("delete",     "Delete"),
                Extra    ("snap",       "Snap to Surface"),
                Extra    ("duplicate",  "Duplicate")
            },

            [SelectionKind.Manipulable] = new[]
            {
                Pending("grasp-here",    "Grasp Here"),
                Pending("set-as-target", "Set as Target"),
                Pending("properties",    "Properties")
            },

            [SelectionKind.RobotLink] = new[]
            {
                Available("set-joint-angle", "Set Joint Angle"),
                Pending  ("lock-joint",      "Lock Joint")
            },

            [SelectionKind.EndEffector] = new[]
            {
                Available("set-goal",      "Set Goal"),
                Pending  ("gripper",       "Open/Close Gripper"),
                Pending  ("preview-grasp", "Preview Grasp")
            },

            [SelectionKind.Trajectory] = new[]
            {
                Pending  ("scrub",   "Scrub"),
                Pending  ("correct", "Correct"),
                Pending  ("compare", "Compare"),
                Available("execute", "Execute"),
                Extra    ("preview", "Preview")
            },

            [SelectionKind.Waypoint] = new[]
            {
                Pending("move",       "Move"),
                Pending("delete",     "Delete"),
                Pending("pin-timing", "Pin Timing")
            }
        };

        /// <summary>Verbs for a selection kind. Empty when nothing is selected.</summary>
        public static IReadOnlyList<Verb> For(SelectionKind kind) =>
            Rows.TryGetValue(kind, out var verbs) ? verbs : System.Array.Empty<Verb>();

        /// <summary>Verbs that can actually be invoked today.</summary>
        public static IEnumerable<Verb> AvailableFor(SelectionKind kind) =>
            For(kind).Where(v => v.IsAvailable);

        /// <summary>Every kind that has at least one verb.</summary>
        public static IEnumerable<SelectionKind> Kinds => Rows.Keys;

        private static Verb Available(string id, string label) =>
            new(id, label, VerbAvailability.Available);

        private static Verb Pending(string id, string label) =>
            new(id, label, VerbAvailability.NotYetImplemented);

        private static Verb Extra(string id, string label) =>
            new(id, label, VerbAvailability.Available, VerbOrigin.EruptAddition);
    }
}
