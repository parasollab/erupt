using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Bakes MoveItPlanningRequestMenuUI's per-scene study restrictions into the study task
// scenes as prefab-instance overrides: Task2/3 lock the preset start/goal states
// (allowStartGoalEditing = false), Task3 -- where planning isn't part of the task --
// additionally disables the Plan button (allowPlanning = false), and every task scene
// disables Execute Trajectory and Mirror Joint States (participants only plan). Same
// run-once-from-the-menu-and-commit workflow as Task4CollisionObjectSetup.
public static class PlanningMenuRestrictionsSetup
{
    private const string StudyScenesFolder = "Assets/Scenes/Full Study Scenes";

    [MenuItem("Study/Planning Menu/Apply Study Restrictions")]
    public static void ApplyStudyRestrictions()
    {
        ApplyToScenes("Task1_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: false, allowMirroring: false);
        ApplyToScenes("Task2_*.unity", allowStartGoalEditing: false, allowPlanning: true,
            allowExecution: false, allowMirroring: false);
        ApplyToScenes("Task3_*.unity", allowStartGoalEditing: false, allowPlanning: false,
            allowExecution: false, allowMirroring: false);
        ApplyToScenes("Task4_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: false, allowMirroring: false);
    }

    [MenuItem("Study/Planning Menu/Reset Study Restrictions")]
    public static void ResetStudyRestrictions()
    {
        ApplyToScenes("Task1_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: true, allowMirroring: true);
        ApplyToScenes("Task2_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: true, allowMirroring: true);
        ApplyToScenes("Task3_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: true, allowMirroring: true);
        ApplyToScenes("Task4_*.unity", allowStartGoalEditing: true, allowPlanning: true,
            allowExecution: true, allowMirroring: true);
    }

    private static void ApplyToScenes(string pattern, bool allowStartGoalEditing, bool allowPlanning,
        bool allowExecution, bool allowMirroring)
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
                SerializedProperty executeProp = so.FindProperty("allowExecution");
                SerializedProperty mirrorProp = so.FindProperty("allowMirroring");
                if (startGoalProp.boolValue == allowStartGoalEditing && planProp.boolValue == allowPlanning &&
                    executeProp.boolValue == allowExecution && mirrorProp.boolValue == allowMirroring)
                {
                    unchanged++;
                    continue;
                }
                startGoalProp.boolValue = allowStartGoalEditing;
                planProp.boolValue = allowPlanning;
                executeProp.boolValue = allowExecution;
                mirrorProp.boolValue = allowMirroring;
                so.ApplyModifiedPropertiesWithoutUndo();
                changed++;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"PlanningMenuRestrictionsSetup: '{scene.name}' -- " +
                           $"allowStartGoalEditing={allowStartGoalEditing}, allowPlanning={allowPlanning}, " +
                           $"allowExecution={allowExecution}, allowMirroring={allowMirroring}.");
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
