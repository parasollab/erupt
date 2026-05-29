using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

public class FPSPublisher : MonoBehaviour
{
    public string topic = "/fps";

    private ROSConnection ros;
    private float elapsedSincePublish;
    private float sumFps;
    private uint fpsSamples;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<Int32Msg>(topic);
        
    }

    void Update()
    {
        sumFps += 1.0f / Time.smoothDeltaTime;
        fpsSamples++;
        elapsedSincePublish += Time.deltaTime;

        if (elapsedSincePublish >= 1f)
        {
            int avgFps = Mathf.RoundToInt(sumFps / fpsSamples);
            Debug.Log($"[FPS] {avgFps}");
            ros.Publish(topic, new Int32Msg(avgFps));
            sumFps = 0f;
            fpsSamples = 0;
            elapsedSincePublish = 0f;
        }
    }
}
