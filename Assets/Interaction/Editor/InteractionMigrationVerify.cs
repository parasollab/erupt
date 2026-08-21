using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Erupt.Interaction.EditorTools
{
    /// <summary>Post-migration assertions for KitchenFR3. Read-only.</summary>
    public static class InteractionMigrationVerify
    {
        [MenuItem("ERUPT/Refactor/Verify KitchenFR3")]
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/KitchenFR3.unity", OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();

            var routers = roots.SelectMany(r => r.GetComponentsInChildren<InteractionRouter>(true)).ToList();
            Check(sb, "exactly one router", routers.Count == 1, $"found {routers.Count}");

            var legacy = roots.SelectMany(r => r.GetComponentsInChildren<Quest3ControllerRayInteractor>(true)).ToList();
            Check(sb, "no legacy ray interactors", legacy.Count == 0, $"found {legacy.Count}");

            var backends = roots.SelectMany(r => r.GetComponentsInChildren<InteractionSourceBehaviour>(true)).ToList();
            Check(sb, "two controller backends", backends.Count == 2, $"found {backends.Count}");
            foreach (var b in backends)
                sb.AppendLine($"    backend '{b.name}' modality={b.Modality} caps={b.Capabilities}");

            var visuals = roots.SelectMany(r => r.GetComponentsInChildren<InteractionRayVisual>(true)).ToList();
            Check(sb, "two ray visuals", visuals.Count == 2, $"found {visuals.Count}");

            var binding = roots.SelectMany(r => r.GetComponentsInChildren<RobotInteractionRouterBinding>(true)).FirstOrDefault();
            Check(sb, "robot binding present", binding != null, "missing");

            var selection = roots.SelectMany(r => r.GetComponentsInChildren<SelectionManager>(true)).FirstOrDefault();
            Check(sb, "selection uses router", selection != null &&
                new SerializedObject(selection).FindProperty("interactionRouter").objectReferenceValue != null, "not assigned");

            var wrist = roots.SelectMany(r => r.GetComponentsInChildren<WristMenuController>(true)).FirstOrDefault();
            Check(sb, "wrist menu uses router", wrist != null &&
                new SerializedObject(wrist).FindProperty("interactionRouter").objectReferenceValue != null, "not assigned");

            if (routers.Count == 1)
            {
                int ui = new SerializedObject(routers[0]).FindProperty("uiLayers").intValue;
                Check(sb, "router rejects UI layer", ui != 0, $"uiLayers=0x{ui:X}");
                sb.AppendLine($"    uiLayers = 0x{ui:X}");
            }

            // The rig prefab must be untouched: the wrist menu is a prefab instance whose
            // router assignment is a scene-stored override.
            if (wrist != null)
            {
                bool isInstance = PrefabUtility.IsPartOfPrefabInstance(wrist);
                Check(sb, "wrist menu still a prefab instance", isInstance, "prefab link broken");
            }

            Debug.Log("VERIFY_BEGIN\n" + sb + "VERIFY_END");
        }

        private static void Check(StringBuilder sb, string label, bool ok, string detail) =>
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}" + (ok ? "" : $" — {detail}"));
    }
}
