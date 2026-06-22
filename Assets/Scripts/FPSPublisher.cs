using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

public class FPSPublisher : MonoBehaviour
{
    public string topic = "/fps";
    [SerializeField] private InputActionReference _togglePublishAction;

    private ROSConnection ros;
    private float elapsedSincePublish;
    private float sumFps;
    private uint fpsSamples;
    private bool _isPublishing = false;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<Int32Msg>(topic);
        _togglePublishAction.action.performed += OnTogglePublish;
    }

    private void OnTogglePublish(InputAction.CallbackContext context)
    {
        _isPublishing = !_isPublishing;
    }

    void Update()
    {
        if (!_isPublishing) return;

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

    void OnDestroy()
    {
        _togglePublishAction.action.performed -= OnTogglePublish;
    }
}
