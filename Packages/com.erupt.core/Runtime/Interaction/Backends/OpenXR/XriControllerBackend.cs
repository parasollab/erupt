using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// OpenXR controller backend: trigger selects, grip drags, thumbstick Y jogs.
    /// </summary>
    /// <remarks>
    /// This is the only class in the migrated path that touches input actions. It applies
    /// no deadzone and no smoothing — both are Router policy per Guidelines Part 3, and
    /// the Controller profile carries the 0.18 deadzone the old script applied inline so
    /// the feel is unchanged.
    /// </remarks>
    public class XriControllerBackend : InteractionSourceBehaviour, IHapticOutput
    {
        [Header("Identity")]
        [Tooltip("Stable id carried on every sample. Use \"left\" or \"right\".")]
        [SerializeField] private string sourceId = "right";

        [Header("Pose")]
        [Tooltip("Transform the ray originates from. Defaults to this transform.")]
        [SerializeField] private Transform rayOrigin;

        [Header("Input Actions")]
        [SerializeField] private InputActionProperty selectAction;
        [SerializeField] private InputActionProperty gripAction;
        [SerializeField] private InputActionProperty axisAction;
        [SerializeField] private InputActionProperty activateAction;

        [Header("Haptics")]
        [Tooltip("This controller's haptic output. Empty = the \"Haptic Device\" action of the map the select action's map " +
                 "extends (\"XRI Right Interaction\" -> \"XRI Right\"), which is how the XRI default input actions are laid out.")]
        [SerializeField] private InputActionProperty hapticAction;

        private HapticControlActionManager hapticManager;
        private InputAction resolvedHapticAction;
        private bool hapticActionResolved;

        private bool wasSelectPressed;
        private bool wasGripPressed;
        private bool isDragging;

        public override Modality Modality => Modality.Controller;

        public override Capability Capabilities =>
            Capability.Ray | Capability.Grip | Capability.Thumbstick | Capability.Haptics | Capability.Precision;

        public override InteractionSample Current => BuildSample();

        private Transform Origin => rayOrigin != null ? rayOrigin : transform;

        /// <summary>The id on every sample this controller emits ("left" or "right").</summary>
        public string SourceId => sourceId;

        private void Awake()
        {
            // A refusal caused by this controller is answered on this controller.
            if (GetComponent<RefusalHaptics>() == null) gameObject.AddComponent<RefusalHaptics>();
        }

        /// <summary>Pulse this controller. Does nothing when it has no haptic action.</summary>
        public void SendHapticImpulse(float amplitude, float seconds)
        {
            InputAction action = HapticAction();
            if (action == null) return;
            hapticManager ??= new HapticControlActionManager();
            hapticManager.GetChannelGroup(action)?.GetChannel()?.SendHapticImpulse(amplitude, seconds);
        }

        private InputAction HapticAction()
        {
            if (hapticActionResolved) return resolvedHapticAction;
            hapticActionResolved = true;

            if (hapticAction.reference != null || (hapticAction.action != null && hapticAction.action.bindings.Count > 0))
                return resolvedHapticAction = hapticAction.action;

            // The XRI default actions keep a controller's haptic output in the map its
            // interaction map is named after: "XRI Right Interaction" next to "XRI Right".
            InputActionMap map = selectAction.action?.actionMap;
            InputActionAsset asset = map?.asset;
            if (asset == null) return null;
            foreach (InputActionMap candidate in asset.actionMaps)
            {
                if (candidate == map || !map.name.StartsWith(candidate.name, System.StringComparison.Ordinal)) continue;
                InputAction found = candidate.FindAction("Haptic Device") ?? candidate.FindAction("Haptic");
                if (found != null) return resolvedHapticAction = found;
            }
            return null;
        }

        private void OnEnable()
        {
            EnableAction(selectAction);
            EnableAction(gripAction);
            EnableAction(axisAction);
            EnableAction(activateAction);
        }

        private void OnDisable()
        {
            DisableAction(selectAction);
            DisableAction(gripAction);
            DisableAction(axisAction);
            DisableAction(activateAction);

            // A disabled backend must not leave an interactable mid-drag.
            if (isDragging)
            {
                EmitRaw(IntentKind.EndDrag, BuildSample(), CurrentRay());
                isDragging = false;
            }

            wasSelectPressed = false;
            wasGripPressed = false;
        }

        private void Update()
        {
            InteractionSample sample = BuildSample();
            Ray ray = CurrentRay();

            bool selectPressed = ReadPressed(selectAction);
            bool gripPressed = ReadPressed(gripAction);

            if (selectPressed && !wasSelectPressed)
                EmitRaw(IntentKind.Select, sample, ray);

            if (gripPressed && !wasGripPressed)
            {
                EmitRaw(IntentKind.BeginDrag, sample, ray);
                isDragging = true;
            }
            else if (isDragging && gripPressed)
            {
                EmitRaw(IntentKind.Drag, sample, ray);
            }
            else if (isDragging && !gripPressed)
            {
                EmitRaw(IntentKind.EndDrag, sample, ray);
                isDragging = false;
            }

            if (ReadPressedThisFrame(activateAction))
                EmitRaw(IntentKind.Activate, sample, ray);

            // Raw axis. The deadzone the old script applied here now lives in the Router.
            Vector2 axis = ReadAxis(axisAction);
            if (axis != Vector2.zero)
                EmitRawAxis(sample, ray, axis);

            wasSelectPressed = selectPressed;
            wasGripPressed = gripPressed;
        }

        private InteractionSample BuildSample()
        {
            Transform origin = Origin;
            var pose = new Pose(origin.position, origin.rotation);
            // Controllers are either tracked or absent; there is no partial confidence.
            return new InteractionSample(Modality.Controller, 1f, Time.unscaledTimeAsDouble, pose, sourceId);
        }

        private Ray CurrentRay()
        {
            Transform origin = Origin;
            return new Ray(origin.position, origin.forward);
        }

        private static void EnableAction(InputActionProperty property) => property.action?.Enable();
        private static void DisableAction(InputActionProperty property) => property.action?.Disable();

        // IsPressed uses the actuation threshold and works for both the Button-typed
        // Activate action and the Value-typed Select (grip) action, whose
        // expectedControlType is empty in the XRI default asset.
        private static bool ReadPressed(InputActionProperty property) =>
            property.action != null && property.action.IsPressed();

        private static bool ReadPressedThisFrame(InputActionProperty property) =>
            property.action != null && property.action.WasPressedThisFrame();

        private static Vector2 ReadAxis(InputActionProperty property) =>
            property.action != null ? property.action.ReadValue<Vector2>() : Vector2.zero;
    }
}
