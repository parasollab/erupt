using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.UiBindings
{
    /// <summary>Which dimension of a shape a resize row changes.</summary>
    public enum ResizeAxis
    {
        /// <summary>Cube X.</summary>
        Width,
        /// <summary>Cube, cylinder and capsule Y.</summary>
        Height,
        /// <summary>Cube Z.</summary>
        Depth,
        /// <summary>Sphere, cylinder and capsule X and Z together.</summary>
        Diameter,
        /// <summary>All three axes together: spheres and anything without a known primitive.</summary>
        Size
    }

    /// <summary>
    /// In-world resize widget for the selected obstacle: one row of −/+ per dimension,
    /// attached beside the object. Guidelines Part 1 P3: a spatial control lives in the
    /// world with the thing it acts on, which is why "resize" on the tier 2 menu opens
    /// this rather than a panel.
    /// </summary>
    /// <remarks>
    /// Replaces the wrist menu's per-axis scale sliders. Each press-and-hold is one undo
    /// step, so tier 1's undo reverts a whole adjustment rather than a single tick.
    /// Changes go straight to the transform when the object is at rest, or through
    /// <see cref="XRUIScaleTransformer"/> while it is held so the grab pipeline does not
    /// overwrite them. The MoveIt sync republishes on its own when it sees the new scale.
    /// </remarks>
    public class ObstacleResizeWidget : MonoBehaviour
    {
        [Tooltip("Metres added or removed per tick.")]
        [SerializeField] private float step = 0.02f;

        [Tooltip("Seconds a button is held before it starts repeating.")]
        [SerializeField] private float holdDelay = 0.35f;

        [Tooltip("Seconds between ticks while held.")]
        [SerializeField] private float holdInterval = 0.05f;

        [Tooltip("Smallest visible dimension, in metres.")]
        [SerializeField] private float minSize = 0.02f;

        [Tooltip("Gap between the object's bounds and the widget, in metres.")]
        [SerializeField] private float sideOffset = 0.08f;

        [SerializeField] private Vector2 buttonSize = new(80f, 64f);
        [SerializeField] private float rowHeight = 72f;
        [SerializeField] private float canvasWidth = 380f;

        private UndoStack undo;
        private SelectionService selection;
        private Transform cameraTransform;

        private GameObject target;
        private PrimitiveType? primitive;
        private readonly List<Button> buttons = new();

        // One undo step per press-and-hold.
        private bool adjusting;
        private Vector3 beforePosition, beforeScale;
        private Quaternion beforeRotation;

        public Canvas Canvas { get; private set; }
        public GameObject Target => target;
        public bool IsOpen => Canvas != null && Canvas.gameObject.activeSelf && target != null;

        /// <summary>Buttons the user can press. Used by the Part 8 element count.</summary>
        public int InteractiveElementCount
        {
            get
            {
                int n = 0;
                foreach (var b in buttons) if (b != null && b.interactable) n++;
                return n;
            }
        }

        public void Initialise(UndoStack undoStack, SelectionService selectionService)
        {
            undo = undoStack;
            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
            selection = selectionService;
            if (selection != null) selection.SelectionChanged += OnSelectionChanged;
        }

        private void OnDestroy()
        {
            if (selection != null) selection.SelectionChanged -= OnSelectionChanged;
        }

        /// <summary>Open for an obstacle, or close if it is already open for that obstacle.</summary>
        public void Toggle(GameObject obstacle, PrimitiveType? shape)
        {
            if (IsOpen && ReferenceEquals(target, obstacle)) Hide();
            else Show(obstacle, shape);
        }

        public void Show(GameObject obstacle, PrimitiveType? shape)
        {
            if (obstacle == null) return;

            target = obstacle;
            primitive = shape;
            Rebuild();
            Canvas.gameObject.SetActive(true);
            Place();
        }

        public void Hide()
        {
            EndAdjust();
            target = null;
            if (Canvas != null) Canvas.gameObject.SetActive(false);
        }

        // Tier 2 disappears on deselect (Part 2); the widget it opened goes with it.
        private void OnSelectionChanged(ISelectable current)
        {
            if (target == null) return;
            if (current == null || !ReferenceEquals(current.GameObject, target)) Hide();
        }

        // --- building ---------------------------------------------------------

        /// <summary>The rows a shape gets. A cube has three; a cylinder two; a sphere one.</summary>
        public static IReadOnlyList<ResizeAxis> AxesFor(PrimitiveType? shape) => shape switch
        {
            PrimitiveType.Cube => new[] { ResizeAxis.Width, ResizeAxis.Height, ResizeAxis.Depth },
            PrimitiveType.Plane or PrimitiveType.Quad => new[] { ResizeAxis.Width, ResizeAxis.Depth },
            PrimitiveType.Cylinder or PrimitiveType.Capsule => new[] { ResizeAxis.Height, ResizeAxis.Diameter },
            _ => new[] { ResizeAxis.Size }   // sphere, or a mesh whose shape we do not know
        };

        private void Rebuild()
        {
            IReadOnlyList<ResizeAxis> axes = AxesFor(primitive);
            float height = 56f + rowHeight * axes.Count + rowHeight;   // header + rows + Done

            if (Canvas != null)
            {
                Canvas.gameObject.SetActive(false);   // Destroy is deferred; do not overlap the new canvas for a frame
                Destroy(Canvas.gameObject);
            }
            buttons.Clear();

            Canvas = UiBuilder.CreateWorldCanvas("Resize", transform, new Vector2(canvasWidth, height));

            var backdrop = UiBuilder.CreatePanel("Backdrop", Canvas.transform, new Color(0.10f, 0.11f, 0.13f, 0.94f));
            UiBuilder.Stretch(backdrop);

            var column = UiBuilder.CreateColumn("Rows", backdrop, 4f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());

            UiBuilder.CreateLabel("Header", column.transform, $"Resize {target.name}", 24f);

            foreach (ResizeAxis axis in axes)
            {
                ResizeAxis captured = axis;
                var row = UiBuilder.CreateRow(axis.ToString(), column.transform, 8f);
                var element = row.gameObject.AddComponent<LayoutElement>();
                element.preferredHeight = rowHeight;

                var label = UiBuilder.CreateLabel("Label", row.transform, axis.ToString(), 24f);
                label.alignment = TMPro.TextAlignmentOptions.Left;
                var labelElement = label.gameObject.AddComponent<LayoutElement>();
                labelElement.preferredWidth = canvasWidth - 2f * buttonSize.x - 40f;
                labelElement.preferredHeight = buttonSize.y;

                AddStepButton(row.transform, "Minus", "−", captured, -1);
                AddStepButton(row.transform, "Plus", "+", captured, +1);
            }

            Button done = UiBuilder.CreateButton("Done", column.transform, "Done", new Vector2(canvasWidth - 24f, buttonSize.y), Hide);
            buttons.Add(done);
        }

        private void AddStepButton(Transform parent, string name, string text, ResizeAxis axis, int direction)
        {
            Button button = UiBuilder.CreateButton(name, parent, text, buttonSize);
            var hold = button.gameObject.AddComponent<HoldRepeat>();
            hold.Configure(holdDelay, holdInterval,
                began: BeginAdjust,
                tick: () => Apply(axis, direction * step),
                ended: EndAdjust);
            buttons.Add(button);
        }

        // --- adjusting --------------------------------------------------------

        private void BeginAdjust()
        {
            if (target == null || adjusting) return;
            adjusting = true;
            beforePosition = target.transform.position;
            beforeRotation = target.transform.rotation;
            beforeScale = target.transform.localScale;
        }

        private void EndAdjust()
        {
            if (!adjusting) return;
            adjusting = false;
            if (target == null || undo == null) return;
            if (target.transform.localScale == beforeScale) return;

            undo.Record(new TransformObstacleCommand(target, "Resize " + target.name,
                                                     beforePosition, beforeRotation, beforeScale));
        }

        private void Apply(ResizeAxis axis, float metres)
        {
            if (target == null) return;

            Vector3 delta = ScaleDeltaFor(primitive, axis, metres);

            var axisLock = target.GetComponent<XRGrabTransformerScaleAxisLock>();
            if (axisLock != null)
            {
                if (axisLock.freezeXScale) delta.x = 0f;
                if (axisLock.freezeYScale) delta.y = 0f;
                if (axisLock.freezeZScale) delta.z = 0f;
            }

            Vector3 current = target.transform.localScale;
            Vector3 wanted = ClampScale(current + delta, primitive, minSize);
            delta = wanted - current;
            if (delta == Vector3.zero) return;

            // While held, XRI rewrites the transform every frame from its own target
            // scale; queue the change so the pipeline applies it instead of undoing it.
            var grab = target.GetComponent<XRGrabInteractable>();
            var viaGrab = target.GetComponent<XRUIScaleTransformer>();
            if (grab != null && grab.isSelected && viaGrab != null)
                viaGrab.queuedDelta += delta;
            else
                target.transform.localScale = wanted;
        }

        /// <summary>
        /// Local-scale change that grows the named dimension by <paramref name="metres"/>.
        /// Unity's cylinder and capsule meshes are 2 m tall at Y scale 1, so their height
        /// moves by half the requested amount in scale units.
        /// </summary>
        public static Vector3 ScaleDeltaFor(PrimitiveType? shape, ResizeAxis axis, float metres)
        {
            bool tall = shape == PrimitiveType.Cylinder || shape == PrimitiveType.Capsule;
            return axis switch
            {
                ResizeAxis.Width => new Vector3(metres, 0f, 0f),
                ResizeAxis.Height => new Vector3(0f, tall ? metres * 0.5f : metres, 0f),
                ResizeAxis.Depth => new Vector3(0f, 0f, metres),
                ResizeAxis.Diameter => new Vector3(metres, 0f, metres),
                _ => new Vector3(metres, tall ? metres * 0.5f : metres, metres)
            };
        }

        /// <summary>Keep every visible dimension at least <paramref name="minSize"/> metres.</summary>
        public static Vector3 ClampScale(Vector3 scale, PrimitiveType? shape, float minSize)
        {
            bool tall = shape == PrimitiveType.Cylinder || shape == PrimitiveType.Capsule;
            float minY = tall ? minSize * 0.5f : minSize;
            return new Vector3(Mathf.Max(minSize, scale.x), Mathf.Max(minY, scale.y), Mathf.Max(minSize, scale.z));
        }

        // --- placement --------------------------------------------------------

        private void LateUpdate()
        {
            if (Canvas == null || !Canvas.gameObject.activeSelf) return;
            if (target == null) { Hide(); return; }   // destroyed under us (delete, undo)
            Place();
        }

        // Beside the object on the user's right, level with its centre; tier 2 has the top.
        private void Place()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            Vector3 right = cameraTransform != null ? Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized : Vector3.right;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;

            Vector3 centre;
            float extentAlongRight;
            var renderer = target.GetComponent<Renderer>();
            if (renderer != null)
            {
                Bounds b = renderer.bounds;
                centre = b.center;
                extentAlongRight = Mathf.Abs(right.x) * b.extents.x + Mathf.Abs(right.y) * b.extents.y + Mathf.Abs(right.z) * b.extents.z;
            }
            else
            {
                centre = target.transform.position;
                extentAlongRight = 0f;
            }

            float halfWidth = canvasWidth / UiBuilder.PixelsPerUnit * 0.5f;
            Canvas.transform.position = centre + right * (extentAlongRight + sideOffset + halfWidth);

            if (cameraTransform != null)
                Canvas.transform.LookAt(Canvas.transform.position + cameraTransform.forward);
        }
    }

    /// <summary>
    /// Fires once on press and then repeatedly while held. Works with any pointer that
    /// reaches uGUI: the XR ray, poke, or the desktop mouse.
    /// </summary>
    public class HoldRepeat : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private float delay = 0.35f, interval = 0.05f;
        private Action began, tick, ended;
        private Button button;
        private bool held;
        private float nextTick;

        public bool IsHeld => held;

        public void Configure(float holdDelay, float holdInterval, Action began, Action tick, Action ended)
        {
            delay = holdDelay;
            interval = holdInterval;
            this.began = began;
            this.tick = tick;
            this.ended = ended;
        }

        private void Awake() => button = GetComponent<Button>();

        public void OnPointerDown(PointerEventData eventData)
        {
            if (button != null && !button.interactable) return;
            if (held) return;
            held = true;
            began?.Invoke();
            tick?.Invoke();
            nextTick = Time.unscaledTime + delay;
        }

        public void OnPointerUp(PointerEventData eventData) => Release();
        public void OnPointerExit(PointerEventData eventData) => Release();
        private void OnDisable() => Release();

        private void Update()
        {
            if (!held || Time.unscaledTime < nextTick) return;
            tick?.Invoke();
            nextTick = Time.unscaledTime + interval;
        }

        private void Release()
        {
            if (!held) return;
            held = false;
            ended?.Invoke();
        }
    }
}
