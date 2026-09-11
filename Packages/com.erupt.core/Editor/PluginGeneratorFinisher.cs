using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;
using Erupt.Plugins;

namespace Erupt.Editor
{
    /// <summary>
    /// Second half of plugin generation. The generated behaviour does not exist until the
    /// new assembly compiles, so the prefab step is parked in <see cref="SessionState"/> and
    /// finished after the reload (the same two-step pattern the OpenCV define installer used).
    /// </summary>
    [InitializeOnLoad]
    public static class PluginGeneratorFinisher
    {
        private const string Key = "Erupt.PluginGenerator.Pending";

        [Serializable]
        private class Pending
        {
            public string pluginName, ns, packageRoot;
            public bool addToScene;
            public int attempts;
        }

        static PluginGeneratorFinisher()
        {
            EditorApplication.delayCall += TryFinish;
        }

        public static void Schedule(PluginSpec spec, string packageRoot, bool addToScene)
        {
            var pending = new Pending { pluginName = spec.PluginName, ns = spec.Namespace, packageRoot = packageRoot, addToScene = addToScene };
            SessionState.SetString(Key, JsonUtility.ToJson(pending));
        }

        public static bool HasPending => !string.IsNullOrEmpty(SessionState.GetString(Key, ""));

        private static void TryFinish()
        {
            string json = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(json)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryFinish;
                return;
            }

            var pending = JsonUtility.FromJson<Pending>(json);
            if (!Directory.Exists(pending.packageRoot))
            {
                // The package was removed before the assembly compiled; nothing to finish.
                SessionState.EraseString(Key);
                return;
            }
            string typeName = $"{pending.ns}.{pending.pluginName}Plugin";
            Type type = TypeCache.GetTypesDerivedFrom<EruptPluginBehaviour>().FirstOrDefault(t => t.FullName == typeName);
            if (type == null)
            {
                // Not compiled yet (or failed to). Give it a few reloads, then give up loudly.
                pending.attempts++;
                if (pending.attempts > 5)
                {
                    SessionState.EraseString(Key);
                    Debug.LogError($"[ERUPT] Plugin '{typeName}' never compiled; prefab not created. Fix the compile errors, then create the prefab by hand or re-run the generator with 'overwrite'.");
                    return;
                }
                SessionState.SetString(Key, JsonUtility.ToJson(pending));
                return;
            }

            SessionState.EraseString(Key);
            try
            {
                string prefabPath = CreatePrefab(type, pending);
                if (pending.addToScene) AddToScene(prefabPath, pending.pluginName);
                Debug.Log($"[ERUPT] Plugin '{pending.pluginName}' ready: {prefabPath}{(pending.addToScene ? " (instanced in the open scene)" : "")}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static string CreatePrefab(Type type, Pending pending)
        {
            string dir = Path.Combine(pending.packageRoot, "Prefabs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{pending.pluginName}Plugin.prefab").Replace('\\', '/');
            if (File.Exists(path)) return path;

            var go = new GameObject($"{pending.pluginName}Plugin");
            go.AddComponent(type);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        // Scene instance only; a prefab asset is never edited here.
        private static void AddToScene(string prefabPath, string pluginName)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;
            if (scene.GetRootGameObjects().Any(r => r.name == $"{pluginName}Plugin")) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = $"{pluginName}Plugin";

            var erupt = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "ERUPT");
            if (erupt != null) instance.transform.SetSiblingIndex(erupt.transform.GetSiblingIndex() + 1);
            else Debug.LogWarning("[ERUPT] No 'ERUPT' root in the open scene; the plugin is instanced but PluginHost will not find it until the EruptCore prefab is added.");

            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
