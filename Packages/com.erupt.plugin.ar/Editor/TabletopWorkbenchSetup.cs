using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Erupt.Plugins.Ar.Editor
{
    /// <summary>Adds the optional AR equipment to the play or build copy of a labelled tabletop scene.</summary>
    public sealed class TabletopWorkbenchSetup : IProcessSceneWithReport
    {
        private const string SceneLabel = "EruptTabletopWorkbench";
        private const string RigPath = "Packages/com.erupt.plugin.ar/Prefabs/TabletopAR.prefab";

        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
            if (asset == null || Array.IndexOf(AssetDatabase.GetLabels(asset), SceneLabel) < 0)
                return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            if (prefab == null)
                throw new BuildFailedException($"The tabletop AR rig is missing at {RigPath}.");
            if (report != null)
                BuildPipelineContext.DependOnAsset(prefab);

            // Run before the plugin host's Start, with the components in the processed scene.
            PrefabUtility.InstantiatePrefab(prefab, scene);
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "Robot Slot" && !root.TryGetComponent<MarkerAnchor>(out _))
                    root.AddComponent<MarkerAnchor>();
        }
    }
}
