using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.Environment
{
    /// <summary>
    /// Moves the selected obstacle with a drag intent, for an input source that has no XR
    /// interactor of its own (the mouse). Obstacles are XR grab interactables; this is not a
    /// second way of holding them, only the router's drag applied to the same objects under
    /// the same rule: an obstacle moves when it is the selection and its grab interactable is
    /// enabled (it is not while a robot carries it).
    /// </summary>
    /// <remarks>
    /// A controller's grip raises the drag intent and grabs through XRI with the same squeeze.
    /// The moment an XR interactor holds the obstacle, this binding lets go and leaves it to
    /// XRI, so a headset session behaves exactly as it did.
    /// </remarks>
    public class ObstacleDragBinding : MonoBehaviour
    {
        [SerializeField] private InteractionRouter router;
        [SerializeField] private SelectionService selection;
        [Tooltip("Moves are recorded on this rig's undo stack. Found in the scene if empty.")]
        [SerializeField] private TierUiRig rig;

        private sealed class Drag
        {
            public Transform target;
            public XRGrabInteractable grab;
            public float distance;
            public Vector3 offset;
            public Vector3 startPosition, startScale;
            public Quaternion startRotation;
        }

        private readonly Dictionary<string, Drag> drags = new();
        private InteractionRouter subscribed;

        /// <summary>Whether an input source is moving an obstacle through this binding.</summary>
        public bool IsDragging(string sourceId) => drags.ContainsKey(sourceId ?? "default");

        /// <summary>Use this router (found in the scene when none is given).</summary>
        public void SetRouter(InteractionRouter target)
        {
            router = target;
            if (isActiveAndEnabled) Subscribe();
        }

        private void OnEnable() => Subscribe();

        private void Start()
        {
            // The router may wake after this component.
            Subscribe();
        }

        private void OnDisable()
        {
            if (subscribed != null)
            {
                subscribed.BeginDrag -= OnBeginDrag;
                subscribed.Drag -= OnDrag;
                subscribed.EndDrag -= OnEndDrag;
                subscribed = null;
            }
            drags.Clear();
        }

        private void Subscribe()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (router == null || ReferenceEquals(subscribed, router)) return;
            if (subscribed != null)
            {
                subscribed.BeginDrag -= OnBeginDrag;
                subscribed.Drag -= OnDrag;
                subscribed.EndDrag -= OnEndDrag;
            }
            subscribed = router;
            router.BeginDrag += OnBeginDrag;
            router.Drag += OnDrag;
            router.EndDrag += OnEndDrag;
        }

        private static string SourceOf(InteractionIntent intent) => intent.Sample.SourceId ?? "default";

        private void OnBeginDrag(InteractionIntent intent)
        {
            if (!intent.HasHit) return;
            string source = SourceOf(intent);
            if (drags.ContainsKey(source)) return;

            ISelectable hit = SelectionService.Resolve(intent.Target);
            if (hit == null || hit.Kind != SelectionKind.Obstacle || hit.GameObject == null) return;
            // Like a grab: only what is selected moves.
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (selection == null || !ReferenceEquals(selection.Current, hit)) return;

            var grab = hit.GameObject.GetComponent<XRGrabInteractable>();
            if (grab == null || !grab.enabled || grab.isSelected) return;

            Transform target = hit.GameObject.transform;
            drags[source] = new Drag
            {
                target = target,
                grab = grab,
                distance = intent.Hit.distance,
                offset = target.position - intent.Ray.GetPoint(intent.Hit.distance),
                startPosition = target.position,
                startRotation = target.rotation,
                startScale = target.localScale
            };
        }

        private void OnDrag(InteractionIntent intent)
        {
            string source = SourceOf(intent);
            if (!drags.TryGetValue(source, out Drag drag)) return;

            // Gone, locked (a robot picked it up), or an XR interactor has it: not ours any more.
            if (drag.target == null || drag.grab == null || !drag.grab.enabled || drag.grab.isSelected)
            {
                drags.Remove(source);
                return;
            }
            drag.target.position = intent.Ray.GetPoint(drag.distance) + drag.offset;
        }

        private void OnEndDrag(InteractionIntent intent)
        {
            string source = SourceOf(intent);
            if (!drags.TryGetValue(source, out Drag drag)) return;
            drags.Remove(source);
            if (drag.target == null) return;
            if ((drag.target.position - drag.startPosition).sqrMagnitude < 1e-8f) return;

            if (rig == null) rig = FindFirstObjectByType<TierUiRig>();
            UndoStack undo = rig != null ? rig.UndoStack : null;
            undo?.Record(new TransformObstacleCommand(drag.target.gameObject, "Move " + drag.target.name,
                drag.startPosition, drag.startRotation, drag.startScale));
        }
    }
}
