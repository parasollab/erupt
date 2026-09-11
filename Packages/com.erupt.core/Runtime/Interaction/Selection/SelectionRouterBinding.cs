using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// The selection rule: a router select intent becomes a <see cref="SelectionService"/>
    /// selection. Target resolution and UI rejection already happened in the router.
    /// </summary>
    /// <remarks>
    /// Any hand may select (Guidelines Part 3's busy-hand rule); the right-hand-only
    /// filter of the Phase 1 migration is retired. Objects tagged "Selectable" without a
    /// marker are the legacy obstacle set and are given an Obstacle marker on first hit.
    /// Robot kinds are left to the robot binding, which publishes them itself, so the two
    /// bindings never fight over one intent regardless of subscription order.
    /// </remarks>
    public class SelectionRouterBinding : MonoBehaviour
    {
        [SerializeField] private InteractionRouter router;
        [SerializeField] private SelectionService selectionService;

        [Tooltip("Legacy tag that marks selectable environment objects.")]
        [SerializeField] private string legacyTag = "Selectable";

        private void Awake()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (selectionService == null) selectionService = FindFirstObjectByType<SelectionService>();
        }

        private void OnEnable()
        {
            if (router == null || selectionService == null)
            {
                Debug.LogError("SelectionRouterBinding: router or selectionService missing.", this);
                enabled = false;
                return;
            }
            router.Select += OnSelect;
        }

        private void OnDisable()
        {
            if (router != null) router.Select -= OnSelect;
        }

        private void OnSelect(InteractionIntent intent)
        {
            GameObject target = intent.Target;
            if (target == null)
            {
                selectionService.ClearSelection();
                return;
            }

            ISelectable selectable = SelectionService.Resolve(target);
            if (selectable == null && !string.IsNullOrEmpty(legacyTag) && target.CompareTag(legacyTag))
                selectable = SelectableMarker.Ensure(target, SelectionKind.Obstacle);

            if (selectable == null)
            {
                selectionService.ClearSelection();
                return;
            }

            // Robot kinds are the robot binding's to publish (or clear).
            if (selectable.Kind == SelectionKind.RobotLink || selectable.Kind == SelectionKind.EndEffector) return;

            selectionService.Select(selectable);     // re-selecting the current object is a no-op
        }
    }
}
