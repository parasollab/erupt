using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using Erupt.Ros.Tests;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// The live bus as an <see cref="IRosEndpoint"/>: it says where the connector connects,
    /// gives a connection that has not started yet its address, and moves a running one.
    /// Loopback only: a port nothing listens on and a listener of the test's own.
    /// </summary>
    public class LiveRosBusEndpointTests
    {
        private GameObject connectionGo;
        private TcpListener listener;
        private TcpClient accepted;

        [TearDown]
        public void TearDown()
        {
            ROSConnection connection = connectionGo != null ? connectionGo.GetComponent<ROSConnection>() : null;
            if (connection != null) connection.Disconnect();
            accepted?.Close();
            accepted = null;
            listener?.Stop();
            listener = null;
            if (connectionGo != null) UnityEngine.Object.DestroyImmediate(connectionGo);
        }

        // A port nothing listens on: taken from the system, then given back.
        private static int RefusingPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [UnityTest]
        public IEnumerator BeforeTheConnectionStarts_ItIsOnlyGivenTheAddress()
        {
            connectionGo = IdleRosConnection.Create();
            ROSConnection connection = connectionGo.GetComponent<ROSConnection>();
            connection.RosIPAddress = "127.0.0.1";
            connection.RosPort = 10000;
            var bus = new LiveRosBus();

            Assert.AreEqual("127.0.0.1", bus.DefaultHost, "What the connection came with is what the build carries.");
            Assert.AreEqual(10000, bus.DefaultPort);
            Assert.AreEqual("127.0.0.1", bus.Host);
            Assert.IsFalse(bus.HasConnectionThread, "Asking where the bus connects opens nothing.");

            bus.ConnectTo("127.0.0.2", 10001);
            Assert.AreEqual("127.0.0.2", connection.RosIPAddress, "Its Start will connect there.");
            Assert.AreEqual(10001, connection.RosPort);
            Assert.AreEqual("127.0.0.2", bus.Host);
            Assert.AreEqual(10001, bus.Port);
            Assert.AreEqual("127.0.0.1", bus.DefaultHost, "The built-in address is still known.");
            Assert.AreEqual(10000, bus.DefaultPort);
            Assert.IsFalse(connection.HasConnectionThread, "The connection has not started; the bus does not start it early.");

            yield return null;   // Start: this connection is set not to connect by itself
            Assert.IsFalse(connection.HasConnectionThread);
            Assert.Throws<ArgumentException>(() => bus.ConnectTo("", 10000));
        }

        [UnityTest]
        public IEnumerator ARequestMadeBeforeThereIsAConnection_IsOnItFromTheStart()
        {
            var bus = new LiveRosBus();
            string builtIn = bus.DefaultHost;
            bus.ConnectTo("127.0.0.3", 10002);
            Assert.IsNull(UnityEngine.Object.FindFirstObjectByType<ROSConnection>(), "Setting the address creates no connection.");
            Assert.AreEqual("127.0.0.3", bus.Host);
            Assert.AreEqual(10002, bus.Port);

            // The first subscription creates the connection, as a plugin's would.
            bus.Subscribe<StringMsg>("/erupt_endpoint_test_in", _ => { });
            ROSConnection connection = UnityEngine.Object.FindFirstObjectByType<ROSConnection>();
            Assert.IsNotNull(connection);
            connectionGo = connection.gameObject;
            connection.ConnectOnStart = false;   // the test opens no socket to an address that is not its own
            Assert.AreEqual("127.0.0.3", connection.RosIPAddress, "Before its Start, so the first connection goes there.");
            Assert.AreEqual(10002, connection.RosPort);
            Assert.AreEqual(builtIn, bus.DefaultHost);
            yield return null;
            Assert.IsFalse(connection.HasConnectionThread);
        }

        [UnityTest]
        public IEnumerator AConnectionThatIsStillTrying_IsMoved_AndKeepsWhatWasRegistered()
        {
            connectionGo = IdleRosConnection.Create();
            ROSConnection connection = connectionGo.GetComponent<ROSConnection>();
            connection.ShowHud = false;
            yield return null;   // started, idle

            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int listening = ((IPEndPoint)listener.LocalEndpoint).Port;
            int refusing = RefusingPort();

            var bus = new LiveRosBus();
            string id = Guid.NewGuid().ToString("N");
            string published = "/erupt_endpoint_out_" + id, subscribed = "/erupt_endpoint_in_" + id;
            bus.RegisterPublisher<StringMsg>(published);
            bus.Subscribe<StringMsg>(subscribed, _ => { });
            Assert.IsFalse(bus.HasConnectionThread);

            // The address the app was built with does not answer.
            bus.ConnectTo("127.0.0.1", refusing);
            Assert.IsTrue(bus.HasConnectionThread, "An idle connection connects when it is asked to.");
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.IsTrue(bus.HasConnectionError);

            // The user enters the right one.
            bus.ConnectTo("127.0.0.1", listening);
            Assert.AreEqual(listening, bus.Port);
            Assert.IsTrue(bus.HasConnectionThread);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!listener.Pending() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(listener.Pending(), "The connection reached the new address.");
            accepted = listener.AcceptTcpClient();

            RosTopicState pub = connection.GetTopic(published), sub = connection.GetTopic(subscribed);
            while (!(pub.SentPublisherRegistration && sub.SentSubscriberRegistration) && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(pub.SentPublisherRegistration, "The publisher is announced to the new endpoint.");
            Assert.IsTrue(sub.SentSubscriberRegistration, "And the subscription.");

            // The attempt at the old address was retried once a second. It must be over: what it
            // did when it gave up used to undo the registrations of the connection after it,
            // and every publish was dropped from then on.
            yield return new WaitForSecondsRealtime(2.2f);
            Assert.IsTrue(pub.SentPublisherRegistration);
            Assert.IsTrue(sub.SentSubscriberRegistration);
            Assert.IsFalse(listener.Pending(), "One connection, not two.");

            // The same address again changes nothing.
            bus.ConnectTo("127.0.0.1", listening);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.IsTrue(pub.SentPublisherRegistration);
            Assert.IsFalse(listener.Pending(), "No reconnect for the address already in use.");
        }
    }
}
