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
        [SerializeField, Tooltip("Snapped to again after the restart, so the next user starts at the user's spot (headset only).")]
        private SpawnHuman spawn;
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
                if (client.Busy) Debug.LogWarning($"{LogTag} the goal did not end within {cancelTimeout} s; restarting anyway");
                _ = cancel;
            }
            // The last plan's solutions would otherwise survive the restart and the guide would
            // jump from the placement straight to "Execute".
            if (client != null) client.ForgetTask();
            // A restart in the middle of an execution leaves the billet attached to the gripper in
            // MoveIt (the cancel lands between the attach and the detach), and a hand that "holds"
            // something fails every grasp IK afterwards: the second run could never plan. Let go of
            // it here and in the planning scene; everything is re-ADDed at its Unity pose on Confirm.
            var attachedListener = FindFirstObjectByType<AttachedCollisionObjectListener>();
            int released = attachedListener != null ? attachedListener.ReleaseAll() : 0;
            if (released > 0) Debug.Log($"{LogTag} {released} object(s) taken out of the robot's hand");
            CollisionObjectPublisher.DetachAllFromRobot(ok => { if (!ok) Debug.LogWarning($"{LogTag} MoveIt may still hold a part in the gripper; planning could fail until the stack is restarted"); });
            foreach (var pub in FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                pub.ForceRepublish();
            selection?.ClearSelection();
            if (vise != null && vise.IsClosed) vise.SetImmediate(true);
            if (doors != null && doors.State != MachineDoors.DoorState.Closed) doors.Close();
            if (parts != null)
            {
                // Forced: a part the robot let go of seconds ago is still "settling" and would stay in the vise.
                int returned = parts.ReturnAll(force: true);
                if (returned > 0) Debug.Log($"{LogTag} {returned} billet(s) back on the stand");
            }
            if (vise != null) vise.ScanPresence();
            if (trajectoryPlayer != null && trajectoryPlayer.IsMirroring) trajectoryPlayer.StopLiveMirror(); // execution feedback may have restarted it
            if (placement != null)
            {
                // Forced: the restart has already stopped everything the controller would wait for; a
                // refused reposition left the robot where the previous user put it.
                placement.ForceReposition();
                placement.ResetToStart();
                if (placement.Phase != RobotPlacementController.PlacementPhase.Placing || Vector3.Distance(placement.Handle.position, placement.StartHandlePose.position) > 1e-3f)
                    Debug.LogWarning($"{LogTag} robot not back at its start pose (phase {placement.Phase}, handle grabbed {placement.HandleGrabbed})");
            }
            if (guide != null) guide.ResetProgress();
            if (spawn != null && XRSettings.isDeviceActive) spawn.MoveToSpawnPoint();
            RestartCount++;
            InProgress = false;
            Restarted?.Invoke();
            Debug.Log($"{LogTag} ready for the next user");
        }
    }
}
