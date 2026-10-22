using UnityEngine;
using UnityEngine.XR;

namespace Factory
{
    /// <summary>
    /// Keyboard stand-in for the machine panel and for the robot's release when the scene runs
    /// in the editor without a headset (the mouse drags and turns the parts through
    /// DesktopMouseInteractor). G drops the selected part where it is, as the robot's open hand
    /// would; C clamps the vise, V releases it, O toggles the doors; 1 / 2 / 3 jump the camera
    /// to the user's spot, the vise and the machine panel. Disabled in device builds and
    /// whenever an XR device is active, so it is harmless to leave in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public class MachineDesktopInput : MonoBehaviour
    {
        [SerializeField] private MachineVise vise;
        [SerializeField] private MachineDoors doors;
        [SerializeField] private ReleasedPartGravity gravity;
        [SerializeField] private DesktopMouseInteractor mouse;
        [Tooltip("Camera positions for keys 1, 2, 3 (world).")]
        [SerializeField] private Vector3[] viewPositions = new Vector3[0];
        [Tooltip("What each view looks at (world), one per position.")]
        [SerializeField] private Vector3[] viewTargets = new Vector3[0];
        [Tooltip("Only run in the editor (never in a headset build).")]
        public bool editorOnly = true;
        [Tooltip("Stay inactive while an XR device is tracking (Link, simulator, headset).")]
        public bool onlyWithoutHeadset = true;
        public bool showHelp = true;

        public MachineVise Vise => vise;
        public MachineDoors Doors => doors;
        public ReleasedPartGravity Gravity => gravity;
        public int ViewCount => Mathf.Min(viewPositions.Length, viewTargets.Length);

        private void Start()
        {
            if (editorOnly && !Application.isEditor) { enabled = false; return; }
            if (onlyWithoutHeadset && XRSettings.isDeviceActive) { enabled = false; return; }
            if (mouse == null) mouse = FindFirstObjectByType<DesktopMouseInteractor>(FindObjectsInactive.Include);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.G)) DropSelected();
            if (Input.GetKeyDown(KeyCode.C)) Clamp();
            if (Input.GetKeyDown(KeyCode.V)) ReleaseVise();
            if (Input.GetKeyDown(KeyCode.O)) ToggleDoors();
            if (Input.GetKeyDown(KeyCode.Alpha1)) JumpToView(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) JumpToView(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) JumpToView(2);
        }

        /// <summary>The robot's hand opens on the selected part: it falls from where it is.</summary>
        public bool DropSelected()
        {
            GameObject part = SelectionManager.Instance != null ? SelectionManager.Instance.SelectedObject : null;
            if (part == null || gravity == null || !gravity.IsEligible(part)) return false;
            if (mouse != null && mouse.Dragged != null && mouse.Dragged.IsChildOf(part.transform)) return false; // still in the "hand"
            gravity.Drop(part);
            return true;
        }

        public void Clamp() { if (vise != null) vise.Clamp(); }
        public void ReleaseVise() { if (vise != null) vise.Release(); }
        public void ToggleDoors() { if (doors != null) doors.Toggle(); }

        public bool JumpToView(int index)
        {
            if (mouse == null || index < 0 || index >= ViewCount) return false;
            mouse.JumpTo(viewPositions[index], viewTargets[index]);
            return true;
        }

        private void OnGUI()
        {
            if (!showHelp) return;
            string help = "Machine: G drop selected part (robot lets go) | C clamp | V release | O doors | 1 user view, 2 vise, 3 panel";
            GUI.Label(new Rect(10, Screen.height - 70, Screen.width - 20, 22), help);
        }
    }
}
