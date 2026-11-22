using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Mouse and keyboard backend, for working in the editor without a headset: the left
    /// button selects, the right button held drags, the scroll wheel is the axis (or, during
    /// a drag, pushes the dragged point away and pulls it closer), and one key is the
    /// Activate intent that opens and closes the tier 3 panel. It goes through the same
    /// router and intents as the headset backends.
    /// </summary>
    /// <remarks>
    /// The backend sits on the ERUPT root, so every scene has it. <see cref="DesktopControls"/>
    /// decides whether it runs: it is suppressed while an XR head device drives the view, so it
    /// never puts a second ray next to the controllers'.
    /// </remarks>
    public class DesktopMouseBackend : InteractionSourceBehaviour
    {
        // What the Input System reports for one notch of a mouse wheel.
        private const float ScrollPerNotch = 120f;

        [SerializeField] private Camera sourceCamera;
        [SerializeField] private string sourceId = "mouse";

        [Tooltip("Axis value for one notch of the scroll wheel (the wheel stands in for a thumbstick: it jogs the selected joint).")]
        [SerializeField] private float scrollScale = 3f;

        [Tooltip("While the right button drags, one notch of the scroll wheel moves the dragged point this far along the ray, in metres.")]
        [SerializeField] private float dragDepthPerNotch = 0.05f;

        [Tooltip("Opens and closes the tier 3 panel (the Activate intent, which the controller's Menu button raises).")]
        [SerializeField] private Key panelKey = Key.Tab;

        private bool wasLeftPressed;
        private bool wasRightPressed;
        private bool isDragging;
        private float dragDepth;
        private Ray lastRay;
        private PointerEventData pointerProbe;
        private readonly List<RaycastResult> uiHits = new();

        public override Modality Modality => Modality.Mouse;

        public override Capability Capabilities =>
            Capability.Ray | Capability.Grip | Capability.Thumbstick | Capability.Precision;

        /// <summary>
        /// True while something else drives the view and the pointers (an XR head device):
        /// the backend then emits nothing and reports itself inactive.
        /// </summary>
        public bool Suppressed
        {
            get => suppressed;
            set
            {
                if (suppressed == value) return;
                suppressed = value;
                if (suppressed) Release();
            }
        }
        private bool suppressed;

        /// <summary>The key that raises the Activate intent (opens and closes the tier 3 panel).</summary>
        public Key PanelKey => panelKey;

        /// <summary>The id on every sample this backend emits.</summary>
        public string SourceId => sourceId;

        public override bool IsActive => isActiveAndEnabled && !suppressed && Mouse.current != null && ResolveCamera() != null;

        public override InteractionSample Current => new InteractionSample(
            Modality.Mouse, 1f, Time.unscaledTimeAsDouble,
            new Pose(lastRay.origin, Quaternion.LookRotation(
                lastRay.direction == Vector3.zero ? Vector3.forward : lastRay.direction)),
            sourceId);

        private Camera ResolveCamera() => sourceCamera != null ? sourceCamera : Camera.main;

        private void Update()
        {
            if (suppressed) return;
            Mouse mouse = Mouse.current;
            Camera cam = ResolveCamera();
            if (mouse == null || cam == null) return;

            Vector2 screen = mouse.position.ReadValue();
            Ray pointer = cam.ScreenPointToRay(screen);
            float notches = mouse.scroll.ReadValue().y / ScrollPerNotch;

            bool leftPressed = mouse.leftButton.isPressed;
            bool rightPressed = mouse.rightButton.isPressed;

            // A dragged point keeps its distance along the ray; the wheel changes that distance
            // by moving the ray's origin along it.
            if (isDragging && rightPressed) dragDepth += notches * dragDepthPerNotch;
            lastRay = isDragging ? new Ray(pointer.origin + pointer.direction * dragDepth, pointer.direction) : pointer;
            InteractionSample sample = Current;

            // A press on a menu belongs to the menu: the UI module clicks the button, and a
            // world Select with the same press would change the selection under it.
            if (leftPressed && !wasLeftPressed && !IsOverUi(screen))
                EmitRaw(IntentKind.Select, sample, lastRay);

            if (rightPressed && !wasRightPressed)
            {
                dragDepth = 0f;
                lastRay = pointer;
                sample = Current;
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
                dragDepth = 0f;
            }

            if (!isDragging && !Mathf.Approximately(notches, 0f))
                EmitRawAxis(sample, lastRay, new Vector2(0f, notches * scrollScale));

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && panelKey != Key.None && keyboard[panelKey].wasPressedThisFrame)
                EmitRaw(IntentKind.Activate, sample, lastRay);

            wasLeftPressed = leftPressed;
            wasRightPressed = rightPressed;
        }

        /// <summary>Whether a screen point is over a UI graphic that takes pointer events (a menu button, a panel).</summary>
        public bool IsOverUi(Vector2 screenPoint)
        {
            EventSystem events = EventSystem.current;
            if (events == null) return false;
            pointerProbe ??= new PointerEventData(events);
            pointerProbe.Reset();
            pointerProbe.position = screenPoint;
            uiHits.Clear();
            events.RaycastAll(pointerProbe, uiHits);
            return uiHits.Count > 0;
        }

        private void OnDisable() => Release();

        // Whatever stops the backend must not leave an interactable mid-drag.
        private void Release()
        {
            if (isDragging)
            {
                EmitRaw(IntentKind.EndDrag, Current, lastRay);
                isDragging = false;
            }
            dragDepth = 0f;
            wasLeftPressed = false;
            wasRightPressed = false;
        }
    }
}
