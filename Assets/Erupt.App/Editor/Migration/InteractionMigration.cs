using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Erupt.Interaction;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>
    /// One-shot Phase 1 migration of KitchenFR3 onto the interaction router.
    /// Editor-only; scoped to a single scene by maintainer instruction.
    /// </summary>
    /// <remarks>
    /// Deliberately makes no change to any prefab asset. Components added to a prefab
    /// instance and property overrides on it are stored in the scene file, so the shared
    /// XR rig prefab — used by four other scenes — is untouched.
    /// </remarks>
    public static class InteractionMigration
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";
        private const string ActionsPath =
            "Assets/Samples/XR Interaction Toolkit/3.2.1/Starter Assets/XRI Default Input Actions.inputactions";

        private static readonly StringBuilder Log = new StringBuilder();

        [MenuItem("ERUPT/Refactor/Migrate KitchenFR3 to Interaction Router")]
        public static void Migrate()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            var references = LoadActionReferences();

            InteractionRouter router = CreateRouter(roots);
            BindRobot(roots, router);
            BindSelection(roots, router);
            BindWristMenu(roots, router, references);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("MIGRATE_BEGIN\n" + Log + "MIGRATE_END");
        }

        // --- Router ---------------------------------------------------------

        private static InteractionRouter CreateRouter(GameObject[] roots)
        {
            var existing = Find<InteractionRouter>(roots);
            if (existing != null)
            {
                Line($"router: reusing existing on '{existing.name}'");
                return existing;
            }

            var rig = roots.FirstOrDefault(r => r.name.Contains("XR Origin"));
            var go = new GameObject("Interaction Router");
            if (rig != null) go.transform.SetParent(rig.transform, false);

            var router = go.AddComponent<InteractionRouter>();

            // Without a UI mask the router would return wrist-menu geometry as a world
            // target, and SelectionManager would clear the selection when the user points
            // at the menu — the opposite of the pre-refactor behavior.
            int uiMask = BuildUiLayerMask(roots);
            var so = new SerializedObject(router);
            so.FindProperty("uiLayers").intValue = uiMask;
            so.ApplyModifiedPropertiesWithoutUndo();

            Line($"router: created under '{(rig != null ? rig.name : "(scene root)")}' uiLayers=0x{uiMask:X}");
            return router;
        }

        private static int BuildUiLayerMask(GameObject[] roots)
        {
            int mask = 1 << LayerMask.NameToLayer("UI");

            foreach (var root in roots)
            {
                foreach (var wrist in root.GetComponentsInChildren<WristMenuController>(true))
                {
                    foreach (var t in wrist.GetComponentsInChildren<Transform>(true))
                        mask |= 1 << t.gameObject.layer;

                    Line($"uiLayers: wrist menu '{wrist.name}' contributed layer(s) via hierarchy");
                }
            }

            return mask;
        }

        // --- Rays -----------------------------------------------------------
        // The Quest3ControllerRayInteractor -> XriControllerBackend migration ran in
        // Guidelines Phase 1 and the legacy script was deleted in plugin-refactor Phase 0,
        // so the ray step no longer exists. The action-reference loader stays for the
        // wrist-menu binding below.

        // --- Feature bindings -----------------------------------------------

        private static void BindRobot(GameObject[] roots, InteractionRouter router)
        {
            var robot = Find<Quest3RobotInteractionController>(roots);
            if (robot == null) { Line("robot: NOT FOUND"); return; }

            var binding = robot.GetComponent<RobotInteractionRouterBinding>()
                          ?? robot.gameObject.AddComponent<RobotInteractionRouterBinding>();

            var so = new SerializedObject(binding);
            so.FindProperty("router").objectReferenceValue = router;
            so.FindProperty("robotInteraction").objectReferenceValue = robot;
            so.FindProperty("jointJogRadiansPerSecond").floatValue = 0.8f;
            so.ApplyModifiedPropertiesWithoutUndo();

            Line($"robot: bound RobotInteractionRouterBinding on '{robot.name}'");
        }

        private static void BindSelection(GameObject[] roots, InteractionRouter router)
        {
            var selection = Find<SelectionManager>(roots);
            if (selection == null) { Line("selection: NOT FOUND"); return; }

            var so = new SerializedObject(selection);
            so.FindProperty("interactionRouter").objectReferenceValue = router;

            // Pre-refactor this was bound to XRI Right Interaction/SelectObject — right
            // trigger only. Routing both controllers would silently widen it.
            so.FindProperty("selectionSourceId").stringValue = "right";
            so.ApplyModifiedPropertiesWithoutUndo();

            Line($"selection: router assigned on '{selection.name}', sourceId scoped to \"right\"");
        }

        private static void BindWristMenu(GameObject[] roots, InteractionRouter router,
                                          Dictionary<string, InputActionReference> refs)
        {
            var wrist = Find<WristMenuController>(roots);
            if (wrist == null) { Line("wrist: NOT FOUND"); return; }

            var so = new SerializedObject(wrist);
            so.FindProperty("interactionRouter").objectReferenceValue = router;
            so.ApplyModifiedPropertiesWithoutUndo();

            Line($"wrist: router assigned on '{wrist.name}' (prefab-instance override, stored in scene)");
        }

        // --- Helpers --------------------------------------------------------

        private static Dictionary<string, InputActionReference> LoadActionReferences()
        {
            var map = new Dictionary<string, InputActionReference>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ActionsPath))
            {
                if (asset is InputActionReference reference && reference.action != null)
                    map[$"{reference.action.actionMap.name}/{reference.action.name}"] = reference;
            }
            Line($"actions: loaded {map.Count} InputActionReferences");
            return map;
        }

        private static void SetActionProperty(SerializedObject so, string field,
                                              Dictionary<string, InputActionReference> refs, string key)
        {
            if (!refs.TryGetValue(key, out var reference))
            {
                Line($"  WARNING: action '{key}' not found for field '{field}'");
                return;
            }

            var property = so.FindProperty(field);
            property.FindPropertyRelative("m_UseReference").boolValue = true;
            property.FindPropertyRelative("m_Reference").objectReferenceValue = reference;
            Line($"  {field} <- {key}");
        }

        private static T Find<T>(GameObject[] roots) where T : Component
        {
            foreach (var root in roots)
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        private static void Line(string s) => Log.AppendLine(s);
    }
}
