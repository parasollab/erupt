using System;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// Everything core offers a plugin, handed over at registration so a test can
    /// substitute any of it (a <c>FakeRosBus</c>, a fake robot, a fake UI host).
    /// </summary>
    public interface IEruptContext
    {
        /// <summary>The transport for names that belong to no robot; a robot's own traffic goes through <see cref="RobotInstance.Ros"/>.</summary>
        IRosBus Ros { get; }
        /// <summary>
        /// The robots in the scene, any number of them. A plugin serves all of them (see
        /// <see cref="RobotSessions{T}"/>) and acts on the one a selection belongs to
        /// (<see cref="IRobotRegistry.OwnerOf"/>); <see cref="IRobotRegistry.Active"/> is the
        /// one the user last selected.
        /// </summary>
        IRobotRegistry Robots { get; }
        EnvironmentRegistry Environment { get; }
        SelectionService Selection { get; }
        UndoStack Undo { get; }
        InteractionRouter Router { get; }
        /// <summary>Null when the scene has no tier UI; plugins must then skip UI contributions.</summary>
        IUiHost Ui { get; }
    }

    /// <summary>Plain implementation; the host builds one from scene services, tests build one by hand.</summary>
    public sealed class EruptContext : IEruptContext
    {
        public IRosBus Ros { get; set; }
        public IRobotRegistry Robots { get; set; }
        public EnvironmentRegistry Environment { get; set; }
        public SelectionService Selection { get; set; }
        public UndoStack Undo { get; set; }
        public InteractionRouter Router { get; set; }
        public IUiHost Ui { get; set; }
    }
}
