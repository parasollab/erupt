using NUnit.Framework;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// A connector connection that never connects, for a test that loads a whole scene.
    /// </summary>
    /// <remarks>
    /// A fake bus keeps ERUPT's own traffic off the network, but a scene may carry something
    /// that asks the connector for its connection directly (the connector's visualization suite
    /// does). With no connection in place the connector makes one, which opens a socket to ROS,
    /// tries again every second and logs each failure from its own thread. The connector does
    /// not stop that thread when the scene is unloaded, so the lines keep arriving during later
    /// tests, and one that lands in a test asserting that nothing was logged fails it.
    /// Create this before loading the scene and the connector hands it out instead; destroy the
    /// returned object in the teardown.
    /// </remarks>
    public static class IdleRosConnection
    {
        public static GameObject Create()
        {
            var host = new GameObject("ROSConnection (test: never connects)");
            // Awake makes it the connector's instance; ConnectOnStart is read in Start, a frame later.
            ROSConnection connection = host.AddComponent<ROSConnection>();
            connection.ConnectOnStart = false;
            Assert.AreSame(connection, ROSConnection.GetOrCreateInstance(),
                "Another ROSConnection is alive (left by an earlier test?), so the scene would use that one.");
            return host;
        }
    }
}
