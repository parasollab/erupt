using UnityEngine;
using UnityEngine.UI;
using Erupt.Ui;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Tier 3 "RADER" tab: record/stop, replay, publish, the two streaming toggles, and the
    /// FERL "Resume" answer. Six interactive elements (Guidelines Part 8: ≤ 7). Replaces
    /// RADER's <c>FullMenu</c> / <c>FERLPopup</c> prefabs. Record is only enabled in Teach.
    /// </summary>
    public sealed class DemosTab
    {
        private static readonly Vector2 ButtonSize = new(300f, 56f);

        private readonly RaderPlugin plugin;
        private readonly RectTransform root;
        private readonly Button recordButton, replayButton, publishButton, stateButton, mirrorButton, resumeButton;
        private readonly TMPro.TextMeshProUGUI statusLabel, feedbackLabel, infoLabel;

        public DemosTab(RaderPlugin plugin, RectTransform content)
        {
            this.plugin = plugin;
            root = content;

            var column = UiBuilder.CreateColumn("Rader", content, 8f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            Transform t = column.transform;

            statusLabel = UiBuilder.CreateLabel("Status", t, "", 22f);
            recordButton = UiBuilder.CreateButton("Record", t, "Record", ButtonSize, OnRecord);
            replayButton = UiBuilder.CreateButton("Replay", t, "Replay", ButtonSize, OnReplay);
            publishButton = UiBuilder.CreateButton("Publish", t, "Publish", ButtonSize, plugin.Publish);

            var rec = plugin.Recorder;
            stateButton = UiBuilder.CreateToggle("State", t, "Publish state", rec != null && rec.PublishState, ButtonSize,
                on => { if (plugin.Recorder != null) plugin.Recorder.PublishState = on; });
            mirrorButton = UiBuilder.CreateToggle("Mirror", t, "Mirror joint_states", rec != null && rec.MirrorInput, ButtonSize,
                on => { if (plugin.Recorder != null) plugin.Recorder.MirrorInput = on; });

            feedbackLabel = UiBuilder.CreateLabel("Feedback", t, "", 20f);
            resumeButton = UiBuilder.CreateButton("Resume", t, "Resume", ButtonSize, () => plugin.Feedback?.Respond());

            infoLabel = UiBuilder.CreateLabel("Info", t, "", 18f);
            infoLabel.enableWordWrapping = true;

            plugin.StateChanged += Refresh;
            if (plugin.Feedback != null) plugin.Feedback.Changed += Refresh;
            if (plugin.Log != null) plugin.Log.Received += OnInfo;
            Refresh();
        }

        public RectTransform Root => root;
        public int InteractiveElementCount => UiBuilder.CountInteractive(root);
        public Button RecordButton => recordButton;
        public Button ResumeButton => resumeButton;

        public void Dispose()
        {
            plugin.StateChanged -= Refresh;
            if (plugin.Feedback != null) plugin.Feedback.Changed -= Refresh;
            if (plugin.Log != null) plugin.Log.Received -= OnInfo;
        }

        private void OnRecord()
        {
            if (plugin.IsRecording) plugin.StopDemonstration();
            else plugin.StartDemonstration();
        }

        private void OnReplay()
        {
            if (plugin.IsReplaying) plugin.StopReplay();
            else plugin.Replay();
        }

        private void OnInfo(string text)
        {
            if (infoLabel != null) infoLabel.text = text;
        }

        private void Refresh()
        {
            if (root == null) return;
            var rec = plugin.Recorder;
            bool hasDemo = rec?.LastDemonstration != null;

            UiBuilder.SetButtonText(recordButton, plugin.IsRecording ? "Stop" : "Record");
            UiBuilder.SetInteractable(recordButton, rec != null && (plugin.IsRecording || plugin.InTeachMode));
            UiBuilder.SetButtonText(replayButton, plugin.IsReplaying ? "Stop replay" : "Replay");
            UiBuilder.SetInteractable(replayButton, hasDemo && !plugin.IsRecording && plugin.Player != null);
            UiBuilder.SetInteractable(publishButton, hasDemo && !plugin.IsRecording);

            string status;
            if (rec == null) status = "No robot";
            else if (plugin.IsRecording) status = $"Recording… {rec.PointCount} points";
            else if (hasDemo) status = $"Demonstration: {rec.LastDemonstration.points.Length} points, {rec.LastDemonstration.joint_names.Length} joints";
            else status = plugin.InTeachMode ? "Ready (Teach)" : "Switch to Teach mode to record";
            statusLabel.text = status;

            var fb = plugin.Feedback;
            bool pending = fb != null && fb.RequestPending;
            feedbackLabel.text = !pending ? "" : fb.CanRespond ? "Feedback request satisfied" : "Feedback requested… waiting";
            UiBuilder.SetInteractable(resumeButton, pending && fb.CanRespond);
        }
    }
}
