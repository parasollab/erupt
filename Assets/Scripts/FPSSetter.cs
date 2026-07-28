using UnityEngine;

public class FPSSetter : MonoBehaviour
{
    [SerializeField] private float targetFPS = 90.0f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
#if ERUPT_USE_META_XR && UNITY_ANDROID && !UNITY_VISIONOS
        OVRPlugin.systemDisplayFrequency = targetFPS;
#else
        enabled = false;
#endif
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
