using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Generates a non-destructive set of streaming study scenes. StartScene becomes the
/// persistent bootstrap (using Tutorial's known-good XR/platform roots); every other
/// generated scene has those duplicate roots removed. Source scenes are never modified.
/// </summary>
public static class PersistentStudySceneMigration
{
    private const string SourceFolder = "Assets/Scenes/Full Study Scenes";
    private const string OutputFolder = "Assets/Scenes/Streaming Study Scenes";
    private const string StartScenePath = SourceFolder + "/StartScene.unity";
    private const string BootstrapTemplatePath = SourceFolder + "/Tutorial.unity";

    [MenuItem("ERUPT/Study/Migrate To Persistent XR Streaming Scenes")]
    public static void RunMigrationFromMenu()
    {
        bool proceed = EditorUtility.DisplayDialog(
            "Migrate study scenes",
            "Generate streaming copies, create a persistent-XR StartScene, and point Build Settings at the generated copies? Original scenes are not changed.",
            "Migrate",
            "Cancel");

        if (proceed)
        {
            RunMigration();
        }
    }

    // Public entry point for -executeMethod in CI or a batch-mode Unity editor.
    public static void RunMigration()
    {
        SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            ValidateInputs();
            EnsureOutputFolder();
            CreateBootstrapScene();

            string[] sourceScenes = AssetDatabase.FindAssets("t:Scene", new[] { SourceFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .Where(path => path != StartScenePath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < sourceScenes.Length; i++)
            {
                string sourcePath = sourceScenes[i];
                EditorUtility.DisplayProgressBar(
                    "Migrating study scenes",
                    Path.GetFileNameWithoutExtension(sourcePath),
                    (i + 1f) / (sourceScenes.Length + 1f));
                CreateStreamingContentScene(sourcePath);
            }

            UpdateBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"PersistentStudySceneMigration: generated {sourceScenes.Length + 1} scenes in '{OutputFolder}'.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (previousSetup != null && previousSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }
    }

    [MenuItem("ERUPT/Study/Validate Persistent XR Streaming Scenes")]
    public static void ValidateGeneratedScenes()
    {
        string[] buildPaths = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        int generatedCount = buildPaths.Count(path => path.StartsWith(OutputFolder + "/", StringComparison.Ordinal));
        if (generatedCount == 0)
        {
            Debug.LogWarning("PersistentStudySceneMigration: Build Settings do not reference generated streaming scenes yet.");
            return;
        }

        string generatedStart = OutputFolder + "/StartScene.unity";
        Scene scene = EditorSceneManager.OpenScene(generatedStart, OpenSceneMode.Single);
        List<string> missing = new List<string>();
        string[] requiredRoots =
        {
            "XR Origin (XR Rig)", "XR Interaction Manager", "EventSystem",
            "PanelInputConfiguration", "XR UI Toolkit Manager", "OVRManager"
        };

        HashSet<string> rootNames = new HashSet<string>(scene.GetRootGameObjects().Select(root => root.name));
        foreach (string requiredRoot in requiredRoots)
        {
            if (!rootNames.Contains(requiredRoot))
            {
                missing.Add(requiredRoot);
            }
        }

        if (scene.GetRootGameObjects().All(root => root.GetComponent<StudyController>() == null))
        {
            missing.Add(nameof(StudyController));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Generated StartScene is missing: " + string.Join(", ", missing));
        }

        Debug.Log($"PersistentStudySceneMigration: validation passed ({generatedCount} generated scenes in Build Settings).\n" +
                  "Run a Quest Development Build and capture CPU Usage + Asset Loading profiler modules before deleting source scenes.");
    }

    private static void ValidateInputs()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(StartScenePath) == null)
        {
            throw new FileNotFoundException("Study StartScene was not found.", StartScenePath);
        }

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(BootstrapTemplatePath) == null)
        {
            throw new FileNotFoundException("Tutorial bootstrap template was not found.", BootstrapTemplatePath);
        }
    }

    private static void EnsureOutputFolder()
    {
        if (!AssetDatabase.IsValidFolder(OutputFolder))
        {
            AssetDatabase.CreateFolder("Assets/Scenes", "Streaming Study Scenes");
        }
    }

    private static void CreateBootstrapScene()
    {
        Scene bootstrap = EditorSceneManager.OpenScene(BootstrapTemplatePath, OpenSceneMode.Single);
        foreach (GameObject root in bootstrap.GetRootGameObjects())
        {
            if (!PersistentXRInfrastructure.IsPersistentRootName(root.name))
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        Scene sourceStart = EditorSceneManager.OpenScene(StartScenePath, OpenSceneMode.Additive);
        GameObject controllerRoot = sourceStart.GetRootGameObjects()
            .FirstOrDefault(root => root.GetComponent<StudyController>() != null);
        if (controllerRoot == null)
        {
            throw new InvalidOperationException("StartScene does not contain a StudyController root.");
        }

        GameObject controllerClone = UnityEngine.Object.Instantiate(controllerRoot);
        controllerClone.name = controllerRoot.name;
        SceneManager.MoveGameObjectToScene(controllerClone, bootstrap);
        EditorSceneManager.CloseScene(sourceStart, true);

        EditorSceneManager.SetActiveScene(bootstrap);
        EditorSceneManager.MarkSceneDirty(bootstrap);
        EditorSceneManager.SaveScene(bootstrap, OutputFolder + "/StartScene.unity", true);
    }

    private static void CreateStreamingContentScene(string sourcePath)
    {
        Scene scene = EditorSceneManager.OpenScene(sourcePath, OpenSceneMode.Single);
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (PersistentXRInfrastructure.IsPersistentRootName(root.name))
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        string outputPath = OutputFolder + "/" + Path.GetFileName(sourcePath);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, outputPath, true);
    }

    private static void UpdateBuildSettings()
    {
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            string path = scenes[i].path;
            if (!scenes[i].enabled || !path.StartsWith(SourceFolder + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string generatedPath = OutputFolder + "/" + Path.GetFileName(path);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(generatedPath) == null)
            {
                throw new FileNotFoundException("Generated scene is missing.", generatedPath);
            }

            scenes[i] = new EditorBuildSettingsScene(generatedPath, true);
        }

        EditorBuildSettings.scenes = scenes;
    }
}
