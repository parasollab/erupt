using UnityEngine;

[ExecuteAlways]
// a
public class LightFlicker : MonoBehaviour
{
    public float minIntensity = 0.5f;
    public float maxIntensity = 1.5f;
    public float flickerSpeed = 0.1f;

    private Light lightSource;
    private float baseIntensity;

    void Start()
    {
        lightSource = GetComponent<Light>();
        if (lightSource != null)
        {
            baseIntensity = lightSource.intensity; 
            InvokeRepeating("Flicker", 0.0f, flickerSpeed); 
        }
    }

    void Flicker()
    {
        lightSource.intensity = baseIntensity * Random.Range(minIntensity, maxIntensity);
    }
}
