using System;
using System.Collections.Generic;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// What a tier 2 menu should show, and what happens when a verb is chosen.
    /// </summary>
    /// <remarks>
    /// Deliberately free of uGUI: the rules about when a contextual menu exists and what
    /// it contains are testable without building a Canvas. The view is a thin renderer
    /// over this.
    ///
    /// Guidelines Part 2: tier 2 "appears attached to the currently selected object,
    /// disappears on deselect".
    /// </remarks>
    public class ContextualMenuModel
    {
        private readonly Dictionary<string, Action<ISelectable>> handlers = new();
        private VerbRegistry registry;

        public ContextualMenuModel() : this(null) { }

        /// <param name="verbs">Vocabulary to draw from; the Guidelines table when null.</param>
        public ContextualMenuModel(VerbRegistry verbs)
        {
            registry = verbs ?? VerbRegistry.FromTable();
            registry.Changed += RefreshVerbs;
        }

        public VerbRegistry Registry => registry;

        /// <summary>Swap the vocabulary (the rig hands plugins' registry to the view it built).</summary>
        public void SetRegistry(VerbRegistry verbs)
        {
            if (verbs == null || ReferenceEquals(verbs, registry)) return;
            registry.Changed -= RefreshVerbs;
            registry = verbs;
            registry.Changed += RefreshVerbs;
            RefreshVerbs();
        }

        public ISelectable Target { get; private set; }
        public SelectionKind Kind => Target?.Kind ?? SelectionKind.None;

        /// <summary>True when a menu should be on screen at all.</summary>
        public bool IsVisible => Target != null && Verbs.Count > 0;

        public IReadOnlyList<Verb> Verbs { get; private set; } = Array.Empty<Verb>();

        /// <summary>Fires whenever the menu's contents or visibility change.</summary>
        public event Action Changed;

        /// <summary>Bind a verb id to its implementation. Unbound verbs render disabled, or are left out (<see cref="ListsUnavailableVerbs"/>).</summary>
        public void Bind(string verbId, Action<ISelectable> handler)
        {
            handlers[verbId] = handler;
            RefreshVerbs();
        }

        /// <summary>
        /// Whether <see cref="Verbs"/> includes the verbs that cannot be invoked: the ones the
        /// table marks as not implemented yet and the ones nothing has bound. True lists them
        /// (the view greys them out); false leaves them out, so a menu holds only what can be
        /// pressed and a selection with nothing to do has no menu. The verbs stay in the
        /// registry either way.
        /// </summary>
        public bool ListsUnavailableVerbs
        {
            get => listsUnavailableVerbs;
            set
            {
                if (listsUnavailableVerbs == value) return;
                listsUnavailableVerbs = value;
                RefreshVerbs();
            }
        }
        private bool listsUnavailableVerbs = true;

        public void SetTarget(ISelectable selectable)
        {
            if (ReferenceEquals(Target, selectable)) return;

            Target = selectable;
            RefreshVerbs();
        }

        private void RefreshVerbs()
        {
            // A verb with a condition (VerbRegistry.ShowWhen) is listed only for the selections it applies to.
            // A target whose Unity object is gone has no verbs: nothing is asked about it.
            bool gone = Target == null || SelectionService.IsDestroyed(Target);
            IReadOnlyList<Verb> row = gone ? Array.Empty<Verb>() : registry.For(Target.Kind, Target);
            if (!listsUnavailableVerbs && row.Count > 0)
            {
                var pressable = new List<Verb>(row.Count);
                foreach (Verb verb in row)
                    if (CanInvoke(verb)) pressable.Add(verb);
                row = pressable;
            }
            Verbs = row;
            Changed?.Invoke();
        }

        /// <summary>
        /// Whether a verb can be pressed: it must be implemented in the table *and* have a
        /// handler bound. A verb marked available with nothing wired to it is a wiring
        /// bug, and disabling it is better than a button that silently does nothing.
        /// </summary>
        public bool CanInvoke(Verb verb) => verb.IsAvailable && handlers.ContainsKey(verb.Id);

        /// <summary>
        /// Invoke a verb. Returns a refusal reason when it cannot run, rather than a
        /// silent no-op — Guidelines Part 3.
        /// </summary>
        public InteractionRefusal Invoke(Verb verb)
        {
            if (Target == null)
                return InteractionRefusal.Refuse(RefusalCode.NothingSelected, "Nothing is selected.", UnityEngine.Vector3.zero);

            UnityEngine.Vector3 at = Target.GameObject != null
                ? Target.GameObject.transform.position
                : UnityEngine.Vector3.zero;

            if (!verb.IsAvailable)
                return InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, $"{verb.Label} is not available yet.", at);

            if (!handlers.TryGetValue(verb.Id, out var handler))
                return InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, $"{verb.Label} is not wired up.", at);

            handler(Target);
            return InteractionRefusal.None;
        }

        public InteractionRefusal Invoke(string verbId)
        {
            foreach (var verb in Verbs)
                if (verb.Id == verbId) return Invoke(verb);

            return InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, $"No verb '{verbId}' for {Kind}.", UnityEngine.Vector3.zero);
        }

        /// <summary>Follow a selection service for as long as it lives.</summary>
        public void FollowSelection(SelectionService service)
        {
            if (service == null) return;
            SetTarget(service.Current);
            service.SelectionChanged += SetTarget;
        }

        public void StopFollowing(SelectionService service)
        {
            if (service != null) service.SelectionChanged -= SetTarget;
        }
    }
}
