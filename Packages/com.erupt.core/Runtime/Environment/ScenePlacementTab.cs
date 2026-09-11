using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Ui;

namespace Erupt.UiBindings
{
    /// <summary>
    /// Tier 3 "Scene" tab: creates a Cube, Sphere or Cylinder obstacle in Build mode.
    /// </summary>
    /// <remarks>
    /// Guideline deviation, recorded in refactor/plugin/03-report.md: creation is meant to be
    /// a Build-mode in-world placement tool (Part 4 step 2), not a panel. Until that tool
    /// exists this tab is the interim replacement for the wrist menu's "Add Shape" submenu.
    /// It keeps the wrist menu's placement (2 m in front of the user) and material, and
    /// every creation goes through the undo stack.
    /// </remarks>
    public class ScenePlacementTab : MonoBehaviour
    {
        public const string TabId = "scene";

        [SerializeField] private TierUiRig rig;
        [SerializeField] private EnvironmentRegistry registry;
        [SerializeField] private ModeManager modes;
        [SerializeField] private SelectionService selection;
        [Tooltip("Material for new shapes (the wrist menu's litMaterial).")]
        [SerializeField] private Material shapeMaterial;
        [SerializeField] private float placementDistance = 2f;

        private UnityEngine.UI.Button cube, sphere, cylinder;
        private PanelTab tab;

        public PanelTab Tab => tab;
        public int InteractiveElementCount => tab != null ? UiBuilder.CountInteractive(tab.Content) : 0;

        private void Start()
        {
            if (rig == null) rig = FindFirstObjectByType<TierUiRig>();
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
            if (modes == null) modes = FindFirstObjectByType<ModeManager>();
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();
            if (rig == null) { Debug.LogError("ScenePlacementTab: no TierUiRig.", this); return; }

            tab = rig.AddTab(TabId, "Scene", Build);
            if (modes != null) modes.ModeChanged += _ => Refresh();
            Refresh();
        }

        private void Build(RectTransform content)
        {
            var column = UiBuilder.CreateColumn("Shapes", content, 8f);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            Transform t = column.transform;
            var size = new Vector2(300f, 60f);
            cube = UiBuilder.CreateButton("Cube", t, "Add Cube", size, () => Add(PrimitiveType.Cube));
            sphere = UiBuilder.CreateButton("Sphere", t, "Add Sphere", size, () => Add(PrimitiveType.Sphere));
            cylinder = UiBuilder.CreateButton("Cylinder", t, "Add Cylinder", size, () => Add(PrimitiveType.Cylinder));
            UiBuilder.CreateLabel("Hint", t, "Build mode only", 20f);
        }

        private void Refresh()
        {
            bool build = modes == null || modes.Is(AppMode.Build);
            foreach (var b in new[] { cube, sphere, cylinder })
                if (b != null) UiBuilder.SetInteractable(b, build);
        }

        // Every shape used to land on the same point, so the second one sat inside the first
        // and the ray always hit the first. Step sideways until the spot is clear.
        private static Vector3 FreeSpot(Vector3 wanted, Vector3 right)
        {
            const float clearance = 0.75f;
            for (int i = 0; i < 6; i++)
            {
                Vector3 candidate = wanted + right * (1.1f * i);
                if (!Physics.CheckSphere(candidate, clearance, ~0, QueryTriggerInteraction.Ignore)) return candidate;
            }
            return wanted;
        }

        /// <summary>Create a shape in front of the user, reversibly.</summary>
        public GameObject Add(PrimitiveType type)
        {
            if (modes != null && !modes.Is(AppMode.Build)) return null;

            Camera cam = Camera.main;
            Vector3 position = cam != null ? cam.transform.position + cam.transform.forward * placementDistance
                                           : Vector3.forward * placementDistance;
            position = FreeSpot(position, cam != null ? cam.transform.right : Vector3.right);
            var command = new CreateObstacleCommand(new ObstacleSnapshot
            {
                PrimitiveType = type,
                Name = type.ToString(),
                Position = position,
                Rotation = Quaternion.identity,
                Scale = Vector3.one,
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
