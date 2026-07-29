using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

public sealed class VisionOSUIDocumentStripper : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!IsVisionOSBuild(report))
            return;

        int stripped = StripUIDocuments(scene, markDirty: false, useUndo: false);
        if (stripped > 0)
            Debug.Log($"VisionOSUIDocumentStripper: stripped {stripped} UIDocument component(s) from build scene '{scene.name}'.");

        int strippedSkinnedMeshes = StripOptimizedSkinnedMeshRenderers(scene, markDirty: false, useUndo: false);
        if (strippedSkinnedMeshes > 0)
            Debug.Log($"VisionOSUIDocumentStripper: stripped {strippedSkinnedMeshes} optimized SkinnedMeshRenderer component(s) from build scene '{scene.name}'.");
    }

    [MenuItem("ERUPT/VisionOS/Report UIDocuments In Open Scenes")]
    private static void ReportUIDocumentsInOpenScenes()
    {
        int total = 0;
        foreach (var document in FindUIDocumentsInOpenScenes())
        {
            total++;
            Debug.Log($"VisionOS UIDocument warning source: {GetPath(document.transform)}", document);
        }

        Debug.Log($"VisionOSUIDocumentStripper: found {total} UIDocument component(s) in open scene(s).");
    }

    [MenuItem("ERUPT/VisionOS/Strip UIDocuments From Open Scenes")]
    private static void StripUIDocumentsFromOpenScenes()
    {
        int total = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                total += StripUIDocuments(scene, markDirty: true, useUndo: true);
        }

        Debug.Log($"VisionOSUIDocumentStripper: stripped {total} UIDocument component(s) from open scene(s).");
    }

    [MenuItem("ERUPT/VisionOS/Report Optimized Skinned Mesh Renderers In Open Scenes")]
    private static void ReportOptimizedSkinnedMeshRenderersInOpenScenes()
    {
        int total = 0;
        foreach (var renderer in FindOptimizedSkinnedMeshRenderersInOpenScenes())
        {
            total++;
            Debug.Log($"VisionOS optimized SkinnedMeshRenderer warning source: {GetPath(renderer.transform)}", renderer);
        }

        Debug.Log($"VisionOSUIDocumentStripper: found {total} optimized SkinnedMeshRenderer component(s) in open scene(s).");
    }

    [MenuItem("ERUPT/VisionOS/Strip Optimized Skinned Mesh Renderers From Open Scenes")]
    private static void StripOptimizedSkinnedMeshRenderersFromOpenScenes()
    {
        int total = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                total += StripOptimizedSkinnedMeshRenderers(scene, markDirty: true, useUndo: true);
        }

        Debug.Log($"VisionOSUIDocumentStripper: stripped {total} optimized SkinnedMeshRenderer component(s) from open scene(s).");
    }

    private static bool IsVisionOSBuild(BuildReport report)
    {
        if (report != null && report.summary.platform.ToString() == "VisionOS")
            return true;

        return EditorUserBuildSettings.activeBuildTarget.ToString() == "VisionOS";
    }

    private static int StripUIDocuments(Scene scene, bool markDirty, bool useUndo)
    {
        int stripped = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (var document in root.GetComponentsInChildren<UIDocument>(true))
            {
                if (document == null)
                    continue;

                if (useUndo)
                    Undo.DestroyObjectImmediate(document);
                else
                    Object.DestroyImmediate(document, true);

                stripped++;
            }
        }

        if (markDirty && stripped > 0)
            EditorSceneManager.MarkSceneDirty(scene);

        return stripped;
    }

    private static int StripOptimizedSkinnedMeshRenderers(Scene scene, bool markDirty, bool useUndo)
    {
        int stripped = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!ShouldStripSkinnedMeshRenderer(renderer))
                    continue;

                if (useUndo)
                    Undo.DestroyObjectImmediate(renderer);
                else
                    Object.DestroyImmediate(renderer, true);

                stripped++;
            }
        }

        if (markDirty && stripped > 0)
            EditorSceneManager.MarkSceneDirty(scene);

        return stripped;
    }

    private static IEnumerable<UIDocument> FindUIDocumentsInOpenScenes()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (var document in root.GetComponentsInChildren<UIDocument>(true))
                    yield return document;
            }
        }
    }

    private static IEnumerable<SkinnedMeshRenderer> FindOptimizedSkinnedMeshRenderersInOpenScenes()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (ShouldStripSkinnedMeshRenderer(renderer))
                        yield return renderer;
                }
            }
        }
    }

    private static bool ShouldStripSkinnedMeshRenderer(SkinnedMeshRenderer renderer)
    {
        if (renderer == null)
            return false;

        return renderer.bones == null || renderer.bones.Length == 0;
    }

    private static string GetPath(Transform transform)
    {
        var names = new List<string>();
        while (transform != null)
        {
            names.Add(transform.name);
            transform = transform.parent;
        }

        names.Reverse();
        return string.Join("/", names);
    }
}
