using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>
    /// One-shot plugin-refactor Phase 0 clean-up of KitchenFR3 (refactor/plugin/00-plan.md).
    /// Removes the scene hazards recorded in the survey. Editor-only; never modifies a
    /// prefab asset: every removal is either a scene-local object or an added-component
    /// override stored in the scene file.
    /// </summary>
    public static class KitchenSceneCleanup
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";
        private static readonly StringBuilder Log = new StringBuilder();

        [MenuItem("ERUPT/Refactor/Clean KitchenFR3")]
        public static void Clean()
        {
            Log.Clear();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            var robots = ReportRobots(roots);
            CleanGround(roots);
            RemoveDuplicateResetKitchenScene(roots);
            RemoveActionDemo(roots);
            // Steps above may destroy root objects; the array still holds the dead
            // references, so re-read the scene before any further traversal.
            roots = scene.GetRootGameObjects();
            RemoveUnusedRobot(roots, robots);
            roots = scene.GetRootGameObjects();
            ReportMissingScripts(roots);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("CLEAN_BEGIN\n" + Log + "CLEAN_END");
        }

        /// <summary>Batchmode entry point: clean, then re-open and verify.</summary>
        public static void CleanAndVerify()
        {
            Clean();
            Verify();
        }

        [MenuItem("ERUPT/Refactor/Verify KitchenFR3 Clean-up")]
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            var ground = AllObjects(roots).FirstOrDefault(g => g.name == "Ground");
            Check(sb, "Ground present", ground != null, "missing");
            if (ground != null)
            {
                Check(sb, "Ground has no missing scripts",
                    GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(ground) == 0, "still has one");
                Check(sb, "Ground has no CollisionObjectsListenerSimple",
                    ground.GetComponent<CollisionObjectsListenerSimple>() == null, "still present");
            }

            var listeners = roots.SelectMany(r => r.GetComponentsInChildren<CollisionObjectsListenerSimple>(true)).ToList();
            Check(sb, "exactly one CollisionObjectsListenerSimple, enabled",
                listeners.Count == 1 && listeners[0].enabled, $"found {listeners.Count}");

            var resets = roots.SelectMany(r => r.GetComponentsInChildren<ResetKitchenScene>(true)).ToList();
            Check(sb, "exactly one ResetKitchenScene, enabled, action assigned",
                resets.Count == 1 && resets[0].enabled &&
                new SerializedObject(resets[0]).FindProperty("_resetAction").objectReferenceValue != null,
                $"found {resets.Count}");

            Check(sb, "no 'ROS2 Action Demo' root", roots.All(r => r.name != "ROS2 Action Demo"), "still present");

            var robotRoots = RobotRoots(roots);
            Check(sb, "exactly one robot root", robotRoots.Count == 1,
                $"found {robotRoots.Count}: {string.Join(", ", robotRoots.Select(r => r.name))}");

            var ik = Find<DirectArticulationIKController>(roots);
            var ghosts = Find<SpawnGhosts>(roots);
            var player = Find<MtcSolutionPlayer>(roots);
            var ikRoot = RootOf(ik != null ? new SerializedObject(ik).FindProperty("robotRoot").objectReferenceValue : null);
            var ghostRoot = RootOf(ghosts != null ? ghosts.realRobot : null);
            var playerRoot = RootOf(player);
            Check(sb, "IK, SpawnGhosts.realRobot and MtcSolutionPlayer agree on the robot",
                ikRoot != null && ikRoot == ghostRoot && ikRoot == playerRoot,
                $"ik={Name(ikRoot)} ghosts={Name(ghostRoot)} player={Name(playerRoot)}");
            Check(sb, "SpawnGhosts.robotPrefab is a prefab asset",
                ghosts != null && ghosts.robotPrefab != null && !ghosts.robotPrefab.scene.IsValid(),
                ghosts == null ? "no SpawnGhosts" : $"robotPrefab={Name(ghosts.robotPrefab)} inScene={(ghosts.robotPrefab != null && ghosts.robotPrefab.scene.IsValid())}");

            int missing = AllObjects(roots).Sum(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
            Check(sb, "no missing scripts anywhere in the scene", missing == 0, $"found {missing}");

            var rig = roots.FirstOrDefault(r => r.name.Contains("XR Origin"));
            Check(sb, "XR rig still a prefab instance", rig != null && PrefabUtility.IsPartOfPrefabInstance(rig), "prefab link broken");

            Debug.Log("CLEAN_VERIFY_BEGIN\n" + sb + "CLEAN_VERIFY_END");
        }

        // --- Steps ------------------------------------------------------------

        private sealed class RobotRefs
        {
            public GameObject IkRoot, GhostReal, PlayerHost, GhostTemplate;
        }

        private static RobotRefs ReportRobots(GameObject[] roots)
        {
            var ik = Find<DirectArticulationIKController>(roots);
            var ghosts = Find<SpawnGhosts>(roots);
            var player = Find<MtcSolutionPlayer>(roots);

            var refs = new RobotRefs
            {
                IkRoot = RootOf(ik != null ? new SerializedObject(ik).FindProperty("robotRoot").objectReferenceValue : null),
                GhostReal = RootOf(ghosts != null ? ghosts.realRobot : null),
                PlayerHost = RootOf(player),
                GhostTemplate = ghosts != null ? ghosts.robotPrefab : null,
            };

            Line($"robots: roots with ArticulationBody = {string.Join(", ", RobotRoots(roots).Select(Describe))}");
            Line($"robots: DirectArticulationIKController.robotRoot -> {Name(refs.IkRoot)}");
            Line($"robots: SpawnGhosts.realRobot -> {Name(refs.GhostReal)}");
            Line($"robots: MtcSolutionPlayer host -> {Name(refs.PlayerHost)}");
            Line($"robots: SpawnGhosts.robotPrefab -> {Name(refs.GhostTemplate)} " +
                 $"(scene object={(refs.GhostTemplate != null && refs.GhostTemplate.scene.IsValid())})");
            return refs;
        }

        private static void CleanGround(GameObject[] roots)
        {
            var ground = AllObjects(roots).FirstOrDefault(g => g.name == "Ground");
            if (ground == null) { Line("ground: NOT FOUND"); return; }
            if (PrefabUtility.IsPartOfPrefabInstance(ground)) { Line("ground: WARNING is a prefab instance, skipped"); return; }

            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(ground);
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(ground);
            Line($"ground: missing-script components {missing}, removed {removed}");

            foreach (var listener in ground.GetComponents<CollisionObjectsListenerSimple>())
            {
                if (listener.enabled)
                {
                    Line("ground: WARNING CollisionObjectsListenerSimple is ENABLED here; not removed");
                    continue;
                }
                Line($"ground: removed disabled CollisionObjectsListenerSimple (topic='{new SerializedObject(listener).FindProperty("topic").stringValue}')");
                Object.DestroyImmediate(listener);
            }

            var remaining = roots.SelectMany(r => r.GetComponentsInChildren<CollisionObjectsListenerSimple>(true)).ToList();
            Line($"listener: remaining {remaining.Count} -> {string.Join(", ", remaining.Select(l => $"'{Path(l.transform)}' enabled={l.enabled}"))}");
        }

        private static void RemoveDuplicateResetKitchenScene(GameObject[] roots)
        {
            var all = roots.SelectMany(r => r.GetComponentsInChildren<ResetKitchenScene>(true)).ToList();
            foreach (var r in all)
            {
                bool hasAction = new SerializedObject(r).FindProperty("_resetAction").objectReferenceValue != null;
                Line($"reset: '{Path(r.transform)}' enabled={r.enabled} action={hasAction} " +
                     $"addedOverride={PrefabUtility.IsAddedComponentOverride(r)}");
            }

            var dead = all.Where(r => !r.enabled &&
                                      new SerializedObject(r).FindProperty("_resetAction").objectReferenceValue == null).ToList();
            if (all.Count - dead.Count != 1)
            {
                Line($"reset: WARNING expected exactly one live ResetKitchenScene, found {all.Count - dead.Count}; nothing removed");
                return;
            }

            foreach (var r in dead)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(r) && !PrefabUtility.IsAddedComponentOverride(r))
                {
                    Line("reset: WARNING duplicate belongs to the prefab asset; skipped (would modify the prefab)");
                    continue;
                }
                Object.DestroyImmediate(r);
                Line("reset: removed disabled duplicate (added-component override, stored in the scene)");
            }
        }

        private static void RemoveActionDemo(GameObject[] roots)
        {
            var demo = roots.FirstOrDefault(r => r.name == "ROS2 Action Demo");
            if (demo == null) { Line("demo: NOT FOUND"); return; }
            if (PrefabUtility.IsPartOfPrefabInstance(demo)) { Line("demo: WARNING is a prefab instance, skipped"); return; }
            Object.DestroyImmediate(demo);
            Line("demo: deleted root 'ROS2 Action Demo' (sample script deleted in Phase 0)");
        }

        private static void RemoveUnusedRobot(GameObject[] roots, RobotRefs refs)
        {
            var used = new HashSet<GameObject>();
            if (refs.IkRoot != null) used.Add(refs.IkRoot);
            if (refs.GhostReal != null) used.Add(refs.GhostReal);
            if (refs.PlayerHost != null) used.Add(refs.PlayerHost);

            if (used.Count != 1)
            {
                Line($"robot: WARNING consumers disagree on the live robot ({used.Count} distinct); nothing removed");
                return;
            }

            var ghosts = Find<SpawnGhosts>(roots);
            foreach (var robot in RobotRoots(roots).Where(r => !used.Contains(r)).ToList())
            {
                if (!robot.name.StartsWith("fr3"))
                {
                    Line($"robot: WARNING unreferenced articulated root '{robot.name}' is not an fr3; skipped");
                    continue;
                }

                if (ghosts != null && ghosts.robotPrefab == robot)
                {
                    // The inactive instance is only the ghost template. Point SpawnGhosts at the
                    // prefab asset it was instantiated from; SpawnGhost() sets pose and active
                    // state itself, so the instance overrides (name, inactive, transform) carry
                    // nothing the ghost needs.
                    var asset = PrefabUtility.GetCorrespondingObjectFromSource(robot);
                    if (asset == null)
                    {
                        Line($"robot: WARNING '{robot.name}' is the ghost template but has no prefab asset; skipped");
                        continue;
                    }
                    var so = new SerializedObject(ghosts);
                    so.FindProperty("robotPrefab").objectReferenceValue = asset;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Line($"ghosts: robotPrefab '{robot.name}' (scene instance) -> asset '{AssetDatabase.GetAssetPath(asset)}'");
                }

                Line($"robot: deleting '{robot.name}' ({Describe(robot)})");
                Object.DestroyImmediate(robot);
            }

            Line($"robot: kept '{Name(used.First())}'");
        }

        private static void ReportMissingScripts(GameObject[] roots)
        {
            foreach (var go in AllObjects(roots))
            {
                int n = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (n > 0) Line($"missing: '{Path(go.transform)}' has {n} missing-script component(s) (not removed)");
            }
        }

        // --- Helpers ----------------------------------------------------------

        private static IEnumerable<GameObject> Live(GameObject[] roots) => roots.Where(r => r != null);

        private static List<GameObject> RobotRoots(GameObject[] roots) =>
            Live(roots).Where(r => r.GetComponentInChildren<ArticulationBody>(true) != null).ToList();

        private static string Describe(GameObject go)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            return $"'{go.name}' active={go.activeSelf} prefabInstance={PrefabUtility.IsPartOfPrefabInstance(go)} " +
                   $"asset={(src != null ? AssetDatabase.GetAssetPath(src) : "(scene)")}";
        }

        private static GameObject RootOf(Object o)
        {
            switch (o)
            {
                case GameObject go: return go.transform.root.gameObject;
                case Component c: return c.transform.root.gameObject;
                default: return null;
            }
        }

        private static string Name(Object o) => o != null ? o.name : "(null)";

        private static IEnumerable<GameObject> AllObjects(GameObject[] roots) =>
            Live(roots).SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject);

        private static T Find<T>(GameObject[] roots) where T : Component
        {
            foreach (var root in Live(roots))
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

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
