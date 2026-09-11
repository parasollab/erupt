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
        IRosBus Ros { get; }
        IRobotModel Robot { get; }
        EnvironmentRegistry Environment { get; }
        SelectionService Selection { get; }
        ModeManager Modes { get; }
        UndoStack Undo { get; }
        InteractionRouter Router { get; }
        /// <summary>Null when the scene has no tier UI; plugins must then skip UI contributions.</summary>
        IUiHost Ui { get; }
    }

    /// <summary>Plain implementation; the host builds one from scene services, tests build one by hand.</summary>
    public sealed class EruptContext : IEruptContext
    {
        public IRosBus Ros { get; set; }
        public IRobotModel Robot { get; set; }
        public EnvironmentRegistry Environment { get; set; }
        public SelectionService Selection { get; set; }
        public ModeManager Modes { get; set; }
        public UndoStack Undo { get; set; }
        public InteractionRouter Router { get; set; }
        public IUiHost Ui { get; set; }
    }
}
