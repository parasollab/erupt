using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>Read-only survey helper for the Phase 1 migration. No runtime effect.</summary>
    public static class InteractionMigrationInspector
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";

        [MenuItem("ERUPT/Refactor/Inspect KitchenFR3")]
        public static void Inspect()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder();

            foreach (var root in scene.GetRootGameObjects())
            {
                Report<Quest3RobotInteractionController>(root, "ROBOT", sb);
                Report<SelectionManager>(root, "SELECTION", sb);
                Report<WristMenuController>(root, "WRIST", sb);
                Report<DirectArticulationIKController>(root, "IK", sb);
            }

            Debug.Log("INSPECT_BEGIN\n" + sb + "INSPECT_END");
        }

        private static void Report<T>(GameObject root, string label, StringBuilder sb) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                sb.AppendLine($"{label}\t{Path(c.transform)}\tinPrefab={PrefabUtility.IsPartOfPrefabInstance(c)}\tasset={AssetOf(c)}");
        }

        private static string AssetOf(Component c)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(c);
            return src != null ? AssetDatabase.GetAssetPath(src) : "(scene)";
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }
    }
}
