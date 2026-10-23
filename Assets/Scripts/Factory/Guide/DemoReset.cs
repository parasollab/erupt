using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Factory
{
    /// <summary>
    /// One button for the person running the demo: everything back to the start for the next
    /// user, without leaving the headset. Cancels a running plan or execution, drops a recording
    /// without sending it, stops previews, re-stacks the billets, opens the vise, closes the
    /// doors, sends the robot back to the placement phase at its start pose and resets the guide
    /// to the welcome step. Wired to "Restart demo" on the placement panel; F5 in the editor.
    /// </summary>
    public class DemoReset : MonoBehaviour
    {
        private const string LogTag = "[DemoReset]";

        [SerializeField] private RobotPlacementController placement;
        [SerializeField] private PickPlaceClient client;
        [SerializeField] private PickPlaceTaskRecorder recorder;
        [SerializeField] private MTCTrajectoryPlayer trajectoryPlayer;
        [SerializeField] private MachineDoors doors;
        [SerializeField] private MachineVise vise;
        [SerializeField] private PartRecovery parts;
        [SerializeField] private DemoGuide guide;
        [SerializeField, Tooltip("Found in the scene when empty.")] private SelectionManager selection;
        [SerializeField, Tooltip("Seconds to wait for a cancelled goal to end before the robot is repositioned.")]
        private float cancelTimeout = 4f;
        [Tooltip("Editor-only key that restarts the demo (never in a headset build).")]
        public KeyCode editorRestartKey = KeyCode.F5;

        public bool InProgress { get; private set; }
        public int RestartCount { get; private set; }
        public event System.Action Restarted;

        private void Update()
        {
            if (!Application.isEditor || XRSettings.isDeviceActive) return;
            if (Input.GetKeyDown(editorRestartKey)) Restart();
        }

        public void Restart()
        {
            if (InProgress) return;
            StartCoroutine(RestartRoutine());
        }

        private IEnumerator RestartRoutine()
        {
            InProgress = true;
            Debug.Log($"{LogTag} restarting the demo");
            if (selection == null) selection = SelectionManager.Instance;

            if (recorder != null && recorder.IsRecording) recorder.CancelRecording();
            if (trajectoryPlayer != null)
            {
                if (trajectoryPlayer.IsPlaying) trajectoryPlayer.Stop();
                if (trajectoryPlayer.IsMirroring) trajectoryPlayer.StopLiveMirror();
            }
            if (client != null && client.Busy)
            {
                var cancel = client.CancelAsync();
                float deadline = Time.time + cancelTimeout;
                while (client.Busy && Time.time < deadline) yield return null;
                if (client.Busy) Debug.LogWarning($"{LogTag} the goal did not end within {cancelTimeout} s; repositioning anyway once it does");
                while (client.Busy) yield return null;
                _ = cancel;
            }
            selection?.ClearSelection();
            if (vise != null && vise.IsClosed) vise.SetImmediate(true);
            if (doors != null && doors.State != MachineDoors.DoorState.Closed) doors.Close();
            if (parts != null)
            {
                int returned = parts.ReturnAll();
                if (returned > 0) Debug.Log($"{LogTag} {returned} billet(s) back on the stand");
            }
            if (placement != null)
            {
                if (placement.Phase == RobotPlacementController.PlacementPhase.Confirmed) placement.BeginReposition();
                placement.ResetToStart();
            }
            if (guide != null) guide.ResetProgress();
            RestartCount++;
            InProgress = false;
            Restarted?.Invoke();
            Debug.Log($"{LogTag} ready for the next user");
        }
    }
}
