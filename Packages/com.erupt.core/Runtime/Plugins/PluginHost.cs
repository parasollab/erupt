using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>A dependency the host cannot satisfy: a missing plugin id or a cycle.</summary>
    public sealed class PluginDependencyException : Exception
    {
        public PluginDependencyException(string message) : base(message) { }
    }

    /// <summary>
    /// Finds the plugins in the scene, orders them by <see cref="IEruptPlugin.DependsOn"/>,
    /// hands each the <see cref="IEruptContext"/>, and fans out mode changes. Lives on the
    /// ERUPT root next to the core services it collects into the context.
    /// </summary>
    /// <remarks>
    /// Follows <c>InteractionRouter.Awake</c>: scene services are found with
    /// <c>FindFirstObjectByType</c> and their absence is a logged error, not a silent null.
    /// Tests call <see cref="Initialise"/> with a hand-built context before Start.
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    public class PluginHost : MonoBehaviour
    {
        [Header("Core services (found in the scene if empty)")]
        [SerializeField] private DirectArticulationIKController robot;
        [SerializeField] private EnvironmentRegistry environment;
        [SerializeField] private SelectionService selection;
        [SerializeField] private ModeManager modes;
        [SerializeField] private InteractionRouter router;
        [SerializeField] private TierUiRig ui;

        [Tooltip("Register every EruptPluginBehaviour in the scene on Start (inactive objects included).")]
        [SerializeField] private bool discoverInScene = true;

        private readonly List<IEruptPlugin> registered = new();
        private IEruptContext context;
        private bool started;

        public IEruptContext Context => context;

        /// <summary>Whether Start registers every scene plugin. Tests turn it off before Start.</summary>
        public bool DiscoverInScene
        {
            get => discoverInScene;
            set => discoverInScene = value;
        }
        /// <summary>Plugins in registration (dependency) order.</summary>
        public IReadOnlyList<IEruptPlugin> Plugins => registered;

        /// <summary>The app's command history. Owned here; the tier UI renders it.</summary>
        public UndoStack Undo { get; } = new();

        /// <summary>Supply a context (tests) instead of collecting one from the scene.</summary>
        public void Initialise(IEruptContext ctx)
        {
            if (started) throw new InvalidOperationException("PluginHost already started; initialise before Start.");
            context = ctx ?? throw new ArgumentNullException(nameof(ctx));
        }

        private void Awake()
        {
            if (ui == null) ui = FindFirstObjectByType<TierUiRig>(FindObjectsInactive.Include);
            // Runs before the rig's Awake when the host's execution order is earlier or the
            // rig is still inactive; otherwise the rig keeps the stack it built with and
            // this call is a no-op, which is harmless because the bar exposes its own.
            if (ui != null) ui.UseUndoStack(Undo);
        }

        private void Start()
        {
            started = true;
            context ??= BuildContext();

            if (modes != null) modes.ModeChanged += OnModeChanged;
            else if (context.Modes != null) context.Modes.ModeChanged += OnModeChanged;

            if (!discoverInScene) return;

            var found = FindObjectsByType<EruptPluginBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID)
                .Cast<IEruptPlugin>().ToList();
            try
            {
                RegisterAll(found);
            }
            catch (PluginDependencyException e)
            {
                Debug.LogError($"[PluginHost] {e.Message} No plugin was registered.", this);
            }
        }

        private void OnDestroy()
        {
            var m = modes != null ? modes : context?.Modes;
            if (m != null) m.ModeChanged -= OnModeChanged;
            UnregisterAll();
        }

        /// <summary>
        /// Register a set of plugins in dependency order. Refuses the whole set (registers
        /// nothing) when an id is missing or the dependencies form a cycle.
        /// </summary>
        public void RegisterAll(IEnumerable<IEruptPlugin> plugins)
        {
            context ??= BuildContext();
            var ordered = Order(plugins.ToList(), registered.Select(p => p.Id));
            foreach (var plugin in ordered) RegisterOne(plugin);
        }

        /// <summary>Register one plugin; its dependencies must already be registered.</summary>
        public void Register(IEruptPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            RegisterAll(new[] { plugin });
        }

        public void Unregister(IEruptPlugin plugin)
        {
            if (plugin == null || !registered.Remove(plugin)) return;
            plugin.OnUnregister(context);
            context?.Ui?.Verbs.RemoveAllFrom(plugin.Id);
        }

        public bool TryGet(string id, out IEruptPlugin plugin)
        {
            plugin = registered.FirstOrDefault(p => p.Id == id);
            return plugin != null;
        }

        public bool TryGet<T>(out T plugin) where T : class, IEruptPlugin
        {
            plugin = registered.OfType<T>().FirstOrDefault();
            return plugin != null;
        }

        private void RegisterOne(IEruptPlugin plugin)
        {
            if (registered.Any(p => p.Id == plugin.Id))
                throw new PluginDependencyException($"Plugin id '{plugin.Id}' is already registered.");
            plugin.OnRegister(context);
            registered.Add(plugin);
        }

        private void UnregisterAll()
        {
            for (int i = registered.Count - 1; i >= 0; i--)
            {
                var plugin = registered[i];
                registered.RemoveAt(i);
                try { plugin.OnUnregister(context); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        private void OnModeChanged(AppMode mode)
        {
            foreach (var plugin in registered)
            {
                try { plugin.OnModeChanged(mode); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        /// <summary>Topological order (Kahn). Throws on a missing dependency or a cycle.</summary>
        internal static List<IEruptPlugin> Order(List<IEruptPlugin> plugins, IEnumerable<string> alreadyRegistered)
        {
            var known = new HashSet<string>(alreadyRegistered);
            var byId = new Dictionary<string, IEruptPlugin>();
            foreach (var p in plugins)
            {
                if (string.IsNullOrEmpty(p.Id)) throw new PluginDependencyException($"Plugin '{p.DisplayName}' has an empty id.");
                if (byId.ContainsKey(p.Id) || known.Contains(p.Id)) throw new PluginDependencyException($"Plugin id '{p.Id}' appears twice.");
                byId[p.Id] = p;
            }

            foreach (var p in plugins)
                foreach (var dep in p.DependsOn ?? Array.Empty<string>())
                    if (!byId.ContainsKey(dep) && !known.Contains(dep))
                        throw new PluginDependencyException($"Plugin '{p.Id}' depends on '{dep}', which is not present.");

            var remaining = new Dictionary<string, int>();
            foreach (var p in plugins)
                remaining[p.Id] = (p.DependsOn ?? Array.Empty<string>()).Count(d => byId.ContainsKey(d));

            var ready = new Queue<IEruptPlugin>(plugins.Where(p => remaining[p.Id] == 0));
            var ordered = new List<IEruptPlugin>();
            while (ready.Count > 0)
            {
                var p = ready.Dequeue();
                ordered.Add(p);
                foreach (var q in plugins)
                    if ((q.DependsOn ?? Array.Empty<string>()).Contains(p.Id) && --remaining[q.Id] == 0)
                        ready.Enqueue(q);
            }

            if (ordered.Count != plugins.Count)
            {
                var stuck = plugins.Where(p => !ordered.Contains(p)).Select(p => p.Id);
                throw new PluginDependencyException($"Plugin dependency cycle among [{string.Join(", ", stuck)}].");
            }
            return ordered;
        }

        private IEruptContext BuildContext()
        {
            if (robot == null) robot = FindFirstObjectByType<DirectArticulationIKController>();
            if (environment == null) environment = FindFirstObjectByType<EnvironmentRegistry>();
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (modes == null) modes = FindFirstObjectByType<ModeManager>();
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (ui == null) ui = FindFirstObjectByType<TierUiRig>(FindObjectsInactive.Include);

            if (robot == null) Debug.LogError("[PluginHost] No DirectArticulationIKController in the scene.", this);
            if (environment == null) Debug.LogError("[PluginHost] No EnvironmentRegistry in the scene.", this);
            if (selection == null) Debug.LogError("[PluginHost] No SelectionService in the scene.", this);
            if (modes == null) Debug.LogError("[PluginHost] No ModeManager in the scene.", this);
            if (router == null) Debug.LogWarning("[PluginHost] No InteractionRouter in the scene.", this);
            if (ui == null) Debug.LogWarning("[PluginHost] No TierUiRig in the scene; plugins get no UI host.", this);

            return new EruptContext
            {
                Ros = RosBus.Instance,
                Robot = robot,
                Environment = environment,
                Selection = selection,
                Modes = modes,
                Undo = ui != null && ui.UndoStack != null ? ui.UndoStack : Undo,
                Router = router,
                Ui = ui
            };
        }
    }
}
