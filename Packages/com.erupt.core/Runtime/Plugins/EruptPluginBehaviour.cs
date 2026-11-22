using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Plugins
{
    /// <summary>
    /// MonoBehaviour base for plugins: no-op hooks, the context after registration, and
    /// discovery by <see cref="PluginHost"/>. Put one on a prefab next to the ERUPT root.
    /// </summary>
    public abstract class EruptPluginBehaviour : MonoBehaviour, IEruptPlugin
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public virtual IReadOnlyList<string> DependsOn => Array.Empty<string>();

        /// <summary>The context this plugin was registered with; null before registration.</summary>
        public IEruptContext Context { get; private set; }
        public bool IsRegistered => Context != null;

        void IEruptPlugin.OnRegister(IEruptContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            OnRegister(context);
        }

        void IEruptPlugin.OnUnregister(IEruptContext context)
        {
            if (Context == null) return;   // already unregistered
            try { OnUnregister(context); }
            finally { Context = null; }
        }

        /// <summary>The host this plugin is registered with; it unregisters the plugins in order.</summary>
        internal PluginHost Host { get; set; }

        /// <summary>
        /// A registered plugin whose object is destroyed has the host unregister the plugins
        /// first, in dependency order. When a scene is unloaded its objects go in no particular
        /// order; a plugin destroyed before the host got round to it kept its subscriptions (on
        /// the ROS bus and the robot registry, which outlive the scene) and was called back dead.
        /// A subclass that needs <c>OnDestroy</c> overrides this and calls the base.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (Context == null) return;
            if (!ReferenceEquals(Host, null)) Host.PluginIsBeingDestroyed();
            else ((IEruptPlugin)this).OnUnregister(Context);
        }

        /// <summary>Contribute verbs/tabs/widgets, subscribe to ROS. Called once, in dependency order.</summary>
        protected virtual void OnRegister(IEruptContext context) { }
        protected virtual void OnUnregister(IEruptContext context) { }
    }
}
