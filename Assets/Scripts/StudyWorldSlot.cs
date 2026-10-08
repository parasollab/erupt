using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// One half of a double-buffered world. Prepare the hidden slot from pooled objects,
/// reveal it incrementally behind the compositor layer, then recycle the previous slot.
/// </summary>
public sealed class StudyWorldSlot : MonoBehaviour
{
    private readonly List<GameObject> _instances = new List<GameObject>();

    public StudyScenarioDefinition Scenario { get; private set; }

    public IEnumerator PrepareIncrementally(
        StudyScenarioDefinition scenario,
        float frameBudgetMilliseconds = 1.5f)
    {
        Recycle();
        Scenario = scenario;
        if (scenario == null)
        {
            yield break;
        }

        float budget = Mathf.Max(0.1f, frameBudgetMilliseconds);
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (StudyScenarioDefinition.ObjectPlacement placement in scenario.objects)
        {
            if (placement == null || placement.prefab == null)
            {
                continue;
            }

            GameObject instance = StudyPrefabPool.Rent(placement.prefab, transform);
            Transform instanceTransform = instance.transform;
            instanceTransform.localPosition = placement.localPosition;
            instanceTransform.localRotation = Quaternion.Euler(placement.localEulerAngles);
            instanceTransform.localScale = placement.localScale;
            instance.SetActive(false);
            _instances.Add(instance);

            if (stopwatch.Elapsed.TotalMilliseconds >= budget)
            {
                stopwatch.Restart();
                yield return null;
            }
        }
    }

    public IEnumerator RevealIncrementally(float frameBudgetMilliseconds = 1.5f)
    {
        float budget = Mathf.Max(0.1f, frameBudgetMilliseconds);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_instances[i] != null)
            {
                _instances[i].SetActive(true);
            }

            if (stopwatch.Elapsed.TotalMilliseconds >= budget)
            {
                stopwatch.Restart();
                yield return null;
            }
        }
    }

    public void Hide()
    {
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_instances[i] != null)
            {
                _instances[i].SetActive(false);
            }
        }
    }

    public void Recycle()
    {
        for (int i = 0; i < _instances.Count; i++)
        {
            StudyPrefabPool.Release(_instances[i]);
        }
        _instances.Clear();
        Scenario = null;
    }
}
