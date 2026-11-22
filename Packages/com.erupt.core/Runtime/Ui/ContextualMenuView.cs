using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// Renders a <see cref="ContextualMenuModel"/> in the world, next to the selection.
    /// </summary>
    /// <remarks>
    /// Guidelines Part 2: tier 2 "appears attached to the currently selected object,
    /// disappears on deselect". The menu is positioned from the target's renderer bounds
    /// so it clears the object rather than intersecting it.
    ///
    /// The canvas is as tall as its header and buttons and stands on its lower edge, just
    /// above the object: nothing of it (and nothing of the collider that takes presses for
    /// it) hangs over the object, so the object can still be pointed at and dragged.
    ///
    /// The model decides which verbs are listed (<see cref="ContextualMenuModel.ListsUnavailableVerbs"/>).
    /// A listed verb that cannot run is greyed and non-interactive, which is what keeps the
    /// Part 8 design-review count of interactive elements honest.
    /// </remarks>
    public class ContextualMenuView : MonoBehaviour
    {
        [SerializeField] private SelectionService selectionService;
        [Tooltip("Refusals from the menu (a verb that cannot run) are reported here, like any other. Found in the scene if empty.")]
        [SerializeField] private InteractionRouter router;
        [SerializeField] private Vector2 buttonSize = new(300f, 64f);
        [Tooltip("Gap between the top of the selected object and the lower edge of the menu, in metres.")]
        [SerializeField] private float verticalOffset = 0.05f;

        [Tooltip("How far the menu is pulled from above the object toward the viewer, in metres, so that what stands next to the object " +
                 "(the robot's own links, around its end effector) does not come in front of it. Never more than a third of the way.")]
        [SerializeField, Min(0f)] private float towardViewer = 0.2f;

        /// <summary>
        /// Zero (the default) draws the menu at its own size, as a thing in the room. A
        /// distance in metres draws a menu that is further from the viewer than that larger, so
        /// that it reads as it would from that distance: a desktop view cannot step closer the
        /// way a person in a headset does. Set by the backend that drives the view.
        /// </summary>
        public float ReadableDistance { get; set; }

        private const float Width = 320f;
        private const float HeaderHeight = 36f;
        private const float Spacing = 6f;

        private readonly List<Button> buttons = new();
        private GameObject column;
        private Transform cameraTransform;

        public ContextualMenuModel Model { get; } = new();
        public Canvas Canvas { get; private set; }

        /// <summary>Buttons the user can actually press. Used by the Part 8 element count.</summary>
        public int InteractiveElementCount
        {
            get
            {
                int n = 0;
                foreach (var b in buttons) if (b.interactable) n++;
                return n;
            }
        }

        private void Awake()
        {
            Canvas = UiBuilder.CreateWorldCanvas("Tier2", transform, new Vector2(Width, HeaderHeight));
            // The menu stands on its lower edge and grows upwards with its verbs.
            Canvas.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);
            UiBuilder.ResizeWorldCanvas(Canvas, new Vector2(Width, HeaderHeight));
            Model.Changed += Rebuild;

            if (selectionService == null) selectionService = FindFirstObjectByType<SelectionService>();
            Model.FollowSelection(selectionService);

            Rebuild();
        }

        private void OnDestroy()
        {
            Model.Changed -= Rebuild;
            Model.StopFollowing(selectionService);
        }

        /// <summary>Wire a verb to its implementation. Unbound verbs render disabled.</summary>
        public void Bind(string verbId, System.Action<ISelectable> handler) => Model.Bind(verbId, handler);

        /// <summary>The button of a listed verb, or null (the verb is not on the menu, or no menu is showing).</summary>
        public Button ButtonFor(string verbId)
        {
            foreach (Button button in buttons)
                if (button != null && button.name == verbId) return button;
            return null;
        }

        private void Rebuild()
        {
            // The whole column goes, header included: a rebuild used to leave the previous
            // column (and its header) under the canvas for good.
            if (Canvas == null) return;   // the view is on its way out
            if (column != null)
            {
                // Destroy takes effect at the end of the frame; until then the old column is
                // inactive, so it is neither drawn nor pressed next to the new one.
                column.SetActive(false);
                Destroy(column);
                column = null;
            }
            buttons.Clear();

            // A selection whose object has just been destroyed (its scene is being unloaded)
            // has no menu; the selection service drops it a moment later.
            bool visible = Model.IsVisible && !SelectionService.IsDestroyed(Model.Target);
            Canvas.gameObject.SetActive(visible);
            if (!visible) return;

            int count = Model.Verbs.Count;
            UiBuilder.ResizeWorldCanvas(Canvas, new Vector2(Width, HeaderHeight + count * (buttonSize.y + Spacing)));

            var layout = UiBuilder.CreateColumn("Verbs", Canvas.transform, Spacing);
            column = layout.gameObject;
            UiBuilder.Stretch(layout.GetComponent<RectTransform>());
            // A dark plate behind the header and the gaps, as tier 3 has: the header is white
            // text and would be lost against a bright scene.
            column.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.13f, 0.88f);

            var header = UiBuilder.CreateLabel("Header", column.transform, Model.Target.DisplayName, 24f);
            header.gameObject.AddComponent<LayoutElement>().preferredHeight = HeaderHeight;

            foreach (var verb in Model.Verbs)
            {
                Verb captured = verb;
                Button button = UiBuilder.CreateButton(verb.Id, column.transform, verb.Label, buttonSize,
                    () => Invoke(captured));

                UiBuilder.SetInteractable(button, Model.CanInvoke(verb));
                buttons.Add(button);
            }
        }

        /// <summary>Use this router for the menu's refusals.</summary>
        public void SetRouter(InteractionRouter target) => router = target;

        /// <summary>
        /// Run a verb on the selection, as pressing its button does. A verb that cannot run is
        /// a refusal on the interaction router, where the user-facing feedback listens.
        /// </summary>
        public InteractionRefusal Invoke(Verb verb)
        {
            InteractionRefusal refusal = Model.Invoke(verb);
            if (refusal.IsRefused)
            {
                if (router == null) router = FindFirstObjectByType<InteractionRouter>();
                if (router != null) router.ReportRefusal(refusal);
            }
            return refusal;
        }

        private void LateUpdate()
        {
            if (!Canvas.gameObject.activeSelf || Model.Target == null) return;
            // The selection clears itself when its object is destroyed; until it has, there is
            // nothing to follow.
            if (SelectionService.IsDestroyed(Model.Target)) return;

            GameObject target = Model.Target.GameObject;
            if (target == null) return;

            Vector3 anchor = AnchorFor(target);
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform != null)
            {
                // Along the line of sight: the menu stays where it is in the view and comes
                // in front of what is beside the object.
                Vector3 toViewer = cameraTransform.position - anchor;
                float distance = toViewer.magnitude;
                if (distance > 1e-3f) anchor += toViewer / distance * Mathf.Min(towardViewer, distance / 3f);
            }
            float scale = 1f;
            if (cameraTransform != null)
            {
                Quaternion facing = Quaternion.LookRotation(cameraTransform.forward, Vector3.up);
                if (ReadableDistance > 0f)
                    scale = Mathf.Max(1f, Vector3.Distance(cameraTransform.position, anchor) / ReadableDistance);
                if (ClearsLineOfSight) BringInFront(ref anchor, ref scale, facing);
                Canvas.transform.SetPositionAndRotation(anchor, facing);
            }
            else
            {
                Canvas.transform.position = anchor;
            }
            Canvas.transform.localScale = Vector3.one * (scale / UiBuilder.PixelsPerUnit);
        }

        /// <summary>
        /// When set, a menu that has something solid between it and the viewer (another
        /// robot's arm, an obstacle) is brought in front of it along the line of sight and
        /// drawn smaller by the same factor, so on a flat screen it stays where it was, at the
        /// size it was, and is never behind anything. Off by default: in a headset the menu is
        /// a thing in the room, and one that changed its depth would be felt. Set by the
        /// backend that drives the view.
        /// </summary>
        public bool ClearsLineOfSight { get; set; }

        // How far in front of whatever is in the way the menu is put, in metres.
        private const float InFrontBy = 0.05f;

        private void BringInFront(ref Vector3 anchor, ref float scale, Quaternion facing)
        {
            Vector3 eye = cameraTransform.position;
            Vector3 forward = cameraTransform.forward;
            float depth = Vector3.Dot(anchor - eye, forward);
            if (depth <= 0.2f) return;

            // The corners and the middle of the menu as it would stand.
            Vector2 size = Canvas.GetComponent<RectTransform>().sizeDelta * (scale / UiBuilder.PixelsPerUnit);
            Vector3 right = facing * Vector3.right * (size.x * 0.5f);
            Vector3 up = facing * Vector3.up;
            float nearest = depth;
            for (int i = 0; i < 5; i++)
            {
                Vector3 point = i switch
                {
                    0 => anchor + up * (size.y * 0.5f),
                    1 => anchor - right,
                    2 => anchor + right,
                    3 => anchor - right + up * size.y,
                    _ => anchor + right + up * size.y
                };
                Vector3 to = point - eye;
                float length = to.magnitude;
                if (length < 1e-3f) continue;
                if (Physics.Raycast(eye, to / length, out RaycastHit hit, length, ~0, QueryTriggerInteraction.Ignore))
                    nearest = Mathf.Min(nearest, Vector3.Dot(hit.point - eye, forward));
            }
            if (nearest >= depth) return;

            float inFront = Mathf.Max(nearest - InFrontBy, 0.2f);
            float factor = inFront / depth;
            anchor = eye + (anchor - eye) * factor;
            scale *= factor;
        }

        // Clear the top of the object's bounds, so the menu does not sit inside it. An object
        // that is drawn by a child (a handle, a plan's sphere) is measured by that child.
        private Vector3 AnchorFor(GameObject target)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null) renderer = target.GetComponentInChildren<Renderer>();
            return renderer != null
                ? renderer.bounds.center + Vector3.up * (renderer.bounds.extents.y + verticalOffset)
                : target.transform.position + Vector3.up * verticalOffset;
        }
    }
}
