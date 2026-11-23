#if UNITY_EDITOR_OSX && ERUPT_ISDK_207_0_0
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace Erupt.App
{
    internal static class InteractionSdkEditorShutdown
    {
        [DllImport("ISDKEngineTelemetry", CallingConvention = CallingConvention.Cdecl)]
        private static extern int engineTelemetry_OnEditorShutdown();

        [InitializeOnLoadMethod]
        private static void Register()
        {
            // Interaction SDK 207 starts telemetry on editor load, even outside Play Mode.
            if (!AssetDatabase.IsAssetImportWorkerProcess())
                EditorApplication.quitting += OnEditorQuitting;
        }

        private static void OnEditorQuitting()
        {
            try
            {
                if (engineTelemetry_OnEditorShutdown() == 0)
                    Debug.LogWarning("Interaction SDK telemetry reported a failure during editor shutdown.");
                else
                    Debug.Log("Interaction SDK editor telemetry shutdown completed.");
            }
            catch (System.DllNotFoundException)
            {
                // The native plug-in can be disabled for this editor.
            }
        }
    }
}
#endif
