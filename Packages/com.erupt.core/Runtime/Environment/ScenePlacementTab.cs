using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.UiBindings
{
    /// <summary>
    /// Tier 3 "Scene" tab: creates a Cube, Sphere or Cylinder obstacle. The three buttons
    /// are always available.
    /// </summary>
    /// <remarks>
    /// Deviation from the design guidelines: creation is meant to be an in-world placement
    /// tool (Part 4 step 2), not a panel. Until that tool exists this tab is the interim
    /// replacement for the wrist menu's "Add Shape" submenu.
    /// A new shape goes where the user is looking: onto the first surface the gaze meets
    /// within the placement distance, or that far ahead in the air when it meets none. It
    /// keeps the wrist menu's material, and every creation goes through the undo stack.
    /// </remarks>
    public class ScenePlacementTab : MonoBehaviour
    {
        public const string TabId = "scene";

        [SerializeField] private TierUiRig rig;
        [SerializeField] private EnvironmentRegistry registry;
        [SerializeField] private SelectionService selection;
        [Tooltip("Material for new shapes (the wrist menu's litMaterial).")]
        [SerializeField] private Material shapeMaterial;
        [Tooltip("How far along the gaze a new shape may go, in metres. A surface nearer than this takes it.")]
        [SerializeField] private float placementDistance = 2f;
        [Tooltip("Edge length, diameter or height of a new shape, in metres. Unity's primitives are 1 m (cylinders 2 m tall), which is huge on a table.")]
        [SerializeField] private float defaultSize = 0.3f;

        private PanelTab tab;

        /// <summary>The texts on the tab's buttons.</summary>
        public const string AddCubeLabel = "Add Cube", AddSphereLabel = "Add Sphere", AddCylinderLabel = "Add Cylinder";

        public PanelTab Tab => tab;
        public int InteractiveElementCount => tab != null ? UiBuilder.CountInteractive(tab.Content) : 0;

        private void Start()
        {
            if (rig == null) rig = FindFirstObjectByType<TierUiRig>();
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (rig == null) { Debug.LogError("ScenePlacementTab: no TierUiRig.", this); return; }

            tab = rig.AddTab(TabId, "Scene", Build);
            tab.Order = 0; // core's own tab stands first
        }

        private void Build(RectTransform content)
        {
            var column = UiBuilder.CreateColumn("Shapes", content, 8f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            Transform t = column.transform;
            var size = new Vector2(300f, 60f);
            UiBuilder.CreateButton("Cube", t, AddCubeLabel, size, () => Add(PrimitiveType.Cube));
            UiBuilder.CreateButton("Sphere", t, AddSphereLabel, size, () => Add(PrimitiveType.Sphere));
            UiBuilder.CreateButton("Cylinder", t, AddCylinderLabel, size, () => Add(PrimitiveType.Cylinder));
        }

        // Every shape used to land on the same point, so the second one sat inside the first
        // and the ray always hit the first. Step sideways, to either side, until the spot is clear.
        // The check is a box just inside the shape's own size: a shape that rests on a surface
        // touches it, and that is not "occupied".
        private static Vector3 FreeSpot(Vector3 wanted, Vector3 right, float size)
        {
            Vector3 halfExtents = Vector3.one * (size * 0.49f);
            float step = size * 1.2f;
            // Right, left, further right, further left: the nearest free spot to where the user looks.
            for (int i = 0; i < 9; i++)
            {
                float side = ((i + 1) / 2) * (i % 2 == 1 ? 1f : -1f);
                Vector3 candidate = wanted + right * (step * side);
                if (!Physics.CheckBox(candidate, halfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)) return candidate;
            }
            return wanted;
        }

        private static readonly RaycastHit[] gazeHits = new RaycastHit[32];

        /// <summary>
        /// Where a new shape of the given size goes for a gaze from <paramref name="origin"/>
        /// along <paramref name="direction"/>: resting against the first solid surface the gaze
        /// meets within <paramref name="reach"/>, or <paramref name="reach"/> ahead when it
        /// meets none. A robot is not a surface (the gaze passes through its links), and
        /// neither is a menu.
        /// </summary>
        public static Vector3 PlacementAlong(Vector3 origin, Vector3 direction, float reach, float size)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, gazeHits, reach, ~0, QueryTriggerInteraction.Ignore);
            int nearest = -1;
            for (int i = 0; i < count; i++)
            {
                Collider collider = gazeHits[i].collider;
                if (collider == null || collider.GetComponentInParent<ArticulationBody>() != null) continue;
                if (nearest < 0 || gazeHits[i].distance < gazeHits[nearest].distance) nearest = i;
            }
            if (nearest < 0) return origin + direction * reach;
            RaycastHit hit = gazeHits[nearest];
            return hit.point + hit.normal * (size * 0.5f + 0.002f);
        }

        /// <summary>
        /// Local scale that gives a primitive the same visible size on every axis. Unity's
        /// cylinder and capsule meshes span -1..1 along Y, so their Y scale is halved; the
        /// MoveIt publisher doubles it back when it reports the height.
        /// </summary>
        public static Vector3 DefaultScaleFor(PrimitiveType type, float size) => type switch
        {
            PrimitiveType.Cylinder or PrimitiveType.Capsule => new Vector3(size, size * 0.5f, size),
            _ => Vector3.one * size
        };

        /// <summary>Create a shape in front of the user, reversibly.</summary>
        public GameObject Add(PrimitiveType type)
        {
            Camera cam = Camera.main;
            Vector3 position = cam != null ? PlacementAlong(cam.transform.position, cam.transform.forward, placementDistance, defaultSize)
                                           : Vector3.forward * placementDistance;
            position = FreeSpot(position, cam != null ? cam.transform.right : Vector3.right, defaultSize);
            var command = new CreateObstacleCommand(new ObstacleSnapshot
            {
                PrimitiveType = type,
                Name = type.ToString(),
                Position = position,
                Rotation = Quaternion.identity,
                Scale = DefaultScaleFor(type, defaultSize),
                Material = shapeMaterial
            }, registry);

            UndoStack undo = rig != null ? rig.UndoStack : null;
            if (undo != null) undo.Do(command); else command.Execute();

            if (selection != null && command.Spawned != null)
                selection.Select(SelectableMarker.Ensure(command.Spawned, SelectionKind.Obstacle));
            return command.Spawned;
        }
    }
}
