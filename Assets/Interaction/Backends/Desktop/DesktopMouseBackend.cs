using UnityEngine;
using UnityEngine.InputSystem;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Mouse backend for the spectator view and the 2D study condition. Also the only way
    /// to exercise the router without a headset, which makes it the backend CI uses.
    /// </summary>
    public class DesktopMouseBackend : InteractionSourceBehaviour
    {
        [SerializeField] private Camera sourceCamera;
        [SerializeField] private string sourceId = "mouse";

        [Tooltip("Scroll wheel is surfaced as the Y axis, standing in for a thumbstick.")]
        [SerializeField] private float scrollScale = 0.1f;

        private bool wasLeftPressed;
        private bool wasRightPressed;
        private bool isDragging;
        private Ray lastRay;

        public override Modality Modality => Modality.Mouse;

        public override Capability Capabilities =>
            Capability.Ray | Capability.Grip | Capability.Thumbstick | Capability.Precision;

        public override bool IsActive => isActiveAndEnabled && Mouse.current != null && ResolveCamera() != null;

        public override InteractionSample Current => new InteractionSample(
            Modality.Mouse, 1f, Time.unscaledTimeAsDouble,
            new Pose(lastRay.origin, Quaternion.LookRotation(
                lastRay.direction == Vector3.zero ? Vector3.forward : lastRay.direction)),
            sourceId);

        private Camera ResolveCamera() => sourceCamera != null ? sourceCamera : Camera.main;

        private void Update()
        {
            Mouse mouse = Mouse.current;
            Camera cam = ResolveCamera();
            if (mouse == null || cam == null) return;

            lastRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            InteractionSample sample = Current;

            bool leftPressed = mouse.leftButton.isPressed;
            bool rightPressed = mouse.rightButton.isPressed;

            if (leftPressed && !wasLeftPressed)
                EmitRaw(IntentKind.Select, sample, lastRay);

            if (rightPressed && !wasRightPressed)
            {
                EmitRaw(IntentKind.BeginDrag, sample, lastRay);
                isDragging = true;
            }
            else if (isDragging && rightPressed)
            {
                EmitRaw(IntentKind.Drag, sample, lastRay);
            }
            else if (isDragging && !rightPressed)
            {
                EmitRaw(IntentKind.EndDrag, sample, lastRay);
                isDragging = false;
            }

            float scroll = mouse.scroll.ReadValue().y * scrollScale;
            if (!Mathf.Approximately(scroll, 0f))
                EmitRawAxis(sample, lastRay, new Vector2(0f, scroll));

            wasLeftPressed = leftPressed;
            wasRightPressed = rightPressed;
        }

        private void OnDisable()
        {
            if (isDragging)
            {
                EmitRaw(IntentKind.EndDrag, Current, lastRay);
                isDragging = false;
            }
            wasLeftPressed = false;
            wasRightPressed = false;
        }
    }
}
