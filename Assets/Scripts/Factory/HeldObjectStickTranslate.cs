using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Factory
{
    /// <summary>
    /// Thumbstick push/pull of whatever this hand's Near-Far interactor holds: stick forward moves
    /// the held object away along the hand's forward axis, stick back brings it closer, never
    /// rotating it. Works for a near grab as well as a far one: XRI's own manipulation input on
    /// the attach controller only translates in far mode (the anchor offset is zero when the hand
    /// closed on the object directly), which is how first-time users pick up a billet 40 cm away.
    /// Reads the hand's "Translate Manipulation" action from the XRI Default Input Actions asset
    /// (project-wide actions); StickLocomotion already ignores a hand's stick while it is selecting.
    /// </summary>
    public class HeldObjectStickTranslate : MonoBehaviour
    {
        private const string LogTag = "[StickTranslate]";

        [SerializeField, Tooltip("XRI Default Input Actions asset (the same one StickLocomotion reads).")]
        private InputActionAsset inputActions;
        [SerializeField] private string actionMap = "XRI Right Interaction";
        [SerializeField] private string actionName = "Translate Manipulation";
        [SerializeField, Tooltip("Found in the children when empty.")] private NearFarInteractor interactor;
        [SerializeField, Tooltip("Metres per second at full stick deflection.")] private float speed = 0.8f;
        [SerializeField, Range(0f, 0.9f)] private float deadzone = 0.2f;
        [SerializeField, Tooltip("The object is never pulled closer to the hand than this (metres).")] private float minDistance = 0.08f;
        [SerializeField, Tooltip("...nor pushed farther than this.")] private float maxDistance = 3f;

        private InputAction _stick;
        private bool _loggedFirstMove;

        public NearFarInteractor Interactor => interactor;
        public InputAction Action => _stick;
        /// <summary>Metres moved so far (probes).</summary>
        public float MovedMetres { get; private set; }

        private void Awake()
        {
            if (interactor == null) interactor = GetComponentInChildren<NearFarInteractor>(true);
            if (inputActions == null)
            {
                Debug.LogError($"{LogTag} inputActions is not assigned on '{name}'; disabled.");
                enabled = false;
                return;
            }
            _stick = inputActions.FindActionMap(actionMap)?.FindAction(actionName);
            if (_stick == null) Debug.LogError($"{LogTag} action '{actionMap}/{actionName}' not found.");
        }

        private void OnEnable() => _stick?.Enable();

        private void Update()
        {
            if (_stick == null || interactor == null || !interactor.isActiveAndEnabled || !interactor.hasSelection) return;
            float y = _stick.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) < deadzone) return;
            // Re-scale past the deadzone so a small deflection is a slow move, not a jump.
            y = Mathf.Sign(y) * Mathf.InverseLerp(deadzone, 1f, Mathf.Abs(y));
            IInteractionAttachController attach = interactor.interactionAttachController;
            if (attach == null) return;
            Transform anchor = attach.GetOrCreateAnchorTransform();
            Transform hand = interactor.transform;
            Vector3 toAnchor = anchor.position - hand.position;
            float distance = toAnchor.magnitude;
            float step = y * speed * Time.deltaTime;
            if (step < 0f && distance + step < minDistance) step = Mathf.Min(0f, minDistance - distance);
            if (step > 0f && distance + step > maxDistance) step = Mathf.Max(0f, maxDistance - distance);
            if (Mathf.Approximately(step, 0f)) return;
            // Along the hand's forward (the ray); a near grab has no offset yet, so there is no ray direction to reuse.
            attach.ApplyLocalPositionOffset(new Vector3(0f, 0f, step));
            MovedMetres += Mathf.Abs(step);
            if (!_loggedFirstMove)
            {
                _loggedFirstMove = true;
                Debug.Log($"{LogTag} '{name}' stick moves the held object ({interactor.interactablesSelected[0].transform.name}) along the ray");
            }
        }
    }
}
