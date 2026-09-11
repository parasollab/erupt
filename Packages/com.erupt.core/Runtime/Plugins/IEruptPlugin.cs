using System.Collections.Generic;
using Erupt.Interaction;

namespace Erupt.Plugins
{
    /// <summary>
    /// The plugin contract. A plugin contributes verbs, tabs and widgets through the
    /// context's UI host, subscribes to ROS through the context's bus, and reacts to mode
    /// changes. It never reaches into core objects it was not handed.
    /// </summary>
    public interface IEruptPlugin
    {
        /// <summary>Stable id ("moveit", "mtc", "rader"); other plugins depend on it by this name.</summary>
        string Id { get; }
        string DisplayName { get; }
        /// <summary>Plugin ids that must be registered before this one. Resolved by <see cref="PluginHost"/>.</summary>
        IReadOnlyList<string> DependsOn { get; }

        void OnRegister(IEruptContext context);
        void OnUnregister(IEruptContext context);
        void OnModeChanged(AppMode mode);
    }
}
