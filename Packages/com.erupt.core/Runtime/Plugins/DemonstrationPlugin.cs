using UnityEngine;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// Template for learning from demonstration: record interaction samples while the
    /// app is in Teach mode, then publish the demonstration. The base class owns the
    /// Teach-mode gate and the sample subscription; the subclass owns the data.
    /// </summary>
    public abstract class DemonstrationPlugin : EruptPluginBehaviour
    {
        private bool teachMode;
        private bool recording;
        private PanelTab demosTab;

        public bool IsRecording => recording;
        public bool InTeachMode => teachMode;
        public string DemosTabId => $"demos-{Id}";

        /// <summary>One interaction sample, delivered only while Teach mode and recording are both on.</summary>
        protected abstract void OnSample(InteractionSample sample);

        public abstract void StartDemonstration();
        public abstract void StopDemonstration();
        public abstract void Publish();

        /// <summary>Fill the demos tab (record/stop/list). Default: nothing.</summary>
        protected virtual void BuildDemosTab(RectTransform content) { }

        protected override void OnRegister(IEruptContext context)
        {
            teachMode = context.Modes != null && context.Modes.Is(AppMode.Teach);

            IUiHost ui = context.Ui;
            if (ui == null)
            {
                Debug.LogWarning($"[{Id}] No UI host; demonstration tab and verbs not contributed.", this);
                return;
            }
            ui.BindVerb("correct", _ => { if (teachMode) StartDemonstration(); });
            demosTab = ui.AddTab(DemosTabId, DisplayName, BuildDemosTab);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            SetRecording(false);
            demosTab = null;
        }

        protected override void OnModeChanged(AppMode mode)
        {
            teachMode = mode == AppMode.Teach;
            if (!teachMode && recording)
            {
                StopDemonstration();
                SetRecording(false);
            }
        }

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
            if (teachMode && recording) OnSample(sample);
        }
    }
}
