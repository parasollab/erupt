using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
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
        public List<AppMode> Modes { get; } = new();

        public void OnRegister(IEruptContext context) { Registered = context; RegisterOrder.Add(Id); }
        public void OnUnregister(IEruptContext context) => Unregistered = true;
        public void OnModeChanged(AppMode mode) => Modes.Add(mode);
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
        public PanelTab AddTab(string id, string label, Action<RectTransform> build) { Tabs.Add(id); build?.Invoke(null); return new PanelTab(id, label); }
        public void SummonTab(string id) => Summoned = id;
        public void RegisterWidget(IWorldWidget widget) => Widgets.Add(widget);
        public void UnregisterWidget(IWorldWidget widget) => Widgets.Remove(widget);
    }

    /// <summary>Planner that answers synchronously, for the template tests.</summary>
    public sealed class FakePlanner : PlanningPlugin
    {
        public ISelectable GoalTarget;
        public int PlanCalls, PreviewCalls, StopCalls, ExecuteCalls;
        public bool RefuseGoal;

        public override string Id => "fake";
        public override string DisplayName => "Fake Planner";

        public override InteractionRefusal SetGoal(ISelectable endEffector)
        {
            GoalTarget = endEffector;
            return RefuseGoal ? InteractionRefusal.Refuse("nope", Vector3.zero) : InteractionRefusal.None;
        }

        public override void RequestPlan(PlanPreferences preferences, Action<PlanResult> done)
        {
            PlanCalls++;
            var result = PublishResult(new PlanResult
            {
                Trajectory = new RosMessageTypes.Trajectory.JointTrajectoryMsg
                {
                    joint_names = new[] { "j1" },
                    points = new[] { new RosMessageTypes.Trajectory.JointTrajectoryPointMsg { positions = new[] { 0.0 } } }
                }
            });
            done?.Invoke(result);
        }

        public override void Preview(PlanResult plan) => PreviewCalls++;
        public override void StopPreview() => StopCalls++;
        public override void Execute(PlanResult plan, Action<ExecutionStatus> status)
        {
            ExecuteCalls++;
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Succeeded));
        }
    }
}
