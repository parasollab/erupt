using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>Compares old vs new targeting parameters. Read-only.</summary>
    public static class InteractionDeltaProbe
    {
        [MenuItem("ERUPT/Refactor/Probe Behavior Delta")]
        public static void Probe()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/KitchenFR3.unity", OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            // The legacy SelectionManager (and its ray interactor) is gone since plugin refactor Phase 2.
            sb.AppendLine("OLD selection ray: (retired in plugin refactor Phase 2)");

            var router = roots.SelectMany(r => r.GetComponentsInChildren<InteractionRouter>(true)).First();
            var so = new SerializedObject(router);
            sb.AppendLine($"NEW router: rayLength={so.FindProperty("rayLength").floatValue} " +
                          $"raycastLayers=0x{so.FindProperty("raycastLayers").intValue:X} " +
                          $"uiLayers=0x{so.FindProperty("uiLayers").intValue:X}");

            // Objects the old name-based UI filter would have made unselectable.
            string[] words = { "wrist", "ui", "menu", "panel", "button" };
            int blocked = 0;
            foreach (var root in roots)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.CompareTag("Selectable")) continue;
                    string n = t.name.ToLower();
                    if (words.Any(w => n.Contains(w)))
                    {
                        sb.AppendLine($"    previously-unselectable Selectable: '{t.name}'");
                        blocked++;
                    }
                }
            sb.AppendLine($"Selectable objects blocked by the old name filter: {blocked}");

            int selectable = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                                  .Count(t => t.CompareTag("Selectable"));
            sb.AppendLine($"Total 'Selectable'-tagged objects in scene: {selectable}");

            Debug.Log("DELTA_BEGIN\n" + sb + "DELTA_END");
        }
    }
}
