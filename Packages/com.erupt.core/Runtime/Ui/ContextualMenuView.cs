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
    /// Unavailable verbs are rendered greyed rather than omitted, so the menu shows the
    /// intended vocabulary. They are non-interactive, which is what keeps the Part 8
    /// design-review count of interactive elements honest.
    /// </remarks>
    public class ContextualMenuView : MonoBehaviour
    {
        [SerializeField] private SelectionService selectionService;
        [SerializeField] private Vector2 buttonSize = new(300f, 64f);
        [SerializeField] private float verticalOffset = 0.15f;

        private readonly List<Button> buttons = new();
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
            Canvas = UiBuilder.CreateWorldCanvas("Tier2", transform, new Vector2(320f, 480f));
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
        public void Bind(string verbId, System.Action<ISelectable> handler)
        {
            Model.Bind(verbId, handler);
            Rebuild();
        }

        private void Rebuild()
        {
            foreach (var button in buttons)
                if (button != null) Destroy(button.gameObject);
            buttons.Clear();

            bool visible = Model.IsVisible;
            Canvas.gameObject.SetActive(visible);
            if (!visible) return;

            var column = UiBuilder.CreateColumn("Verbs", Canvas.transform);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());

            UiBuilder.CreateLabel("Header", column.transform, Model.Target.DisplayName, 24f);

            foreach (var verb in Model.Verbs)
            {
                Verb captured = verb;
                Button button = UiBuilder.CreateButton(verb.Id, column.transform, verb.Label, buttonSize,
                    () => Model.Invoke(captured));

                UiBuilder.SetInteractable(button, Model.CanInvoke(verb));
                buttons.Add(button);
            }
        }

        private void LateUpdate()
        {
            if (!Canvas.gameObject.activeSelf || Model.Target == null) return;

            GameObject target = Model.Target.GameObject;
            if (target == null) return;

            Canvas.transform.position = AnchorFor(target);

            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform != null)
                Canvas.transform.LookAt(Canvas.transform.position + cameraTransform.forward);
        }

        // Clear the top of the object's bounds, so the menu does not sit inside it.
        private Vector3 AnchorFor(GameObject target)
        {
            var renderer = target.GetComponent<Renderer>();
            return renderer != null
                ? renderer.bounds.center + Vector3.up * (renderer.bounds.extents.y + verticalOffset)
                : target.transform.position + Vector3.up * verticalOffset;
        }
    }
}
