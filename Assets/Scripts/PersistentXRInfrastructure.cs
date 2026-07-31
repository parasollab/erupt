using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Owns the XR/platform objects that must survive study-content changes. The generated
/// bootstrap scene contains one copy of each root. Duplicate copies in legacy scenes are
/// disabled from sceneLoaded before the next rendered frame, then destroyed.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class PersistentXRInfrastructure : MonoBehaviour
{
    private static readonly HashSet<string> PersistentRootNames = new HashSet<string>
    {
        "XR Origin (XR Rig)",
        "XR Interaction Manager",
        "EventSystem",
        "PanelInputConfiguration",
        "XR UI Toolkit Manager",
        "OVRManager",
    };

    private static PersistentXRInfrastructure s_Instance;
    private readonly Dictionary<string, GameObject> _roots = new Dictionary<string, GameObject>();
    private XROrigin _xrOrigin;

    public static XROrigin XROrigin => s_Instance != null ? s_Instance._xrOrigin : null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (s_Instance != null)
        {
            return;
        }

        GameObject host = new GameObject(nameof(PersistentXRInfrastructure));
        DontDestroyOnLoad(host);
        s_Instance = host.AddComponent<PersistentXRInfrastructure>();
    }

    public static bool IsPersistentRootName(string rootName)
    {
        return !string.IsNullOrEmpty(rootName) && PersistentRootNames.Contains(rootName);
    }

    public static XROrigin ResolveXROrigin(XROrigin fallback = null)
    {
        if (s_Instance != null && s_Instance._xrOrigin != null)
        {
            return s_Instance._xrOrigin;
        }

        if (fallback != null)
        {
            return fallback;
        }

        return FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
    }

    private void Awake()
    {
        if (s_Instance != null && s_Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null || !IsPersistentRootName(root.name))
            {
                continue;
            }

            if (!_roots.TryGetValue(root.name, out GameObject persistentRoot) || persistentRoot == null)
            {
                _roots[root.name] = root;
                DontDestroyOnLoad(root);

                XROrigin origin = root.GetComponentInChildren<XROrigin>(true);
                if (origin != null)
                {
                    _xrOrigin = origin;
                }

                continue;
            }

            if (root != persistentRoot)
            {
                // Deactivation is immediate, so duplicate cameras, event systems, and input
                // managers never participate in the next rendered frame.
                root.SetActive(false);
                Destroy(root);
            }
        }

        if (_xrOrigin == null)
        {
            _xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        }
    }

    private void OnDestroy()
    {
        if (s_Instance == this)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            s_Instance = null;
        }
    }
}
