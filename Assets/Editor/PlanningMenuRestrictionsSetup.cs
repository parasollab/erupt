using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Bakes MoveItPlanningRequestMenuUI's per-scene study restrictions into the Task2/3 study
// scenes as prefab-instance overrides: both tasks lock the preset start/goal states
// (allowStartGoalEditing = false), and Task3 -- where planning isn't part of the task --
// additionally disables the Plan button (allowPlanning = false). Task1/Task4 keep the
// prefab defaults (everything allowed). Same run-once-from-the-menu-and-commit workflow
// as Task4CollisionObjectSetup.
public static class PlanningMenuRestrictionsSetup
{
    private const string StudyScenesFolder = "Assets/Scenes/Full Study Scenes";

    [MenuItem("Study/Planning Menu/Apply Task2-3 Restrictions")]
    public static void ApplyTask23Restrictions()
    {
        ApplyToScenes("Task2_*.unity", allowStartGoalEditing: false, allowPlanning: true);
        ApplyToScenes("Task3_*.unity", allowStartGoalEditing: false, allowPlanning: false);
    }

    [MenuItem("Study/Planning Menu/Reset Task2-3 Restrictions")]
    public static void ResetTask23Restrictions()
    {
        ApplyToScenes("Task2_*.unity", allowStartGoalEditing: true, allowPlanning: true);
        ApplyToScenes("Task3_*.unity", allowStartGoalEditing: true, allowPlanning: true);
    }

    private static void ApplyToScenes(string pattern, bool allowStartGoalEditing, bool allowPlanning)
    {
        // Interludes are between-task rest scenes without a planning menu.
        List<string> scenePaths = Directory.GetFiles(StudyScenesFolder, pattern)
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("Interlude"))
            .OrderBy(p => p)
            .ToList();
        if (scenePaths.Count == 0)
        {
            Debug.LogError($"PlanningMenuRestrictionsSetup: no scenes match {pattern} in {StudyScenesFolder}.");
            return;
        }

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int changed = 0, unchanged = 0;
        try
        {
            for (int i = 0; i < scenePaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Applying planning menu restrictions",
                    Path.GetFileNameWithoutExtension(scenePaths[i]), (float)i / scenePaths.Count);
                Scene scene = EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Single);

                MoveItPlanningRequestMenuUI menu =
                    Object.FindFirstObjectByType<MoveItPlanningRequestMenuUI>(FindObjectsInactive.Include);
                if (menu == null)
                {
                    Debug.LogWarning($"PlanningMenuRestrictionsSetup: no MoveItPlanningRequestMenuUI " +
                                      $"in '{scene.name}' -- skipping.");
                    continue;
                }

                // SerializedObject writes the private fields as proper prefab-instance
                // overrides (and no-ops cleanly when the values already match).
                SerializedObject so = new SerializedObject(menu);
                SerializedProperty startGoalProp = so.FindProperty("allowStartGoalEditing");
                SerializedProperty planProp = so.FindProperty("allowPlanning");
                if (startGoalProp.boolValue == allowStartGoalEditing && planProp.boolValue == allowPlanning)
                {
                    unchanged++;
                    continue;
                }
                startGoalProp.boolValue = allowStartGoalEditing;
                planProp.boolValue = allowPlanning;
                so.ApplyModifiedPropertiesWithoutUndo();
                changed++;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"PlanningMenuRestrictionsSetup: '{scene.name}' -- " +
                           $"allowStartGoalEditing={allowStartGoalEditing}, allowPlanning={allowPlanning}.");
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ReopenOriginalScene(originalScenePath);
        }
        Debug.Log($"PlanningMenuRestrictionsSetup: {pattern} -- {changed} scene(s) updated, " +
                   $"{unchanged} already configured.");
    }

    private static void ReopenOriginalScene(string originalScenePath)
    {
        if (!string.IsNullOrEmpty(originalScenePath) &&
            originalScenePath != EditorSceneManager.GetActiveScene().path)
        {
            EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }
    }
}
