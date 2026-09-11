using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Erupt.Ui;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Gives every tier canvas the raycaster XRI's ray interactors need. The UI assembly
    /// builds canvases with a plain GraphicRaycaster (enough for mouse and tests); this
    /// backend component listens for them and adds <see cref="TrackedDeviceGraphicRaycaster"/>
    /// so controller rays can click. Lives on the ERUPT root.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class XrUiRaycastInstaller : MonoBehaviour
    {
        private void OnEnable()
        {
            UiBuilder.CanvasCreated += Install;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (canvas.renderMode == RenderMode.WorldSpace) Install(canvas);
        }

        private void OnDisable() => UiBuilder.CanvasCreated -= Install;

        private static void Install(Canvas canvas)
        {
            if (canvas == null) return;
            if (canvas.worldCamera == null) canvas.worldCamera = Camera.main;
            if (canvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null) return;
            canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            Debug.Log($"[XrUiRaycastInstaller] Tracked-device raycaster on '{canvas.name}' (camera: {(canvas.worldCamera ? canvas.worldCamera.name : "none")})");
        }
    }
}
