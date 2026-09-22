using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// Persistent pool used by StudyWorldSlot. New instances are created beneath an inactive
/// root, preventing Awake/OnEnable from running until a prepared slot is revealed.
/// </summary>
[DefaultExecutionOrder(-9000)]
public sealed class StudyPrefabPool : MonoBehaviour
{
    private static StudyPrefabPool s_Instance;

    private readonly Dictionary<GameObject, Stack<GameObject>> _available =
        new Dictionary<GameObject, Stack<GameObject>>();
    private readonly Dictionary<GameObject, GameObject> _sourceByInstance =
        new Dictionary<GameObject, GameObject>();
    private Transform _inactiveRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        EnsureInstance();
    }

    public static GameObject Rent(GameObject prefab, Transform parent)
    {
        if (prefab == null)
        {
            return null;
        }

        StudyPrefabPool pool = EnsureInstance();
        if (!pool._available.TryGetValue(prefab, out Stack<GameObject> entries))
        {
            entries = new Stack<GameObject>();
            pool._available.Add(prefab, entries);
        }

        GameObject instance = null;
        while (entries.Count > 0 && instance == null)
        {
            instance = entries.Pop();
        }

        if (instance == null)
        {
            // _inactiveRoot is inactive in the hierarchy, so prefab callbacks do not run here.
            instance = Instantiate(prefab, pool._inactiveRoot, false);
            instance.name = prefab.name;
            instance.SetActive(false);
            pool._sourceByInstance[instance] = prefab;
        }

        instance.transform.SetParent(parent, false);
        return instance;
    }

    public static void Release(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        StudyPrefabPool pool = EnsureInstance();
        if (!pool._sourceByInstance.TryGetValue(instance, out GameObject prefab) || prefab == null)
        {
            Destroy(instance);
            return;
        }

        instance.SetActive(false);
        instance.transform.SetParent(pool._inactiveRoot, false);
        if (!pool._available.TryGetValue(prefab, out Stack<GameObject> entries))
        {
            entries = new Stack<GameObject>();
            pool._available.Add(prefab, entries);
        }
        entries.Push(instance);
    }

    public static IEnumerator PrewarmIncrementally(
        IEnumerable<StudyScenarioDefinition> scenarios,
        float frameBudgetMilliseconds = 1.5f)
    {
        if (scenarios == null)
        {
            yield break;
        }

        StudyPrefabPool pool = EnsureInstance();
        HashSet<GameObject> warmed = new HashSet<GameObject>();
        Stopwatch stopwatch = Stopwatch.StartNew();
        float budget = Mathf.Max(0.1f, frameBudgetMilliseconds);

        foreach (StudyScenarioDefinition scenario in scenarios)
        {
            if (scenario == null)
            {
                continue;
            }

            foreach (StudyScenarioDefinition.ObjectPlacement placement in scenario.objects)
            {
                GameObject prefab = placement?.prefab;
                if (prefab == null || !warmed.Add(prefab))
                {
                    continue;
                }

                GameObject instance = Rent(prefab, pool._inactiveRoot);
                Release(instance);

                // Instantiating one unusually large prefab cannot be preempted, so never
                // instantiate a second one in the same frame after the budget is consumed.
                if (stopwatch.Elapsed.TotalMilliseconds >= budget)
                {
                    stopwatch.Restart();
                    yield return null;
                }
            }
        }
    }

    private static StudyPrefabPool EnsureInstance()
    {
        if (s_Instance != null)
        {
            return s_Instance;
        }

        GameObject host = new GameObject(nameof(StudyPrefabPool));
        DontDestroyOnLoad(host);
        s_Instance = host.AddComponent<StudyPrefabPool>();
        return s_Instance;
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

        GameObject inactiveRootObject = new GameObject("Inactive Pooled Objects");
        inactiveRootObject.transform.SetParent(transform, false);
        _inactiveRoot = inactiveRootObject.transform;
        inactiveRootObject.SetActive(false);
    }
}
