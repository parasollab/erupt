using Unity.PolySpatial;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Ensures the scene owns the visionOS runtime components that PolySpatial/ARKit input
/// and world sensing depend on. This keeps older scenes from relying on implicit setup.
/// </summary>
[DisallowMultipleComponent]
public sealed class VisionOSSceneBootstrap : MonoBehaviour
{
    const string BootstrapName = "VisionOS Scene Bootstrap";
    const string ARSessionName = "AR Session";
    const string VolumeCameraName = "VisionOS Volume Camera";

    static VisionOSSceneBootstrap s_Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (!ShouldInstallForCurrentPlatform() || s_Instance != null)
            return;

        var go = new GameObject(BootstrapName);
        DontDestroyOnLoad(go);
        s_Instance = go.AddComponent<VisionOSSceneBootstrap>();
    }

    static bool ShouldInstallForCurrentPlatform()
    {
#if UNITY_EDITOR
        return EditorUserBuildSettings.activeBuildTarget.ToString() == "VisionOS";
#elif UNITY_VISIONOS
        return true;
#else
        return false;
#endif
    }

    void Awake()
    {
        if (s_Instance != null && s_Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureSceneComponents();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (s_Instance == this)
            s_Instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureSceneComponents();
    }

    void EnsureSceneComponents()
    {
        EnsureARSession();
        EnsureVolumeCamera();
        EnsurePlaneManager();
    }

    static void EnsureARSession()
    {
        if (FindAnyObjectByType<ARSession>() != null)
            return;

        var sessionObject = new GameObject(ARSessionName);
        sessionObject.AddComponent<ARSession>();
    }

    static void EnsureVolumeCamera()
    {
        if (FindAnyObjectByType<VolumeCamera>() != null)
            return;

        var volumeObject = new GameObject(VolumeCameraName);
        var volumeCamera = volumeObject.AddComponent<VolumeCamera>();
        volumeCamera.CullingMask = ~0;
    }

    static void EnsurePlaneManager()
    {
        if (FindAnyObjectByType<ARPlaneManager>() != null)
            return;

        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
        {
            Debug.LogWarning("VisionOSSceneBootstrap: No XROrigin found; ARPlaneManager was not added.");
            return;
        }

        var planeManager = origin.gameObject.AddComponent<ARPlaneManager>();
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
    }
}
