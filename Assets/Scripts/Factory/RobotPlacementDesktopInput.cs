using UnityEngine;
using UnityEngine.XR;

namespace Factory
{
    /// <summary>
    /// Keyboard stand-in for the placement panel when the scene runs in the editor without a
    /// headset (the mouse drags the handle through DesktopMouseInteractor). Arrow keys nudge,
    /// comma / period turn, Return confirms, R resets, P goes back to placing. Disabled in
    /// device builds and whenever an XR device is active, so it is harmless to leave in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotPlacementDesktopInput : MonoBehaviour
    {
        [SerializeField] private RobotPlacementController controller;
        [Tooltip("Only run in the editor (never in a headset build).")]
        public bool editorOnly = true;
        [Tooltip("Stay inactive while an XR device is tracking (Link, simulator, headset).")]
        public bool onlyWithoutHeadset = true;
        [Tooltip("Seconds between repeated nudges while an arrow key is held.")]
        public float repeatSeconds = 0.12f;
        public bool showHelp = true;

        private float _nextRepeat;

        private void Start()
        {
            if (editorOnly && !Application.isEditor) { enabled = false; return; }
            if (onlyWithoutHeadset && XRSettings.isDeviceActive) { enabled = false; return; }
            if (controller == null)
            {
                Debug.LogWarning("RobotPlacementDesktopInput: no controller assigned; disabled.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) controller.Confirm();
            if (Input.GetKeyDown(KeyCode.P)) controller.BeginReposition();
            if (Input.GetKeyDown(KeyCode.R)) controller.ResetToStart();
            if (Input.GetKeyDown(KeyCode.Comma)) controller.RotateStep(-1f);
            if (Input.GetKeyDown(KeyCode.Period)) controller.RotateStep(1f);

            float dx = (Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            float dz = (Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
            bool pressedNow = Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.LeftArrow)
                              || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow);
            if ((dx != 0f || dz != 0f) && (pressedNow || Time.time >= _nextRepeat))
            {
                controller.NudgeStep(dx, dz);
                _nextRepeat = Time.time + repeatSeconds;
            }
        }

        private void OnGUI()
        {
            if (!showHelp || controller == null) return;
            string help = controller.Phase == RobotPlacementController.PlacementPhase.Placing
                ? "Placement: left-drag the ring | arrows nudge | , . turn | R start over | Return confirm"
                : "Placed: P repositions the robot (when no plan is running)";
            GUI.Label(new Rect(10, Screen.height - 48, Screen.width - 20, 22), help);
        }
    }
}
