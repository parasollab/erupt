using UnityEngine;

public class FPSSetter : MonoBehaviour
{
    [SerializeField] private float targetFPS = 90.0f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        OVRPlugin.systemDisplayFrequency = targetFPS;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
