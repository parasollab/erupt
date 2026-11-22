using UnityEngine;
using UnityEngine.UI;
using Erupt.Ui;

namespace Erupt.Plugins.Demonstration
{
    /// <summary>
    /// Tier 3 "Demonstrations" tab: record/stop, replay, publish, the two streaming toggles,
    /// and the last info message. Five interactive elements (Guidelines Part 8: ≤ 7).
    /// Record is available whenever the robot can record.
    /// </summary>
    public sealed class DemosTab
    {
        private static readonly Vector2 ButtonSize = new(300f, 56f);

        private readonly DemonstrationCollectionPlugin plugin;
        private readonly RectTransform root;
        private readonly Button recordButton, replayButton, publishButton, stateButton, mirrorButton;
        private readonly TMPro.TextMeshProUGUI statusLabel, infoLabel;

        public DemosTab(DemonstrationCollectionPlugin plugin, RectTransform content)
        {
            this.plugin = plugin;
            root = content;

            var column = UiBuilder.CreateColumn("Demonstrations", content, 8f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            Transform t = column.transform;

            statusLabel = UiBuilder.CreateLabel("Status", t, "", 22f);
            recordButton = UiBuilder.CreateButton("Record", t, "Record", ButtonSize, OnRecord);
            replayButton = UiBuilder.CreateButton("Replay", t, "Replay", ButtonSize, OnReplay);
            publishButton = UiBuilder.CreateButton("Publish", t, "Publish", ButtonSize, plugin.Publish);

            // The recorder of the robot that is active now; the plugin builds a new tab when that changes.
            var rec = plugin.Recorder;
            stateButton = UiBuilder.CreateToggle("State", t, "Publish state", rec != null && rec.PublishState, ButtonSize,
                on => { if (rec != null) rec.PublishState = on; });
            mirrorButton = UiBuilder.CreateToggle("Mirror", t, "Mirror joint_states", rec != null && rec.MirrorInput, ButtonSize,
                on => { if (rec != null) rec.MirrorInput = on; });

            infoLabel = UiBuilder.CreateLabel("Info", t, "", 18f);
            infoLabel.enableWordWrapping = true;

            plugin.StateChanged += Refresh;
            if (plugin.Log != null) plugin.Log.Received += OnInfo;
            Refresh();
        }

        public RectTransform Root => root;
        public int InteractiveElementCount => UiBuilder.CountInteractive(root);
        public Button RecordButton => recordButton;

        public void Dispose()
        {
            plugin.StateChanged -= Refresh;
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
            UiBuilder.SetInteractable(recordButton, rec != null);
            UiBuilder.SetButtonText(replayButton, plugin.IsReplaying ? "Stop replay" : "Replay");
            UiBuilder.SetInteractable(replayButton, hasDemo && !plugin.IsRecording && plugin.Player != null);
            UiBuilder.SetInteractable(publishButton, hasDemo && !plugin.IsRecording);

            string status;
            if (rec == null) status = "No robot";
            else if (plugin.IsRecording) status = $"Recording… {rec.PointCount} points";
            else if (hasDemo) status = $"Demonstration: {rec.LastDemonstration.points.Length} points, {rec.LastDemonstration.joint_names.Length} joints";
            else status = "Ready to record";
            statusLabel.text = status;
        }
    }
}
