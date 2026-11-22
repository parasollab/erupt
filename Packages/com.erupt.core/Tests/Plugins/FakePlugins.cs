using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>Plain plugin for host tests: records the calls it receives.</summary>
    public sealed class FakePlugin : IEruptPlugin
    {
        public static readonly List<string> RegisterOrder = new();

        public FakePlugin(string id, params string[] deps) { Id = id; DependsOn = deps; }

        public string Id { get; }
        public string DisplayName => Id;
        public IReadOnlyList<string> DependsOn { get; }
        public IEruptContext Registered { get; private set; }
        public bool Unregistered { get; private set; }

        public void OnRegister(IEruptContext context) { Registered = context; RegisterOrder.Add(Id); }
        public void OnUnregister(IEruptContext context) => Unregistered = true;
    }

    /// <summary>Scene plugin for discovery tests.</summary>
    public sealed class FakePluginBehaviour : EruptPluginBehaviour
    {
        public string id = "scene-plugin";
        public string[] deps = Array.Empty<string>();
        public int registerCalls, unregisterCalls;
        public override string Id => id;
        public override string DisplayName => id;
        public override IReadOnlyList<string> DependsOn => deps;
        protected override void OnRegister(IEruptContext context) => registerCalls++;
        protected override void OnUnregister(IEruptContext context) => unregisterCalls++;
    }

    /// <summary>Records what a plugin asks of the UI without building any uGUI.</summary>
    public sealed class FakeUiHost : IUiHost
    {
        public VerbRegistry Verbs { get; } = VerbRegistry.FromTable();
        public readonly Dictionary<string, Action<ISelectable>> Bound = new();
        public readonly List<(SelectionKind kind, string id, string plugin)> Registered = new();
        public readonly List<string> Tabs = new();
        public readonly Dictionary<string, PanelTab> TabsById = new();
        public readonly List<IWorldWidget> Widgets = new();
        public string Summoned;

        public Verb RegisterVerb(SelectionKind kind, string verbId, string label, Action<ISelectable> handler, string pluginId)
        {
            var v = Verbs.Register(kind, verbId, label, pluginId);
            Registered.Add((kind, verbId, pluginId));
            if (handler != null) Bound[verbId] = handler;
            return v;
        }
        public void BindVerb(string verbId, Action<ISelectable> handler) => Bound[verbId] = handler;
        public PanelTab AddTab(string id, string label, Action<RectTransform> build)
        {
            Tabs.Add(id);
            build?.Invoke(null);
            return TabsById[id] = new PanelTab(id, label);
        }
        public bool RemoveTab(string id)
        {
            TabsById.Remove(id);
            return Tabs.Remove(id);
        }
        public void SummonTab(string id) => Summoned = id;
        public void RegisterWidget(IWorldWidget widget) => Widgets.Add(widget);
        public void UnregisterWidget(IWorldWidget widget) => Widgets.Remove(widget);
    }

    /// <summary>Planner that answers synchronously, for the template tests. Records which robot each call was for.</summary>
    public sealed class FakePlanner : PlanningPlugin
    {
        public ISelectable GoalTarget;
        public int PlanCalls, PreviewCalls, StopCalls, ExecuteCalls;
        public bool RefuseGoal;
        public readonly List<RobotInstance> GoalRobots = new();
        public readonly List<RobotInstance> PlanRobots = new();
        public readonly List<string> PreviewedRobotIds = new();
        public readonly List<string> ExecutedRobotIds = new();
        /// <summary>Robots this planner says it cannot serve.</summary>
        public readonly HashSet<RobotInstance> Unserved = new();
        public int TabBuilds;

        public override string Id => "fake";
        public override string DisplayName => "Fake Planner";
        public override bool Serves(RobotInstance robot) => !Unserved.Contains(robot);

        public override InteractionRefusal SetGoal(RobotInstance robot, ISelectable endEffector)
        {
            GoalTarget = endEffector;
            GoalRobots.Add(robot);
            return RefuseGoal ? InteractionRefusal.Refuse("nope", Vector3.zero) : InteractionRefusal.None;
        }

        public override void RequestPlan(RobotInstance robot, PlanPreferences preferences, Action<PlanResult> done)
        {
            PlanCalls++;
            PlanRobots.Add(robot);
            var result = PublishResult(new PlanResult
            {
                Trajectory = new RosMessageTypes.Trajectory.JointTrajectoryMsg
                {
                    joint_names = new[] { "j1" },
                    points = new[] { new RosMessageTypes.Trajectory.JointTrajectoryPointMsg { positions = new[] { 0.0 } } }
                }
            }, robot);
            done?.Invoke(result);
        }

        public override void Preview(PlanResult plan) { PreviewCalls++; PreviewedRobotIds.Add(plan?.RobotId); }
        public override void StopPreview(RobotInstance robot) { StopCalls++; StoppedPreviews?.Add(robot); }
        public override void Execute(PlanResult plan, Action<ExecutionStatus> status)
        {
            ExecuteCalls++;
            ExecutedRobotIds.Add(plan?.RobotId);
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Succeeded));
        }

        protected override void BuildSettingsTab(RectTransform content) => TabBuilds++;

        /// <summary>Drop one robot's plans, as a planner does when that robot's task is reset.</summary>
        public void ClearPlansOf(RobotInstance robot) => ClearResults(robot);

        /// <summary>Robots this planner needs to stay where they are (it is executing on them, say).</summary>
        public readonly HashSet<RobotInstance> Busy = new();
        /// <summary>The robots whose base move this planner was told about, in order.</summary>
        public readonly List<RobotInstance> BaseMoves = new();
        public readonly List<RobotInstance> StoppedPreviews = new();

        protected override string WhyBaseMustStay(RobotInstance robot) =>
            Busy.Contains(robot) ? $"Robot '{robot.Id}' is executing a trajectory." : null;
        protected override void OnRobotBaseMoved(RobotInstance robot, Pose previousBase) => BaseMoves.Add(robot);
    }

    /// <summary>Demonstration plugin that counts what the base class lets through.</summary>
    public sealed class FakeDemonstrator : DemonstrationPlugin
    {
        public int Samples, StartCalls, StopCalls, PublishCalls;
        public InteractionSample Last;
        public readonly List<RobotInstance> StartRobots = new();
        public int TabBuilds;

        public override string Id => "fake-demo";
        public override string DisplayName => "Fake Demonstrator";

        protected override void OnSample(InteractionSample sample) { Samples++; Last = sample; }
        public override void StartDemonstration(RobotInstance robot) { StartCalls++; StartRobots.Add(robot); SetRecording(true); }
        public override void StopDemonstration(RobotInstance robot) { StopCalls++; SetRecording(false); }
        public override void Publish(RobotInstance robot) => PublishCalls++;
        protected override void BuildDemosTab(RectTransform content) => TabBuilds++;
    }
}
