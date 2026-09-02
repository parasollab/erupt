using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Bakes a "HideRobotVisuals" GameObject into the Task2/3 study scenes (the tasks where
// the participant never moves the robot), so the robot renders and collides in the Task1
// and Task4 scenes only. Same run-once-from-the-menu-and-commit workflow as
// Task4CollisionObjectSetup.
public static class HideRobotVisualsSetup
{
    private const string StudyScenesFolder = "Assets/Scenes/Full Study Scenes";
    private const string HiderObjectName = "HideRobotVisuals";
    private const string RobotRootName = "ur5e_robot";

    // The add targets Task2/3 only; the remove also sweeps Task1, so scenes baked by an
    // earlier version of this tool (which included Task1) can be cleaned up.
    private static readonly string[] AddPatterns = { "Task2_*.unity", "Task3_*.unity" };
    private static readonly string[] RemovePatterns = { "Task1_*.unity", "Task2_*.unity", "Task3_*.unity" };

    // Interludes are between-task rest scenes; they get no hider (nothing robot-related
    // happens there, and some have no robot at all).
    private static List<string> FindTaskScenes(string[] patterns)
    {
        return patterns
            .SelectMany(pattern => Directory.GetFiles(StudyScenesFolder, pattern))
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("Interlude"))
            .OrderBy(p => p)
            .ToList();
    }

    [MenuItem("Study/Hide Robot Visuals/Add To Task2-3 Scenes")]
    public static void AddToTask23Scenes()
    {
        List<string> scenePaths = FindTaskScenes(AddPatterns);
        if (scenePaths.Count == 0)
        {
            Debug.LogError($"HideRobotVisualsSetup: no Task2/3 scenes found in {StudyScenesFolder}.");
            return;
        }

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int added = 0, skipped = 0;
        try
        {
            for (int i = 0; i < scenePaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Adding HideRobotVisuals",
                    Path.GetFileNameWithoutExtension(scenePaths[i]), (float)i / scenePaths.Count);
                Scene scene = EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Single);

                if (Object.FindFirstObjectByType<HideRobotVisuals>(FindObjectsInactive.Include) != null)
                {
                    skipped++;
                    continue;
                }
                if (GameObject.Find(RobotRootName) == null)
                {
                    Debug.LogWarning($"HideRobotVisualsSetup: no '{RobotRootName}' in '{scene.name}' -- " +
                                      "skipping, there is no robot to hide.");
                    continue;
                }

                GameObject hider = new GameObject(HiderObjectName);
                hider.AddComponent<HideRobotVisuals>();
                added++;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"HideRobotVisualsSetup: added '{HiderObjectName}' to '{scene.name}'.");
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ReopenOriginalScene(originalScenePath);
        }
        Debug.Log($"HideRobotVisualsSetup: done -- added {added}, skipped {skipped} already-configured, " +
                   $"of {scenePaths.Count} scene(s).");
    }

    [MenuItem("Study/Hide Robot Visuals/Remove From Task1-3 Scenes")]
    public static void RemoveFromTask123Scenes()
    {
        List<string> scenePaths = FindTaskScenes(RemovePatterns);
        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int removed = 0;
        try
        {
            foreach (string scenePath in scenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                HideRobotVisuals[] hiders =
                    Object.FindObjectsByType<HideRobotVisuals>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (hiders.Length == 0) continue;

                foreach (HideRobotVisuals hider in hiders)
                {
                    Object.DestroyImmediate(hider.gameObject, true);
                    removed++;
                }
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"HideRobotVisualsSetup: removed {hiders.Length} hider(s) from '{scene.name}'.");
            }
        }
        finally
        {
            ReopenOriginalScene(originalScenePath);
        }
        Debug.Log($"HideRobotVisualsSetup: removed {removed} hider object(s) total.");
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
