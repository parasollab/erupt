using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Erupt.Environment;
using Erupt.UiBindings;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>
    /// Plugin-refactor Phase 1 scene wiring for KitchenFR3 (refactor/plugin/01-plan.md):
    /// adds the Environment root with the registry, and the MoveIt planning-scene sync
    /// next to the collision-object listener. Idempotent. Never modifies a prefab asset.
    /// </summary>
    public static class EnvironmentWiring
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";
        private static readonly StringBuilder Log = new StringBuilder();

        [MenuItem("ERUPT/Refactor/Wire Environment (Phase 1)")]
        public static void Wire()
        {
            Log.Clear();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            var origin = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                              .FirstOrDefault(t => t.name == "origin");
            Line(origin != null ? $"origin: '{Path(origin)}'" : "origin: NOT FOUND (registry will use its own transform)");

            // --- Environment root + registry
            var envRoot = roots.FirstOrDefault(r => r.name == "Environment");
            if (envRoot == null)
            {
                envRoot = new GameObject("Environment");
                Line("environment: created root 'Environment'");
            }
            var registry = envRoot.GetComponent<EnvironmentRegistry>() ?? envRoot.AddComponent<EnvironmentRegistry>();
            var rso = new SerializedObject(registry);
            rso.FindProperty("worldOrigin").objectReferenceValue = origin != null ? origin.gameObject : null;
            rso.ApplyModifiedPropertiesWithoutUndo();
            Line($"registry: on '{envRoot.name}', worldOrigin -> {(origin != null ? origin.name : "(null)")}");

            // --- MoveIt sync next to the listener
            var listeners = roots.SelectMany(r => r.GetComponentsInChildren<CollisionObjectsListenerSimple>(true)).ToList();
            if (listeners.Count != 1)
                Line($"listener: WARNING expected exactly one CollisionObjectsListenerSimple, found {listeners.Count}");
            foreach (var listener in listeners)
            {
                var host = listener.gameObject;
                if (PrefabUtility.IsPartOfPrefabInstance(host) && !PrefabUtility.IsAddedComponentOverride(listener))
                    Line($"listener: NOTE '{Path(host.transform)}' is a prefab instance; the sync is added as a scene override");

                var lso = new SerializedObject(listener);
                lso.FindProperty("registry").objectReferenceValue = registry;
                lso.ApplyModifiedPropertiesWithoutUndo();

                var sync = host.GetComponent<MoveItPlanningSceneSync>() ?? host.AddComponent<MoveItPlanningSceneSync>();
                var sso = new SerializedObject(sync);
                sso.FindProperty("registry").objectReferenceValue = registry;
                sso.FindProperty("listener").objectReferenceValue = listener;
                sso.ApplyModifiedPropertiesWithoutUndo();
                Line($"sync: MoveItPlanningSceneSync on '{Path(host.transform)}' (registry, listener assigned)");
            }

            // --- Consumers that used to hold the listener now hold the registry
            Assign<AttachedCollisionObjectListener>(roots, "registry", registry);
            Assign<MTCTrajectoryPlayer>(roots, "registry", registry);
            Assign<ObstacleVerbBindings>(roots, "registry", registry);
            Assign<WristMenuController>(roots, "registry", registry);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("WIRE_ENV_BEGIN\n" + Log + "WIRE_ENV_END");
        }

        [MenuItem("ERUPT/Refactor/Verify Environment Wiring (Phase 1)")]
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            var registries = roots.SelectMany(r => r.GetComponentsInChildren<EnvironmentRegistry>(true)).ToList();
            Check(sb, "exactly one EnvironmentRegistry", registries.Count == 1, $"found {registries.Count}");
            var registry = registries.FirstOrDefault();
            Check(sb, "registry worldOrigin is 'origin'",
                registry != null && registry.WorldOriginObject != null && registry.WorldOriginObject.name == "origin",
                registry == null ? "no registry" : $"worldOrigin={(registry.WorldOriginObject ? registry.WorldOriginObject.name : "(null)")}");

            var syncs = roots.SelectMany(r => r.GetComponentsInChildren<MoveItPlanningSceneSync>(true)).ToList();
            Check(sb, "exactly one MoveItPlanningSceneSync", syncs.Count == 1, $"found {syncs.Count}");
            Check(sb, "sync sits with the listener",
                syncs.Count == 1 && syncs[0].GetComponent<CollisionObjectsListenerSimple>() != null, "listener missing on sync host");
            Check(sb, "sync references the registry",
                registry != null && syncs.Count == 1 && new SerializedObject(syncs[0]).FindProperty("registry").objectReferenceValue == registry, "not assigned");

            var listener = roots.SelectMany(r => r.GetComponentsInChildren<CollisionObjectsListenerSimple>(true)).FirstOrDefault();
            Check(sb, "listener references the registry",
                registry != null && listener != null && new SerializedObject(listener).FindProperty("registry").objectReferenceValue == registry, "not assigned");

            var player = roots.SelectMany(r => r.GetComponentsInChildren<MTCTrajectoryPlayer>(true)).FirstOrDefault();
            Check(sb, "MTCTrajectoryPlayer references the registry",
                registry != null && player != null && new SerializedObject(player).FindProperty("registry").objectReferenceValue == registry, "not assigned");

            int missing = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                               .Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
            Check(sb, "no missing scripts anywhere in the scene", missing == 0, $"found {missing}");

            Debug.Log("WIRE_ENV_VERIFY_BEGIN\n" + sb + "WIRE_ENV_VERIFY_END");
        }

        /// <summary>Batchmode entry point.</summary>
        public static void WireAndVerify()
        {
            Wire();
            Verify();
        }

        private static void Assign<T>(GameObject[] roots, string property, Object value) where T : Component
        {
            var all = roots.SelectMany(r => r.GetComponentsInChildren<T>(true)).ToList();
            if (all.Count == 0) { Line($"{typeof(T).Name}: none in scene"); return; }
            foreach (var c in all)
            {
                var so = new SerializedObject(c);
                var prop = so.FindProperty(property);
                if (prop == null) { Line($"{typeof(T).Name}: WARNING no '{property}' field"); continue; }
                prop.objectReferenceValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
                Line($"{typeof(T).Name}: '{Path(c.transform)}'.{property} -> {value.name}");
            }
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
