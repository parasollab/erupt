using UnityEngine;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// Template for learning from demonstration: record interaction samples while a
    /// demonstration is being recorded, then publish it. The base class owns the sample
    /// subscription and the <c>correct</c> verb on a plan; the subclass owns the data.
    /// Recording starts and stops when the user says so (the tab's Record button, the
    /// <c>correct</c> verb, the subclass's own triggers); there is no mode to enter first.
    /// </summary>
    /// <remarks>
    /// One plugin instance serves every robot in the scene: a demonstration is started on the
    /// robot the selected object belongs to (the active robot otherwise), so the template
    /// methods name their robot. Keep the per-robot recorders in a <see cref="RobotSessions{T}"/>.
    /// The tab shows the active robot and is rebuilt when that changes.
    /// </remarks>
    public abstract class DemonstrationPlugin : EruptPluginBehaviour
    {
        private bool recording;
        private ActiveRobotTab demosTab;

        public bool IsRecording => recording;
        public string DemosTabId => $"demos-{Id}";
        public PanelTab DemosTab => demosTab?.Tab;

        /// <summary>One interaction sample, delivered only while a demonstration is being recorded.</summary>
        protected abstract void OnSample(InteractionSample sample);

        /// <summary>Start recording a demonstration on <paramref name="robot"/>.</summary>
        public abstract void StartDemonstration(RobotInstance robot);
        /// <summary>Stop the demonstration being recorded on <paramref name="robot"/>; null stops whichever is running.</summary>
        public abstract void StopDemonstration(RobotInstance robot);
        /// <summary>Publish <paramref name="robot"/>'s last demonstration.</summary>
        public abstract void Publish(RobotInstance robot);

        /// <summary>Start recording on the active robot.</summary>
        public void StartDemonstration() => StartDemonstration(Context?.Robots?.Active);
        /// <summary>Stop whichever demonstration is being recorded.</summary>
        public void StopDemonstration() => StopDemonstration(null);
        /// <summary>Publish the active robot's last demonstration.</summary>
        public void Publish() => Publish(Context?.Robots?.Active);

        /// <summary>Fill the demos tab (record/stop/list) for the active robot. Called again whenever the active robot changes. Default: nothing.</summary>
        protected virtual void BuildDemosTab(RectTransform content) { }

        /// <summary>The robot a selected object belongs to; the active robot for an object that belongs to none.</summary>
        public RobotInstance RobotOf(ISelectable target)
        {
            IRobotRegistry robots = Context?.Robots;
            if (robots == null) return null;
            RobotInstance owner = target?.GameObject != null ? robots.OwnerOf(target.GameObject) : null;
            return owner != null ? owner : robots.Active;
        }

        protected override void OnRegister(IEruptContext context)
        {
            IUiHost ui = context.Ui;
            if (ui == null)
            {
                Debug.LogWarning($"[{Id}] No UI host; demonstration tab and verbs not contributed.", this);
                return;
            }
            ui.BindVerb(CorrectVerb, Correct);
            // The verb is on every plan's menu while this plugin is registered. The condition
            // outlives the plugin: once it is unregistered the verb is gone from the menus.
            if (ui.Verbs != null && ui.Verbs.Contains(SelectionKind.Trajectory, CorrectVerb))
                ui.Verbs.ShowWhen(SelectionKind.Trajectory, CorrectVerb, _ => IsRegistered);
            demosTab = new ActiveRobotTab(ui, context.Robots, DemosTabId, DisplayName, BuildDemosTab);
            if (demosTab.Tab != null) demosTab.Tab.Order = TabOrder;
        }

        /// <summary>Where this plugin's tab stands in the tier 3 strip (<see cref="PanelTab.Order"/>).</summary>
        protected virtual int TabOrder => PanelTab.DefaultOrder;

        /// <summary>The verb on a plan that starts a demonstration to correct it.</summary>
        public const string CorrectVerb = "correct";

        // The correct verb: start a demonstration on the robot the selected plan is for, and
        // open the demos tab, where the recording shows and where it is stopped.
        private void Correct(ISelectable target)
        {
            bool was = recording;
            StartDemonstration(RobotOf(target));
            if (!was && recording) Context?.Ui?.SummonTab(DemosTabId);
        }

        /// <summary>Raise a refusal on the interaction router, at the selected object when there is one.</summary>
        protected void Refuse(string reason, ISelectable target = null, RefusalCode code = RefusalCode.VerbUnavailable)
        {
            Vector3 at = target?.GameObject != null ? target.GameObject.transform.position : transform.position;
            Context?.Router?.ReportRefusal(InteractionRefusal.Refuse(code, reason, at));
        }

        protected override void OnUnregister(IEruptContext context)
        {
            SetRecording(false);
            demosTab?.Dispose();
            demosTab = null;
            context?.Ui?.RemoveTab(DemosTabId);
            context?.Ui?.Verbs?.Refresh();
        }

        /// <summary>Rebuild the demos tab for the active robot (it is rebuilt by itself when the active robot changes).</summary>
        protected void RebuildDemosTab() => demosTab?.Rebuild();

        /// <summary>Subclasses flip this from Start/StopDemonstration; it gates the sample feed.</summary>
        protected void SetRecording(bool on)
        {
            if (recording == on) return;
            recording = on;
            if (on) InteractionSampleBus.Sample += Forward;
            else InteractionSampleBus.Sample -= Forward;
        }

        private void Forward(InteractionSample sample)
        {
            if (recording) OnSample(sample);
        }
    }
}
