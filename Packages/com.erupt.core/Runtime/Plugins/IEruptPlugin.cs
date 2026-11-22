using System.Collections.Generic;

namespace Erupt.Plugins
{
    /// <summary>
    /// The plugin contract. A plugin contributes verbs, tabs and widgets through the
    /// context's UI host and subscribes to ROS through the context's bus. It never reaches
    /// into core objects it was not handed. There are no application modes to react to:
    /// whatever a plugin offers is offered all the time, as a button the user presses.
    /// </summary>
    public interface IEruptPlugin
    {
        /// <summary>Stable id ("moveit", "mtc", "demonstration"); other plugins depend on it by this name.</summary>
        string Id { get; }
        string DisplayName { get; }
        /// <summary>Plugin ids that must be registered before this one. Resolved by <see cref="PluginHost"/>.</summary>
        IReadOnlyList<string> DependsOn { get; }

        void OnRegister(IEruptContext context);
        void OnUnregister(IEruptContext context);
    }
}
