using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// The one place robot intents are dispatched. It sits on the interaction router's object
    /// (created on demand) and serves every robot in the scene: an intent whose target belongs
    /// to a robot goes to that robot's <see cref="RobotInteractionController"/> and to no other.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Select: the controller of the robot that owns the hit object selects it; a hit that
    /// belongs to no robot drops the robots' own selection.</item>
    /// <item>Drag: tracked per input source, so two hands can drag two robots at once. A ray
    /// that hit nothing robot-owned but passes close to an end-effector handle drags the
    /// nearest such handle.</item>
    /// <item>Axis: jogs the selected joint of the robot that owns the current selection.</item>
    /// <item>Active robot: the owner of the most recent selection that belongs to a robot (a
    /// link, an end effector, a plan's handle). Selecting anything else leaves it as it is.
    /// This is the only caller of <see cref="IRobotRegistry.SetActive"/>.</item>
    /// <item>While the scene has several robots the active one is shown: its links are tinted
    /// and every robot's selectables name their robot ("End Effector · left").</item>
    /// <item>Proximity: the robots' tips and bodies are offered to the router as one
    /// <see cref="IProximitySource"/>.</item>
    /// </list>
    /// </remarks>
    [DisallowMultipleComponent]
    public class RobotIntentDispatcher : MonoBehaviour, IProximitySource
    {
        private sealed class Entry
        {
            public RobotInteractionController controller;
            public RobotInstance robot;
            public float jogRadiansPerSecond;
        }

        // How close (metres) a ray must pass to an end-effector handle to drag it without hitting it.
        private const float NearHandleDistance = 0.08f;

        private readonly List<Entry> entries = new();
        private readonly Dictionary<string, Entry> dragBySource = new();
        private InteractionRouter router;
        private SelectionService selection;
        private readonly List<IRobotRegistry> registries = new();
        private bool subscribed;

        /// <summary>The dispatcher of a router, created on the router's object when it has none.</summary>
        public static RobotIntentDispatcher For(InteractionRouter router)
        {
            if (router == null) return null;
            if (!router.TryGetComponent(out RobotIntentDispatcher dispatcher))
                dispatcher = router.gameObject.AddComponent<RobotIntentDispatcher>();
            dispatcher.Use(router);
            return dispatcher;
        }

        /// <summary>The interaction controllers being served, in the order they were added.</summary>
        public int Count => entries.Count;

        /// <summary>Whether an input source is dragging something of a robot.</summary>
        public bool IsDragging(string sourceId) => dragBySource.ContainsKey(sourceId ?? "default");

        /// <summary>The controller an input source is dragging with; null when it drags nothing of a robot.</summary>
        public RobotInteractionController DraggedBy(string sourceId) =>
            dragBySource.TryGetValue(sourceId ?? "default", out Entry entry) ? entry.controller : null;

        private void Use(InteractionRouter target)
        {
            if (ReferenceEquals(router, target)) return;
            Unsubscribe();
            router = target;
            if (isActiveAndEnabled) Subscribe();
        }

        /// <summary>Follow this selection service for the active robot (found in the scene when none is given).</summary>
        public void SetSelection(SelectionService service)
        {
            if (ReferenceEquals(selection, service)) return;
            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
            selection = service;
            if (selection != null && isActiveAndEnabled) selection.SelectionChanged += OnSelectionChanged;
        }

        /// <summary>Serve a robot's interaction controller. <paramref name="robot"/> may be null for a controller that stands alone.</summary>
        public void Add(RobotInteractionController controller, RobotInstance robot, float jogRadiansPerSecond)
        {
            if (controller == null) return;
            Entry entry = Find(controller);
            if (entry == null)
            {
                entry = new Entry { controller = controller };
                entries.Add(entry);
            }
            entry.robot = robot;
            entry.jogRadiansPerSecond = jogRadiansPerSecond;

            IRobotRegistry registry = robot != null ? robot.Registry : null;
            if (registry != null && !registries.Contains(registry))
            {
                registries.Add(registry);
                registry.ActiveChanged += OnRobotsChanged;
                registry.Added += OnRobotsChanged;
                registry.Removed += OnRobotsChanged;
            }
            RefreshActiveDisplay();
        }

        /// <summary>Stop serving a controller; drags it holds end.</summary>
        public void Remove(RobotInteractionController controller)
        {
            Entry entry = Find(controller);
            if (entry == null) return;
            var sources = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in dragBySource)
                if (ReferenceEquals(pair.Value, entry)) sources.Add(pair.Key);
            foreach (string sourceId in sources)
            {
                dragBySource.Remove(sourceId);
                if (controller != null) controller.EndHandleDragBySource(sourceId);
            }
            entries.Remove(entry);
            if (controller != null)
            {
                controller.SetActiveHighlight(false);
                controller.SetLabelSuffix(null);
            }
            RefreshActiveDisplay();
        }

        private void OnEnable()
        {
            if (router == null) router = GetComponent<InteractionRouter>();
            Subscribe();
        }

        private void OnDisable() => Unsubscribe();

        private void OnDestroy()
        {
            foreach (IRobotRegistry registry in registries)
            {
                registry.ActiveChanged -= OnRobotsChanged;
                registry.Added -= OnRobotsChanged;
                registry.Removed -= OnRobotsChanged;
            }
            registries.Clear();
        }

        private void Subscribe()
        {
            if (subscribed || router == null) return;
            router.Select += OnSelect;
            router.BeginDrag += OnBeginDrag;
            router.Drag += OnDrag;
            router.EndDrag += OnEndDrag;
            router.Axis += OnAxis;
            router.AddProximitySource(this);
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (selection != null) selection.SelectionChanged += OnSelectionChanged;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
            if (router == null) return;
            router.Select -= OnSelect;
            router.BeginDrag -= OnBeginDrag;
            router.Drag -= OnDrag;
            router.EndDrag -= OnEndDrag;
            router.Axis -= OnAxis;
            router.RemoveProximitySource(this);
        }

        // ---- ownership ---------------------------------------------------------------------

        private Entry Find(RobotInteractionController controller)
        {
            foreach (Entry entry in entries)
                if (ReferenceEquals(entry.controller, controller)) return entry;
            return null;
        }

        /// <summary>
        /// The controller of the robot an object belongs to: the robot above it or named by its
        /// <see cref="RobotOwned"/> marker, else the controller that says the object is its own.
        /// Null for an object that belongs to no robot.
        /// </summary>
        public RobotInteractionController ControllerFor(GameObject target) => EntryFor(target)?.controller;

        private Entry EntryFor(GameObject target)
        {
            if (target == null) return null;
            RobotInstance owner = OwnerOf(target);
            foreach (Entry entry in entries)
            {
                if (entry.controller == null) continue;
                if (owner != null ? ReferenceEquals(entry.robot, owner) : entry.robot == null && entry.controller.Owns(target))
                    return entry;
            }
            return null;
        }

        private static RobotInstance OwnerOf(GameObject target)
        {
            RobotInstance robot = target.GetComponentInParent<RobotInstance>(true);
            if (robot != null) return robot;
            RobotOwned marker = target.GetComponentInParent<RobotOwned>(true);
            return marker != null ? marker.Owner : null;
        }

        private static string SourceOf(InteractionIntent intent) => intent.Sample.SourceId ?? "default";

        // ---- intents -----------------------------------------------------------------------

        /// <summary>A select intent: the owner of the hit object selects it; nobody else reacts.</summary>
        public void OnSelect(InteractionIntent intent)
        {
            // A source that is dragging a robot does not also select with the same press.
            if (dragBySource.ContainsKey(SourceOf(intent))) return;
            // A miss leaves the robots' selection as it is.
            if (!intent.HasHit) return;

            Entry owner = EntryFor(intent.Hit.transform != null ? intent.Hit.transform.gameObject : null);
            if (owner != null)
            {
                owner.controller.SelectFromIntent(intent);
                return;
            }
            // Something that is no robot's was hit: whichever robot holds the selection lets go of it.
            foreach (Entry entry in entries)
                if (entry.controller != null) entry.controller.ClearSelection();
        }

        /// <summary>A drag begins: on the robot whose object was hit, else on the end-effector handle the ray passes closest to.</summary>
        public void OnBeginDrag(InteractionIntent intent)
        {
            if (!intent.HasHit) return;
            string sourceId = SourceOf(intent);
            if (dragBySource.ContainsKey(sourceId)) return;

            Entry owner = EntryFor(intent.Hit.transform != null ? intent.Hit.transform.gameObject : null);
            if (owner != null)
            {
                if (owner.controller.TryBeginHandleDrag(intent)) dragBySource[sourceId] = owner;
                return;
            }

            Entry nearest = null;
            float nearestDistance = NearHandleDistance;
            foreach (Entry entry in entries)
            {
                if (entry.controller == null) continue;
                float distance = entry.controller.DistanceToHandle(intent.Ray);
                if (distance > nearestDistance) continue;
                nearest = entry;
                nearestDistance = distance;
            }
            if (nearest != null && nearest.controller.TryBeginHandleDragNear(intent)) dragBySource[sourceId] = nearest;
        }

        public void OnDrag(InteractionIntent intent)
        {
            if (!dragBySource.TryGetValue(SourceOf(intent), out Entry entry) || entry.controller == null) return;
            entry.controller.UpdateHandleDrag(intent);
            Surface(entry.controller.LastRefusal, intent);
        }

        public void OnEndDrag(InteractionIntent intent)
        {
            string sourceId = SourceOf(intent);
            if (!dragBySource.TryGetValue(sourceId, out Entry entry)) return;
            dragBySource.Remove(sourceId);
            if (entry.controller != null) entry.controller.EndHandleDrag(intent);
        }

        /// <summary>The axis jogs the selected joint of the robot that owns the current selection.</summary>
        public void OnAxis(InteractionIntent intent)
        {
            Entry entry = JogTarget();
            // A robot that is being dragged is not jogged at the same time.
            if (entry == null || entry.controller.IsDragging) return;

            // Deadzone and precision scaling already applied by the router.
            float delta = intent.Axis.y * entry.jogRadiansPerSecond * Time.deltaTime;
            if (Mathf.Approximately(delta, 0f)) return;

            entry.controller.JogSelectedJoint(delta);
            Surface(entry.controller.LastRefusal, intent);
        }

        // The robot whose joint the axis moves: the owner of the current selection when there is
        // a selection service; without one, the only robot that has a joint selected.
        private Entry JogTarget()
        {
            if (selection != null)
            {
                GameObject selected = selection.Current?.GameObject;
                Entry owner = EntryFor(selected);
                return owner != null && owner.controller.SelectedJoint != null ? owner : null;
            }
            Entry found = null;
            foreach (Entry entry in entries)
            {
                if (entry.controller == null || entry.controller.SelectedJoint == null) continue;
                if (found != null) return null;
                found = entry;
            }
            return found;
        }

        // The refusal goes out with the source that caused it, so the feedback can answer on
        // that controller. The router drops repeats, so reporting on every frame is fine.
        private void Surface(InteractionRefusal refusal, InteractionIntent intent)
        {
            if (refusal.IsRefused && router != null) router.ReportRefusal(refusal.From(intent.Sample.SourceId));
        }

        // ---- the active robot ------------------------------------------------------------------

        // Selecting something that belongs to a robot makes that robot the active one; any other
        // selection (an obstacle, nothing) leaves the active robot as it is.
        private void OnSelectionChanged(ISelectable current)
        {
            GameObject selected = current?.GameObject;
            if (selected == null) return;
            RobotInstance owner = OwnerOf(selected);
            if (owner == null || owner.Registry == null) return;
            if (!ReferenceEquals(owner.Registry.Active, owner)) owner.Registry.SetActive(owner);
        }

        private void OnRobotsChanged(RobotInstance _) => RefreshActiveDisplay();

        // With one robot nothing says which robot is active. With several, each robot's
        // selectables carry its id and the active one's links are tinted.
        private void RefreshActiveDisplay()
        {
            foreach (Entry entry in entries)
            {
                if (entry.controller == null) continue;
                IRobotRegistry registry = entry.robot != null ? entry.robot.Registry : null;
                bool several = registry != null && registry.All.Count > 1;
                entry.controller.SetLabelSuffix(several ? entry.robot.Id : null);
                entry.controller.SetActiveHighlight(several && ReferenceEquals(registry.Active, entry.robot));
            }
        }

        // ---- proximity -------------------------------------------------------------------------

        void IProximitySource.GetProbePoints(List<Vector3> points)
        {
            foreach (Entry entry in entries)
            {
                Transform tip = entry.robot != null && entry.robot.Model != null ? entry.robot.Model.EndEffector : null;
                if (tip == null && entry.controller != null) tip = entry.controller.Handle;
                if (tip != null) points.Add(tip.position);
            }
        }

        bool IProximitySource.IsOwnCollider(Collider collider)
        {
            if (collider == null) return false;
            if (OwnerOf(collider.gameObject) != null) return true;
            foreach (Entry entry in entries)
                if (entry.controller != null && entry.robot == null && entry.controller.Owns(collider.gameObject)) return true;
            return false;
        }
    }
}
