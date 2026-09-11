using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Interaction.Backends;
using Erupt.Plugins;
using Erupt.Ui;
using Erupt.UiBindings;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>
    /// Plugin-refactor Phase 3 wiring (refactor/plugin/03-plan.md): retires the legacy UI.
    /// Moves the pick/place recorder onto the MTC plugin, removes the wrist UI from the XR rig
    /// prefab (the one intentional prefab edit of the refactor), deletes the UI Toolkit scene
    /// objects, adds the Phase 3 components to the ERUPT prefab, turns the tier UI on and
    /// anchors it. Idempotent. Phase 4 deletes this file with the rest of the migration tools.
    /// </summary>
    public static class UiPortWiring
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";
        private const string RigPrefab = "Assets/Prefabs/XR Origin (XR Rig).prefab";
        private const string CorePrefab = "Packages/com.erupt.core/Prefabs/EruptCore.prefab";
        private const string MtcPrefab = "Packages/com.erupt.plugin.mtc/Prefabs/MtcPlugin.prefab";
        // WristMenuController.litMaterial on the rig prefab (the script no longer exists).
        private const string ShapeMaterialGuid = "31321ba15b8f8eb4c954353edc038b1d";
        private const string WristUiName = "WristUIMenu";
        private static readonly StringBuilder Log = new StringBuilder();

        [MenuItem("ERUPT/Refactor/Port UI (Phase 3)")]
        public static void Wire()
        {
            Log.Clear();
            UpdatePrefabAssets();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            // --- 1. recorder moves from the rig's wrist UI to the MTC plugin (before the prefab edit removes its host)
            var mtcGo = roots.FirstOrDefault(r => r.name == "MtcPlugin");
            var mtc = mtcGo != null ? mtcGo.GetComponent<MtcPlugin>() : null;
            var oldRecorder = All(roots).Select(t => t.GetComponent<PickPlaceTaskRecorder>()).FirstOrDefault(c => c != null && c.gameObject != mtcGo);
            if (mtcGo != null)
            {
                var recorder = mtcGo.GetComponent<PickPlaceTaskRecorder>() ?? mtcGo.AddComponent<PickPlaceTaskRecorder>();
                if (oldRecorder != null)
                {
                    ComponentUtility.CopyComponent(oldRecorder);
                    ComponentUtility.PasteComponentValues(recorder);
                    Line($"recorder: values copied from '{Path(oldRecorder.transform)}'");
                }
                Set(recorder, "selectionManager", All(roots).Select(t => t.GetComponent<SelectionHighlighter>()).FirstOrDefault(c => c != null));
                Set(mtc, "recorder", recorder);
                Set(mtc, "pickPlace", mtcGo.GetComponent<PickPlaceActionClient>());
                Line("recorder: PickPlaceTaskRecorder on 'MtcPlugin', plugin.recorder/pickPlace assigned");
            }
            else Line("recorder: WARNING no 'MtcPlugin' root; run Wire Plugins (Phase 2) first");

            // --- 2. legacy UI objects
            foreach (var name in new[] { "MoveItPlanningRequestMenu", "MTCMenu", "XR UI Toolkit Manager", "PanelInputConfiguration" })
                DeleteRoot(roots, name);
            roots = scene.GetRootGameObjects();

            // --- 3. ERUPT instance: tier UI on, anchors, plugin links
            var erupt = roots.FirstOrDefault(r => r.name == "ERUPT");
            if (erupt == null) { Line("erupt: WARNING no ERUPT root; run Wire Plugins (Phase 2) first"); }
            else
            {
                var rig = erupt.GetComponent<TierUiRig>();
                Set(rig, "useTierUi", true);
                var leftController = All(roots).FirstOrDefault(t => t.name == "Left Controller");
                Set(rig, "tierOneAnchor", leftController);
                var tier3 = roots.FirstOrDefault(r => r.name == "Tier3Anchor");
                if (tier3 == null)
                {
                    tier3 = new GameObject("Tier3Anchor");
                    var origin = All(roots).FirstOrDefault(t => t.name == "origin");
                    // In front of and above the world origin, facing back at it; adjust in the Editor.
                    tier3.transform.position = (origin != null ? origin.position : Vector3.zero) + new Vector3(0f, 1.2f, 0.8f);
                    tier3.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                    Line("tier3: created scene-local 'Tier3Anchor' (position is a first guess; move it in the Editor)");
                }
                Set(rig, "tierThreeAnchor", tier3.transform);
                Line($"erupt: tier UI ON, tier 1 anchor -> {(leftController ? Path(leftController) : "(null)")}, tier 3 anchor -> Tier3Anchor");

                var placement = erupt.GetComponent<ScenePlacementTab>();
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(ShapeMaterialGuid));
                Set(placement, "shapeMaterial", material);
                Line($"scene tab: shapeMaterial -> {(material ? material.name : "(missing)")}");

                var moveit = All(roots).Select(t => t.GetComponent<MoveItPlugin>()).FirstOrDefault(c => c != null);
                var ghosts = All(roots).Select(t => t.GetComponent<SpawnGhosts>()).FirstOrDefault(c => c != null);
                Set(moveit, "ghosts", ghosts);
                Line($"moveit: ghosts -> {(ghosts ? ghosts.name : "(null)")}");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            // --- 4. the rig prefab: drop the wrist UI (after the scene no longer needs its override)
            RemoveWristUiFromRig();

            Debug.Log("PORT_UI_BEGIN\n" + Log + "PORT_UI_END");
        }

        [MenuItem("ERUPT/Refactor/Verify UI Port (Phase 3)")]
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            var erupt = roots.FirstOrDefault(r => r.name == "ERUPT");
            var rig = erupt != null ? erupt.GetComponent<TierUiRig>() : null;
            Check(sb, "TierUiRig on, tier 1 anchored to the left controller, tier 3 to Tier3Anchor", rig != null &&
                new SerializedObject(rig).FindProperty("useTierUi").boolValue &&
                (new SerializedObject(rig).FindProperty("tierOneAnchor").objectReferenceValue as Transform)?.name == "Left Controller" &&
                (new SerializedObject(rig).FindProperty("tierThreeAnchor").objectReferenceValue as Transform)?.name == "Tier3Anchor", "not set");
            foreach (var t in new[] { typeof(ObstacleVerbBindings), typeof(ScenePlacementTab), typeof(XrUiRaycastInstaller) })
                Check(sb, $"ERUPT has {t.Name}", erupt != null && erupt.GetComponent(t) != null, "missing");
            var placement = erupt != null ? erupt.GetComponent<ScenePlacementTab>() : null;
            Check(sb, "scene tab has a shape material", placement != null && new SerializedObject(placement).FindProperty("shapeMaterial").objectReferenceValue != null, "unassigned");

            Check(sb, "no legacy UI roots", roots.All(r => !r.name.StartsWith("MoveItPlanningRequestMenu") && !r.name.StartsWith("MTCMenu") && r.name != "XR UI Toolkit Manager" && r.name != "PanelInputConfiguration"), "still present");
            Check(sb, "no UIDocument left in the scene", All(roots).All(t => t.GetComponent<UnityEngine.UIElements.UIDocument>() == null), "found");
            Check(sb, "no wrist UI under the rig", All(roots).All(t => t.name != WristUiName && t.name != "WristUI Document"), "still present");

            var mtc = All(roots).Select(t => t.GetComponent<MtcPlugin>()).FirstOrDefault(c => c != null);
            var recorders = All(roots).Select(t => t.GetComponent<PickPlaceTaskRecorder>()).Where(c => c != null).ToList();
            Check(sb, "exactly one PickPlaceTaskRecorder, on the MTC plugin, assigned", mtc != null && recorders.Count == 1 && recorders[0].gameObject == mtc.gameObject &&
                new SerializedObject(mtc).FindProperty("recorder").objectReferenceValue == recorders[0], $"found {recorders.Count}");
            var moveit = All(roots).Select(t => t.GetComponent<MoveItPlugin>()).FirstOrDefault(c => c != null);
            Check(sb, "MoveItPlugin has ghosts", moveit != null && new SerializedObject(moveit).FindProperty("ghosts").objectReferenceValue != null, "unassigned");

            int missing = All(roots).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
            Check(sb, "no missing scripts anywhere in the scene", missing == 0, $"found {missing}");
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab);
            Check(sb, "rig prefab has no wrist UI and no missing scripts", rigPrefab != null &&
                rigPrefab.GetComponentsInChildren<Transform>(true).All(t => t.name != WristUiName) &&
                rigPrefab.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)) == 0, "wrist UI or missing script remains");
            Check(sb, "XR rig still a prefab instance", roots.Any(r => r.name.Contains("XR Origin") && PrefabUtility.IsPartOfPrefabInstance(r)), "prefab link broken");

            Debug.Log("PORT_UI_VERIFY_BEGIN\n" + sb + "PORT_UI_VERIFY_END");
        }

        public static void WireAndVerify() { Wire(); Verify(); }

        // --- prefab assets ------------------------------------------------------------

        private static void UpdatePrefabAssets()
        {
            // ERUPT: Phase 3 components, tier UI on by default.
            var root = PrefabUtility.LoadPrefabContents(CorePrefab);
            try
            {
                var rig = root.GetComponent<TierUiRig>();
                var selection = root.GetComponent<SelectionService>();
                var registry = root.GetComponent<EnvironmentRegistry>();
                var modes = root.GetComponent<ModeManager>();
                Set(rig, "useTierUi", true);

                var verbs = root.GetComponent<ObstacleVerbBindings>() ?? root.AddComponent<ObstacleVerbBindings>();
                Set(verbs, "rig", rig); Set(verbs, "selection", selection); Set(verbs, "registry", registry);

                var placement = root.GetComponent<ScenePlacementTab>() ?? root.AddComponent<ScenePlacementTab>();
                Set(placement, "rig", rig); Set(placement, "registry", registry); Set(placement, "modes", modes); Set(placement, "selection", selection);

                if (root.GetComponent<XrUiRaycastInstaller>() == null) root.AddComponent<XrUiRaycastInstaller>();

                PrefabUtility.SaveAsPrefabAsset(root, CorePrefab);
                Line("prefab: EruptCore updated (ObstacleVerbBindings, ScenePlacementTab, XrUiRaycastInstaller, tier UI on)");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            // MtcPlugin: recorder lives here now.
            root = PrefabUtility.LoadPrefabContents(MtcPrefab);
            try
            {
                var plugin = root.GetComponent<MtcPlugin>();
                var recorder = root.GetComponent<PickPlaceTaskRecorder>() ?? root.AddComponent<PickPlaceTaskRecorder>();
                Set(plugin, "recorder", recorder);
                Set(plugin, "pickPlace", root.GetComponent<PickPlaceActionClient>());
                PrefabUtility.SaveAsPrefabAsset(root, MtcPrefab);
                Line("prefab: MtcPlugin updated (PickPlaceTaskRecorder)");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void RemoveWristUiFromRig()
        {
            var root = PrefabUtility.LoadPrefabContents(RigPrefab);
            try
            {
                var wrist = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == WristUiName);
                if (wrist == null) { Line("rig: no wrist UI in the prefab (already removed)"); }
                else
                {
                    Object.DestroyImmediate(wrist.gameObject);
                    Line($"rig: removed '{WristUiName}' (nested UI Toolkit Grab UI instance + WristMenuController) from {RigPrefab}");
                }
                // Anything else whose script was deleted this phase.
                int removed = 0;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (removed > 0) Line($"rig: removed {removed} missing-script component(s)");
                PrefabUtility.SaveAsPrefabAsset(root, RigPrefab);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // --- helpers ------------------------------------------------------------------

        private static void Set(Object target, string property, Object value)
        {
            if (target == null) return;
            var so = new SerializedObject(target); var p = so.FindProperty(property);
            if (p == null) { Line($"WARNING {target.GetType().Name} has no '{property}'"); return; }
            p.objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, bool value)
        {
            if (target == null) return;
            var so = new SerializedObject(target); var p = so.FindProperty(property);
            if (p == null) { Line($"WARNING {target.GetType().Name} has no '{property}'"); return; }
            p.boolValue = value; so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void DeleteRoot(GameObject[] roots, string name)
        {
            // A root whose prefab asset was deleted is renamed "<name> (Missing Prefab with guid: ...)".
            var matches = roots.Where(r => r != null && (r.name == name || r.name.StartsWith(name + " (Missing Prefab"))).ToList();
            if (matches.Count == 0) { Line($"delete: '{name}' not present"); return; }
            foreach (var go in matches)
            {
                Line($"delete: removed root '{go.name}'");
                Object.DestroyImmediate(go);
            }
        }

        private static System.Collections.Generic.IEnumerable<Transform> All(GameObject[] roots) =>
            roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Transform>(true));

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
