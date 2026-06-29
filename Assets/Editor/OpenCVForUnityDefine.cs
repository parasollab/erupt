using System.IO;
using UnityEditor;
using UnityEngine;

/// Automatically adds/removes OPENCV_FOR_UNITY from scripting defines based on whether
/// the OpenCVForUnity asset package is present in Assets/OpenCVForUnity.
///
/// Uses two hooks:
///   [InitializeOnLoad]          — syncs the define on every domain reload (startup, script change).
///   AssetModificationProcessor  — removes the define BEFORE the folder is deleted so the
///                                  next compilation doesn't see a stale define.
[InitializeOnLoad]
public static class OpenCVForUnityDefine
{
    const string Define = "OPENCV_FOR_UNITY";
    const string PackageFolder = "Assets/OpenCVForUnity";

    static readonly string PackagePath = Path.Combine(Application.dataPath, "OpenCVForUnity");

    static OpenCVForUnityDefine()
    {
        SetDefine(Directory.Exists(PackagePath));
    }

    public static void SetDefine(bool enable)
    {
        var targets = new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone };

        foreach (var group in targets)
        {
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            bool hasDef = defines.Contains(Define);

            if (enable && !hasDef)
            {
                defines = string.IsNullOrEmpty(defines) ? Define : defines + ";" + Define;
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
                Debug.Log($"[OpenCVForUnityDefine] Added {Define} for {group}");
            }
            else if (!enable && hasDef)
            {
                defines = defines.Replace(";" + Define, "").Replace(Define + ";", "").Replace(Define, "");
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
                Debug.Log($"[OpenCVForUnityDefine] Removed {Define} for {group}");
            }
        }
    }
}

/// Fires before an asset is deleted so we can remove the define before compilation
/// sees the OpenCV folder as gone. Without this, deletion outside the Unity Project
/// window leaves a stale define that causes a compile error on next reload.
public class OpenCVForUnityDeleteWatcher : AssetModificationProcessor
{
    static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
    {
        if (path == "Assets/OpenCVForUnity" || path.StartsWith("Assets/OpenCVForUnity/"))
            OpenCVForUnityDefine.SetDefine(false);

        return AssetDeleteResult.DidNotDelete;
    }
}
