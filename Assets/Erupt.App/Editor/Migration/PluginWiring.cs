using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;
using Erupt.UiBindings;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>
    /// Plugin-refactor Phase 2 scene wiring for KitchenFR3 (refactor/plugin/02-plan.md):
    /// creates the EruptCore / MoveItPlugin / MtcPlugin prefabs if missing, instances them,
    /// folds the Phase 1 Environment root and the legacy SelectionManager and MTCManager
    /// roots into them, and repoints every consumer. Idempotent. Never edits the XR rig
    /// prefab: fields on its instance are scene overrides.
    /// </summary>
    public static class PluginWiring
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";
        private const string CorePrefab = "Packages/com.erupt.core/Prefabs/EruptCore.prefab";
        private const string MoveItPrefab = "Packages/com.erupt.plugin.moveit/Prefabs/MoveItPlugin.prefab";
        private const string MtcPrefab = "Packages/com.erupt.plugin.mtc/Prefabs/MtcPlugin.prefab";
        private static readonly StringBuilder Log = new StringBuilder();

        // Values the legacy MoveItPlanningRequestMenuUI instance carried in this scene.
        private const string SceneGroup = "panda_arm";
        private const string ScenePlanner = "panda_arm";
        private const string SceneExecuteTopic = "/panda_arm_controller/joint_trajectory";

        [MenuItem("ERUPT/Refactor/Wire Plugins (Phase 2)")]
        public static void Wire()
        {
            Log.Clear();
            EnsurePrefabs();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            Transform origin = All(roots).FirstOrDefault(t => t.name == "origin");
            var router = Find<InteractionRouter>(roots);
            var robot = Find<DirectArticulationIKController>(roots);
            var oldRegistry = roots.Where(r => r.name == "Environment").Select(r => r.GetComponent<EnvironmentRegistry>()).FirstOrDefault(r => r != null);
            var oldHighlighter = roots.Where(r => r.name == "SelectionManager").Select(r => r.GetComponent<SelectionHighlighter>()).FirstOrDefault(h => h != null);
            var mtcManager = roots.FirstOrDefault(r => r.name == "MTCManager");

            // --- ERUPT root
            GameObject erupt = roots.FirstOrDefault(r => r.name == "ERUPT") ?? Instance(CorePrefab, "ERUPT");
            var host = erupt.GetComponent<PluginHost>();
            var registry = erupt.GetComponent<EnvironmentRegistry>();
            var highlighter = erupt.GetComponent<SelectionHighlighter>();
            var selectBinding = erupt.GetComponent<SelectionRouterBinding>();
            var rig = erupt.GetComponent<TierUiRig>();

            Set(registry, "worldOrigin", origin != null ? origin.gameObject : null);
            Set(selectBinding, "router", router);
            Set(host, "robot", robot);
            Set(host, "router", router);
            Set(rig, "useTierUi", false);
            if (oldHighlighter != null) Set(highlighter, "highlightMaterial", oldHighlighter.highlightMaterial);
            Line($"erupt: root '{erupt.name}' registry.worldOrigin -> {(origin ? origin.name : "(null)")}, router -> {(router ? router.name : "(null)")}, robot -> {(robot ? robot.name : "(null)")}, tier UI off");

            // --- MoveIt plugin
            GameObject moveitGo = roots.FirstOrDefault(r => r.name == "MoveItPlugin") ?? Instance(MoveItPrefab, "MoveItPlugin");
            var moveit = moveitGo.GetComponent<MoveItPlugin>();
            var player = Find<JointTrajectoryPlayer>(roots);
            Set(moveit, "player", player);
            Set(moveit, "settings.planningGroupName", SceneGroup);
            Set(moveit, "settings.defaultPlannerId", ScenePlanner);
            Set(moveit, "settings.executeTrajectoryTopic", SceneExecuteTopic);
            Line($"moveit: plugin '{moveitGo.name}', player -> {(player ? player.name : "(null)")}, group/planner {SceneGroup}, topic {SceneExecuteTopic}");
            foreach (var ui in All(roots).Select(t => t.GetComponent<MoveItPlanningRequestMenuUI>()).Where(c => c != null))
            {
                Set(ui, "plugin", moveit);
                Line($"moveit: panel '{Path(ui.transform)}'.plugin -> {moveitGo.name}");
            }

            // --- MTC plugin (components move from MTCManager)
            GameObject mtcGo = roots.FirstOrDefault(r => r.name == "MtcPlugin") ?? Instance(MtcPrefab, "MtcPlugin");
            var mtc = mtcGo.GetComponent<MtcPlugin>();
            var mtcClient = mtcGo.GetComponent<MtcClient>();
            var pickPlace = mtcGo.GetComponent<PickPlaceActionClient>();
            if (mtcManager != null)
            {
                var oldClient = mtcManager.GetComponent<MtcClient>();
                var oldPick = mtcManager.GetComponent<PickPlaceActionClient>();
                if (oldPick != null && pickPlace != null) CopyValues(oldPick, pickPlace);
                Line($"mtc: copied PickPlaceActionClient values from 'MTCManager' ({(oldPick ? "found" : "missing")})");
                Repoint(roots, oldClient, mtcClient, "dataManager");
                Repoint(roots, oldPick, pickPlace, "pickPlaceAction");
            }
            var mtcPlayer = Find<MtcSolutionPlayer>(roots);
            Set(mtc, "client", mtcClient);
            Set(mtc, "player", mtcPlayer);
            Line($"mtc: plugin '{mtcGo.name}', client on prefab, player -> {(mtcPlayer ? Path(mtcPlayer.transform) : "(null)")}");

            // --- fold the old roots
            if (oldRegistry != null) Repoint(roots, oldRegistry, registry, "registry");
            if (oldHighlighter != null) Repoint(roots, oldHighlighter, highlighter, "selectionManager");
            Assign<ObstacleVerbBindings>(roots, "selection", erupt.GetComponent<SelectionService>());
            Assign<Quest3RobotInteractionController>(roots, "selectionService", erupt.GetComponent<SelectionService>());
            Assign<SelectableGrabController>(roots, null, null);   // nothing serialised; listed for the log
            DeleteRoot(roots, "Environment", oldRegistry != null);
            DeleteRoot(roots, "SelectionManager", oldHighlighter != null);
            DeleteRoot(roots, "MTCManager", mtcManager != null);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("WIRE_PLUGINS_BEGIN\n" + Log + "WIRE_PLUGINS_END");
        }

        [MenuItem("ERUPT/Refactor/Verify Plugin Wiring (Phase 2)")]
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            var erupt = roots.FirstOrDefault(r => r.name == "ERUPT");
            Check(sb, "ERUPT root present, prefab instance", erupt != null && PrefabUtility.IsPartOfPrefabInstance(erupt), "missing or not a prefab instance");
            foreach (var t in new[] { typeof(PluginHost), typeof(SelectionService), typeof(SelectionHighlighter), typeof(SelectionRouterBinding), typeof(ModeManager), typeof(EnvironmentRegistry), typeof(TierUiRig) })
                Check(sb, $"ERUPT has {t.Name}", erupt != null && erupt.GetComponent(t) != null, "missing");

            var registries = All(roots).Select(t => t.GetComponent<EnvironmentRegistry>()).Where(r => r != null).ToList();
            Check(sb, "exactly one EnvironmentRegistry, on ERUPT, worldOrigin 'origin'",
                registries.Count == 1 && registries[0].gameObject == erupt && registries[0].WorldOriginObject != null && registries[0].WorldOriginObject.name == "origin",
                $"found {registries.Count}");
            var services = All(roots).Select(t => t.GetComponent<SelectionService>()).Where(r => r != null).ToList();
            Check(sb, "exactly one SelectionService", services.Count == 1, $"found {services.Count}");
            var highlighters = All(roots).Select(t => t.GetComponent<SelectionHighlighter>()).Where(r => r != null).ToList();
            Check(sb, "exactly one SelectionHighlighter with a material", highlighters.Count == 1 && highlighters[0].highlightMaterial != null, $"found {highlighters.Count}");
            Check(sb, "no legacy roots (Environment, SelectionManager, MTCManager)", roots.All(r => r.name != "Environment" && r.name != "SelectionManager" && r.name != "MTCManager"), "still present");

            var rig = erupt != null ? erupt.GetComponent<TierUiRig>() : null;
            Check(sb, "TierUiRig present with tier UI off", rig != null && !new SerializedObject(rig).FindProperty("useTierUi").boolValue, "tier UI on or missing");

            var moveit = All(roots).Select(t => t.GetComponent<MoveItPlugin>()).FirstOrDefault(c => c != null);
            Check(sb, "MoveItPlugin instance with player and scene settings", moveit != null &&
                new SerializedObject(moveit).FindProperty("player").objectReferenceValue != null &&
                moveit.Settings.planningGroupName == SceneGroup && moveit.Settings.executeTrajectoryTopic == SceneExecuteTopic, "missing or unset");
            var panel = All(roots).Select(t => t.GetComponent<MoveItPlanningRequestMenuUI>()).FirstOrDefault(c => c != null);
            Check(sb, "planning panel drives the plugin", panel != null && new SerializedObject(panel).FindProperty("plugin").objectReferenceValue == moveit, "not assigned");

            var mtc = All(roots).Select(t => t.GetComponent<MtcPlugin>()).FirstOrDefault(c => c != null);
            var clients = All(roots).Select(t => t.GetComponent<MtcClient>()).Where(c => c != null).ToList();
            var picks = All(roots).Select(t => t.GetComponent<PickPlaceActionClient>()).Where(c => c != null).ToList();
            Check(sb, "MtcPlugin instance with one MtcClient and one PickPlaceActionClient beside it",
                mtc != null && clients.Count == 1 && clients[0].gameObject == mtc.gameObject && picks.Count == 1 && picks[0].gameObject == mtc.gameObject,
                $"clients={clients.Count} picks={picks.Count}");
            var dash = All(roots).Select(t => t.GetComponent<MTCDashboardPanel>()).FirstOrDefault(c => c != null);
            Check(sb, "MTC dashboard points at the plugin's client", dash != null && clients.Count == 1 &&
                new SerializedObject(dash).FindProperty("dataManager").objectReferenceValue == clients[0], "not assigned");
            var recorder = All(roots).Select(t => t.GetComponent<PickPlaceTaskRecorder>()).FirstOrDefault(c => c != null);
            // The recorder's pickPlaceAction was unassigned before Phase 2 (it publishes the
            // task on a topic); it stays that way, so only a stale reference is a failure.
            var recorderAction = recorder != null ? new SerializedObject(recorder).FindProperty("pickPlaceAction").objectReferenceValue : null;
            Check(sb, "pick/place recorder points at the ERUPT highlighter (action client unassigned or the plugin's)", recorder != null && highlighters.Count == 1 &&
                new SerializedObject(recorder).FindProperty("selectionManager").objectReferenceValue == highlighters[0] &&
                (recorderAction == null || (picks.Count == 1 && recorderAction == picks[0])), "not assigned or stale");

            int stale = 0;
            foreach (var c in All(roots).SelectMany(t => t.GetComponents<MonoBehaviour>()).Where(c => c != null))
            {
                var so = new SerializedObject(c);
                var p = so.FindProperty("registry");
                if (p != null && p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null && p.objectReferenceValue != registries.FirstOrDefault()) stale++;
            }
            Check(sb, "every 'registry' field points at the ERUPT registry", stale == 0, $"{stale} stale");

            int missing = All(roots).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
            Check(sb, "no missing scripts anywhere in the scene", missing == 0, $"found {missing}");
            var xrRig = roots.FirstOrDefault(r => r.name.Contains("XR Origin"));
            Check(sb, "XR rig still a prefab instance", xrRig != null && PrefabUtility.IsPartOfPrefabInstance(xrRig), "prefab link broken");

            Debug.Log("WIRE_PLUGINS_VERIFY_BEGIN\n" + sb + "WIRE_PLUGINS_VERIFY_END");
        }

        public static void WireAndVerify() { Wire(); Verify(); }

        // --- prefabs -------------------------------------------------------------

        private static void EnsurePrefabs()
        {
            if (!File.Exists(CorePrefab))
            {
                var go = new GameObject("ERUPT");
                var host = go.AddComponent<PluginHost>();
                var selection = go.AddComponent<SelectionService>();
                go.AddComponent<SelectionHighlighter>();
                go.AddComponent<SelectionRouterBinding>();
                var modes = go.AddComponent<ModeManager>();
                var registry = go.AddComponent<EnvironmentRegistry>();
                var rig = go.AddComponent<TierUiRig>();
                Set(host, "selection", selection);
                Set(host, "modes", modes);
                Set(host, "environment", registry);
                Set(host, "ui", rig);
                Set(go.GetComponent<SelectionHighlighter>(), "selectionService", selection);
                Set(go.GetComponent<SelectionRouterBinding>(), "selectionService", selection);
                Set(rig, "useTierUi", false);
                SavePrefab(go, CorePrefab);
            }
            if (!File.Exists(MoveItPrefab))
            {
                var go = new GameObject("MoveItPlugin");
                go.AddComponent<MoveItPlugin>();
                SavePrefab(go, MoveItPrefab);
            }
            if (!File.Exists(MtcPrefab))
            {
                var go = new GameObject("MtcPlugin");
                var client = go.AddComponent<MtcClient>();
                var pick = go.AddComponent<PickPlaceActionClient>();
                var plugin = go.AddComponent<MtcPlugin>();
                Set(plugin, "client", client);
                SavePrefab(go, MtcPrefab);
            }
        }

        private static void SavePrefab(GameObject go, string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            AssetDatabase.ImportAsset(path);
            Line($"prefab: created {path}");
        }

        private static GameObject Instance(string prefabPath, string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            Line($"scene: instanced {prefabPath} as '{name}'");
            return go;
        }

        // --- helpers -------------------------------------------------------------

        private static void Set(Object target, string property, Object value)
        {
            if (target == null) return;
            var so = new SerializedObject(target);
            var p = so.FindProperty(property);
            if (p == null) { Line($"WARNING {target.GetType().Name} has no '{property}'"); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, string value)
        {
            var so = new SerializedObject(target); var p = so.FindProperty(property);
            if (p == null) { Line($"WARNING {target.GetType().Name} has no '{property}'"); return; }
            p.stringValue = value; so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, bool value)
        {
            var so = new SerializedObject(target); var p = so.FindProperty(property);
            if (p == null) { Line($"WARNING {target.GetType().Name} has no '{property}'"); return; }
            p.boolValue = value; so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CopyValues(Component from, Component to)
        {
            ComponentUtility.CopyComponent(from);
            ComponentUtility.PasteComponentValues(to);
        }

        /// <summary>Every serialised field named <paramref name="property"/> that referenced <paramref name="from"/> now references <paramref name="to"/>.</summary>
        private static void Repoint(GameObject[] roots, Object from, Object to, string property)
        {
            if (from == null) return;
            int n = 0;
            foreach (var c in All(roots).SelectMany(t => t.GetComponents<MonoBehaviour>()).Where(c => c != null))
            {
                var so = new SerializedObject(c);
                var p = so.FindProperty(property);
                if (p == null || p.propertyType != SerializedPropertyType.ObjectReference || p.objectReferenceValue != from) continue;
                p.objectReferenceValue = to;
                so.ApplyModifiedPropertiesWithoutUndo();
                n++;
                Line($"repoint: '{Path(c.transform)}'.{c.GetType().Name}.{property} -> {to.name}");
            }
            Line($"repoint: {property} {n} reference(s) moved from '{from.name}'");
        }

        private static void Assign<T>(GameObject[] roots, string property, Object value) where T : Component
        {
            var all = All(roots).Select(t => t.GetComponent<T>()).Where(c => c != null).ToList();
            if (property == null) { Line($"{typeof(T).Name}: {all.Count} in scene (no serialised link needed)"); return; }
            foreach (var c in all)
            {
                Set(c, property, value);
                Line($"{typeof(T).Name}: '{Path(c.transform)}'.{property} -> {(value ? value.name : "(null)")}");
            }
        }

        private static void DeleteRoot(GameObject[] roots, string name, bool expected)
        {
            // Earlier deletes leave destroyed entries in the array; skip them.
            var go = roots.FirstOrDefault(r => r != null && r.name == name);
            if (go == null) { Line($"delete: '{name}' not present{(expected ? " (WARNING expected)" : "")}"); return; }
            if (PrefabUtility.IsPartOfPrefabInstance(go)) { Line($"delete: WARNING '{name}' is a prefab instance; left alone"); return; }
            Object.DestroyImmediate(go);
            Line($"delete: removed root '{name}'");
        }

        private static System.Collections.Generic.IEnumerable<Transform> All(GameObject[] roots) =>
            roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Transform>(true));

        private static T Find<T>(GameObject[] roots) where T : Component =>
            All(roots).Select(t => t.GetComponent<T>()).FirstOrDefault(c => c != null);

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static void Check(StringBuilder sb, string label, bool ok, string detail) =>
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}" + (ok ? "" : $" — {detail}"));

        private static void Line(string s) => Log.AppendLine(s);
    }
}
