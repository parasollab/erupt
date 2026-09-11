using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

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
            OnUnregister(context);
            Context = null;
        }

        void IEruptPlugin.OnModeChanged(AppMode mode) => OnModeChanged(mode);

        /// <summary>Contribute verbs/tabs/widgets, subscribe to ROS. Called once, in dependency order.</summary>
        protected virtual void OnRegister(IEruptContext context) { }
        protected virtual void OnUnregister(IEruptContext context) { }
        protected virtual void OnModeChanged(AppMode mode) { }
    }
}
