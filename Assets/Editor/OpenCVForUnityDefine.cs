using System.IO;
using UnityEditor;
using UnityEngine;

/// Automatically adds OPENCV_FOR_UNITY to scripting defines when the OpenCVForUnity
/// asset package is present in the project, and removes it when the folder is absent.
/// This keeps the define in sync with the package without any manual setup.
[InitializeOnLoad]
public static class OpenCVForUnityDefine
{
    const string Define = "OPENCV_FOR_UNITY";
    static readonly string PackagePath = Path.Combine(Application.dataPath, "OpenCVForUnity");

    static OpenCVForUnityDefine()
    {
        bool present = Directory.Exists(PackagePath);
        SetDefine(present);
    }

    static void SetDefine(bool enable)
    {
        var targets = new[]
        {
            BuildTargetGroup.Android,
            BuildTargetGroup.Standalone,
        };

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
