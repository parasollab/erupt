using TMPro;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// Says why an interaction was refused, where it happened: a short world-space text at the
    /// refusal's world point (or at its subject, or in front of the user when it has neither)
    /// that fades out. Guidelines Part 3: refusals are surfaced spatially at the point of
    /// failure. Driven by <see cref="InteractionRouter.Refused"/> alone; one toast serves every
    /// robot and every plugin, and a newer refusal replaces the one on show.
    /// </summary>
    /// <remarks>
    /// The text is a readout, not a control: it has no raycaster and no collider, so it never
    /// takes a press meant for what is behind it. It is pulled a little toward the viewer, so
    /// a point inside the robot (a joint pivot) does not hide it.
    /// </remarks>
    public class RefusalToast : MonoBehaviour
    {
        [Tooltip("Found in the scene if empty.")]
        [SerializeField] private InteractionRouter router;

        [Header("Timing")]
        [SerializeField, Min(0f)] private float visibleSeconds = 1.8f;
        [SerializeField, Min(0.01f)] private float fadeSeconds = 0.6f;

        [Header("Placement")]
        [Tooltip("How far the text is pulled from the refusal's point toward the viewer, in metres.")]
        [SerializeField, Min(0f)] private float towardViewer = 0.12f;
        [Tooltip("How far above the refusal's point the text sits, in metres.")]
        [SerializeField] private float above = 0.08f;
        [Tooltip("Where a refusal without a place is shown: this far in front of the viewer.")]
        [SerializeField, Min(0.1f)] private float fallbackDistance = 0.7f;

        private static readonly Vector2 Size = new Vector2(640f, 120f);

        private Canvas canvas;
        private CanvasGroup group;
        private TextMeshProUGUI label;
        private InteractionRouter subscribed;
        private float shownAt = float.NegativeInfinity;

        /// <summary>The text on show (the last one, once it has faded).</summary>
        public string Text => label != null ? label.text : "";

        /// <summary>Whether a refusal is on show right now.</summary>
        public bool IsShowing => canvas != null && canvas.gameObject.activeSelf;

        /// <summary>The refusal last shown.</summary>
        public InteractionRefusal Last { get; private set; } = InteractionRefusal.None;

        /// <summary>World position of the text.</summary>
        public Vector3 Position => canvas != null ? canvas.transform.position : transform.position;

        /// <summary>Seconds from appearing to gone.</summary>
        public float Lifetime => visibleSeconds + fadeSeconds;

        /// <summary>Listen to this router's refusals.</summary>
        public void SetRouter(InteractionRouter target)
        {
            router = target;
            if (isActiveAndEnabled) Subscribe();
        }

        private void Awake()
        {
            canvas = UiBuilder.CreateWorldReadout("Refusal", transform, Size);
            group = canvas.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            RectTransform panel = UiBuilder.CreatePanel("Background", canvas.transform, new Color(0.45f, 0.08f, 0.06f, 0.92f));
            UiBuilder.Stretch(panel);
            panel.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;

            label = UiBuilder.CreateLabel("Reason", panel, "", 26f);
            label.enableWordWrapping = true;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            UiBuilder.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(16f, 8f);
            label.rectTransform.offsetMax = new Vector2(-16f, -8f);

            canvas.gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            Subscribe();
        }

        private void OnDisable()
        {
            if (subscribed != null) subscribed.Refused -= OnRefused;
            subscribed = null;
            if (canvas != null) canvas.gameObject.SetActive(false);
        }

        private void Subscribe()
        {
            if (subscribed == router) return;
            if (subscribed != null) subscribed.Refused -= OnRefused;
            subscribed = router;
            if (subscribed != null) subscribed.Refused += OnRefused;
        }

        private void OnRefused(InteractionRefusal refusal) => Show(refusal);

        /// <summary>Show a refusal now, replacing the one on show.</summary>
        public void Show(InteractionRefusal refusal)
        {
            if (!refusal.IsRefused || canvas == null) return;

            Last = refusal;
            label.text = refusal.Reason ?? "";
            shownAt = Time.unscaledTime;
            group.alpha = 1f;
            canvas.gameObject.SetActive(true);
            Place(refusal);
        }

        private void LateUpdate()
        {
            if (!IsShowing) return;

            float age = Time.unscaledTime - shownAt;
            if (age >= visibleSeconds + fadeSeconds)
            {
                canvas.gameObject.SetActive(false);
                return;
            }
            group.alpha = age <= visibleSeconds ? 1f : 1f - (age - visibleSeconds) / fadeSeconds;
            Face();
        }

        private void Place(InteractionRefusal refusal)
        {
            Camera cam = Camera.main;
            Vector3 position;
            if (refusal.WorldPoint != Vector3.zero) position = Lifted(refusal.WorldPoint, cam);
            else if (refusal.Subject != null) position = Lifted(refusal.Subject.position, cam);
            else if (cam != null) position = cam.transform.position + cam.transform.forward * fallbackDistance;
            else position = transform.position;

            canvas.transform.position = position;
            Face();
        }

        private Vector3 Lifted(Vector3 point, Camera cam)
        {
            Vector3 position = point + Vector3.up * above;
            if (cam == null) return position;
            Vector3 toViewer = cam.transform.position - position;
            // Never past the viewer: at most halfway there.
            float pull = Mathf.Min(towardViewer, toViewer.magnitude * 0.5f);
            return toViewer.sqrMagnitude > 1e-8f ? position + toViewer.normalized * pull : position;
        }

        // A world canvas reads correctly when its +Z points away from the viewer.
        private void Face()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 away = canvas.transform.position - cam.transform.position;
            if (away.sqrMagnitude > 1e-8f) canvas.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
