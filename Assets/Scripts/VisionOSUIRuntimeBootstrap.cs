using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Normalizes world-space UGUI and UI Toolkit panels for PolySpatial/visionOS.
/// </summary>
[DisallowMultipleComponent]
public sealed class VisionOSUIRuntimeBootstrap : MonoBehaviour
{
    const string BootstrapName = "VisionOS UI Runtime Bootstrap";

    static VisionOSUIRuntimeBootstrap s_Instance;
    bool m_LoggedSummary;
    int m_LastCanvasCount;
    int m_LastDocumentCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (!ShouldInstallForCurrentPlatform() || s_Instance != null)
            return;

        var go = new GameObject(BootstrapName);
        DontDestroyOnLoad(go);
        s_Instance = go.AddComponent<VisionOSUIRuntimeBootstrap>();
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
        StartCoroutine(NormalizeAfterSceneSettles());
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (s_Instance == this)
            s_Instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StopAllCoroutines();
        StartCoroutine(NormalizeAfterSceneSettles());
    }

    IEnumerator NormalizeAfterSceneSettles()
    {
        for (int i = 0; i < 3; i++)
        {
            yield return null;
            NormalizeSceneUI();
        }

        if (!m_LoggedSummary)
        {
            m_LoggedSummary = true;
            Debug.Log($"VisionOSUIRuntimeBootstrap: normalized {m_LastCanvasCount} world-space Canvas object(s) and {m_LastDocumentCount} UIDocument object(s).");
        }
    }

    void NormalizeSceneUI()
    {
        EnsureEventSystem();
        m_LastCanvasCount = NormalizeCanvases();
        m_LastDocumentCount = NormalizeUIDocuments();
    }

    static void EnsureEventSystem()
    {
        var eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (eventSystems.Length > 0)
        {
            foreach (var eventSystem in eventSystems)
                NormalizeEventSystem(eventSystem.gameObject);
            return;
        }

        var eventSystemObject = new GameObject("EventSystem");
        eventSystemObject.AddComponent<EventSystem>();
        NormalizeEventSystem(eventSystemObject);
    }

    static void NormalizeEventSystem(GameObject eventSystemObject)
    {
        if (eventSystemObject.GetComponent<InputSystemUIInputModule>() == null)
            eventSystemObject.AddComponent<InputSystemUIInputModule>();

        foreach (var inputModule in eventSystemObject.GetComponents<BaseInputModule>())
        {
            if (inputModule is InputSystemUIInputModule)
                inputModule.enabled = true;
            else
                inputModule.enabled = false;
        }
    }

    static int NormalizeCanvases()
    {
        int normalizedCount = 0;
        var camera = Camera.main;
        var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var canvas in canvases)
        {
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
                continue;

            if (canvas.worldCamera == null && camera != null)
                canvas.worldCamera = camera;

            canvas.overrideSorting = true;
            canvas.sortingOrder = Mathf.Max(canvas.sortingOrder, 100);

            var standardRaycaster = canvas.GetComponent<GraphicRaycaster>();
            if (standardRaycaster == null)
                standardRaycaster = canvas.gameObject.AddComponent<GraphicRaycaster>();
            standardRaycaster.enabled = true;

            foreach (var trackedRaycaster in canvas.GetComponents<TrackedDeviceGraphicRaycaster>())
                trackedRaycaster.enabled = false;

            EnsureCanvasCollider(canvas);
            normalizedCount++;
        }

        return normalizedCount;
    }

    static void EnsureCanvasCollider(Canvas canvas)
    {
        if (canvas.GetComponent<Collider>() != null)
            return;

        var rectTransform = canvas.GetComponent<RectTransform>();
        if (rectTransform == null)
            return;

        var collider = canvas.gameObject.AddComponent<BoxCollider>();
        collider.isTrigger = false;
        collider.size = new Vector3(
            Mathf.Max(0.01f, rectTransform.rect.width),
            Mathf.Max(0.01f, rectTransform.rect.height),
            10f);
    }

    static int NormalizeUIDocuments()
    {
        int normalizedCount = 0;
        var documents = FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var document in documents)
        {
            if (document == null)
                continue;

            document.enabled = false;
            normalizedCount++;
        }

        return normalizedCount;
    }
}
