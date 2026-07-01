using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

public static class RosConnectionBootstrapper
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsurePersistentConnection()
    {
        ROSConnection ros = ROSConnection.GetOrCreateInstance();
        Object.DontDestroyOnLoad(ros.gameObject);
    }
}
