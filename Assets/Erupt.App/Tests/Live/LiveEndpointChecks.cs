using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ui;

namespace Erupt.App.Live.Tests
{
    /// <summary>The ROS address, changed while the app runs, against the real stack.</summary>
    public abstract partial class LiveSceneFixture
    {
        /// <summary>
        /// The user sends the connection somewhere else and back: to an address where nothing
        /// answers, to the right one on the Settings tab's keypad, and (when this machine
        /// reaches the ROS side at a second address) to that one and back. Each time the app
        /// ends up connected, every planning scene holds what Unity has, joint states flow, and
        /// what the app publishes afterwards still arrives.
        /// </summary>
        protected static IEnumerator AssertRosAddressCanBeChanged()
        {
            IRosEndpoint endpoint = Context.Ros.Endpoint();
            Assert.IsNotNull(endpoint, "The app's bus says where it connects and can be sent elsewhere.");
            Assert.IsNotNull(Host.RosConnection, "The plugin host put the ROS connection on the Settings tab.");
            Assert.IsInstanceOf<MemoryUserSettingsStore>(UserSettings.Store, "The address entered here must not be kept on this machine.");
            string rosHost = endpoint.Host;
            int rosPort = endpoint.Port;
            Assert.IsTrue(RosAddress.TryParseIPv4(rosHost, out _),
                $"The keypad takes IPv4 addresses; give {LiveRos.EndpointVariable} the address of the ROS side in that form (it is '{rosHost}').");

            RobotInstance first = Robots[0];
            Vector3 up = first.PlanningFrame.up;
            GameObject shape = AddShape(PrimitiveType.Sphere, first.PlanningFrame, new Vector3(0.25f, -0.8f, 0.5f), Quaternion.identity, Vector3.one * 0.1f);
            string shapeId = IdOf(shape);
            foreach (RobotInstance robot in Robots) yield return AssertInPlanningScene(robot, shape, shapeId);

            int resyncs = 0;
            Action onResync = () => resyncs++;
            MoveIt.Reconnected += onResync;
            int exceptionsBefore = Exceptions.Count, errorsBefore = Errors.Count;
            try
            {
                // 1. An address where nothing answers: the connection keeps trying it.
                endpoint.ConnectTo("127.0.0.1", RefusingPort());
                yield return LiveRos.Seconds(2.5f);
                Assert.IsTrue(Connection.HasConnectionThread, "The connection keeps trying the address it was given.");
                Assert.IsTrue(Connection.HasConnectionError, "Nothing answers there.");
                Assert.AreEqual(0, resyncs, "Nothing is sent to an address that does not answer.");
                // What changes meanwhile waits for a ROS side.
                shape.transform.position += up * 0.15f;

                // 2. The right address, entered on the keypad.
                yield return EnterRosAddress(rosHost);
                Assert.AreEqual(rosHost, endpoint.Host);
                Assert.AreEqual(rosPort, endpoint.Port, "The keypad changes the address; the port is the built-in one.");
                yield return AssertConnected();
                yield return LiveRos.Until(() => resyncs == 1, 20f, "the MoveIt plugin to bring the ROS side up to date once the address was corrected");
                foreach (RobotInstance robot in Robots) yield return AssertInPlanningScene(robot, shape, shapeId);
                yield return AssertStatusLine(rosHost, rosPort);

                // The attempts at the wrong address came once a second. Were that thread still
                // alive it would by now have undone the registrations of this connection, and
                // nothing the app publishes would arrive.
                yield return LiveRos.Seconds(2.5f);
                shape.transform.position += up * 0.1f;
                foreach (RobotInstance robot in Robots)
                {
                    yield return AssertInPlanningScene(robot, shape, shapeId);
                    yield return AssertJointStatesMirrored(robot);
                }

                // 3. A ROS side that answers at once at another address: no outage in time, and
                // it still gets everything.
                string other = OtherAddressOfThisMachine(rosHost);
                if (other == null)
                    Debug.Log($"[live] The ROS side at {rosHost} is not on this machine, or the machine has one address; the switch between two addresses was not tried.");
                else
                {
                    yield return EnterRosAddress(other);
                    Assert.AreEqual(other, endpoint.Host);
                    // An address of this machine answers at once or refuses at once.
                    bool nobodyThere = false;
                    yield return LiveRos.Until(() => Connection.HasConnectionThread && !Connection.HasConnectionError, 6f, timedOut => nobodyThere = timedOut);
                    if (nobodyThere)
                        Debug.Log($"[live] The ROS side does not answer at {other}:{rosPort} (its port is not published on that address); the switch between two addresses was not tried.");
                    else
                    {
                        yield return AssertConnected();
                        yield return LiveRos.Until(() => resyncs == 2, 20f, $"the MoveIt plugin to bring the ROS side at {other} up to date");
                        shape.transform.position -= up * 0.1f;
                        foreach (RobotInstance robot in Robots)
                        {
                            yield return AssertInPlanningScene(robot, shape, shapeId);
                            yield return AssertEveryPublisherIsInPlanningScene(robot);
                            yield return AssertJointStatesMirrored(robot);
                        }
                        yield return AssertStatusLine(other, rosPort);
                    }

                    int before = resyncs;
                    yield return EnterRosAddress(rosHost);
                    Assert.AreEqual(rosHost, endpoint.Host);
                    yield return AssertConnected();
                    yield return LiveRos.Until(() => resyncs == before + 1, 20f, "the MoveIt plugin to bring the ROS side up to date after going back");
                    foreach (RobotInstance robot in Robots) yield return AssertInPlanningScene(robot, shape, shapeId);
                }

                // The app's own actions still reach the ROS side.
                DeleteWithVerb(shape);
                Spawned.Remove(shape);
                foreach (RobotInstance robot in Robots)
                {
                    yield return AssertNotInPlanningScene(robot, shapeId);
                    yield return AssertEveryPublisherIsInPlanningScene(robot);
                }
            }
            finally
            {
                MoveIt.Reconnected -= onResync;
                if (endpoint.Host != rosHost || endpoint.Port != rosPort) endpoint.ConnectTo(rosHost, rosPort);
                if (Ui.TierThree.IsOpen) Ui.ToggleTierThree();
            }

            yield return LiveRos.Seconds(1f);
            Assert.AreEqual(exceptionsBefore, Exceptions.Count, "Changing the address throws nothing:\n" + Join(Exceptions.Skip(exceptionsBefore)));
            List<string> errors = Errors.Skip(errorsBefore).Where(e => !IsHeadlessNoise(e)).ToList();
            Assert.IsEmpty(errors, "Changing the address logged errors:\n" + Join(errors));
        }

        /// <summary>Enter an address the way the user does: Change address, the keys, Connect.</summary>
        private static IEnumerator EnterRosAddress(string address)
        {
            RosConnectionSection section = Host.RosConnection;
            Ui.SummonTab(SettingsTab.TabId);
            yield return null;
            SettingsButton("RosAddress").onClick.Invoke();
            Assert.IsTrue(section.IsOpen, "Change address opens the keypad.");
            for (int i = section.Entry.Length; i > 0; i--)
                SettingsButton(RosConnectionSection.KeyName(RosConnectionSection.DeleteLabel)).onClick.Invoke();
            foreach (char key in address) SettingsButton(RosConnectionSection.KeyName(key.ToString())).onClick.Invoke();
            Assert.AreEqual(address, section.Entry);
            yield return null;
            SettingsButton("RosConnect").onClick.Invoke();
            Assert.IsFalse(section.IsOpen, "Connect takes the address and puts the keypad away.");
            yield return null;
            Ui.ToggleTierThree();
            Assert.IsFalse(Ui.TierThree.IsOpen);
        }

        private static Button SettingsButton(string name)
        {
            Assert.IsNotNull(Ui.Settings.Tab, "The panel has a Settings tab.");
            Button button = Ui.Settings.Tab.Content.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == name);
            Assert.IsNotNull(button, $"The Settings tab has no button '{name}'.");
            return button;
        }

        private static IEnumerator AssertStatusLine(string host, int port)
        {
            string expected = RosConnectionSection.StatusText(host, port, true);
            TextMeshProUGUI Line() => Ui.Settings.Tab.Content.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.name == "RosStatus");
            yield return LiveRos.Until(() => Line() != null && Line().text == expected, 5f, $"the Settings tab to say '{expected}'");
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

        // Another IPv4 address of this machine, when the ROS side runs on it: a stack published
        // on all interfaces answers at the machine's address on the network as well as at the
        // loopback address (and the other way round). Null when the ROS side is elsewhere.
        //
        // The address is not tried here. The endpoint keeps one outgoing queue, for the newest
        // connection: a connection opened to see whether it answers, and closed again, takes the
        // queue from the app's own connection and leaves the endpoint sending nothing.
        private static string OtherAddressOfThisMachine(string host)
        {
            var mine = new List<string> { "127.0.0.1" };
            string onTheNetwork = null;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties properties = nic.GetIPProperties();
                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string text = address.Address.ToString();
                    if (text.StartsWith("169.254.") || mine.Contains(text)) continue;
                    mine.Add(text);
                    // The interface with a way out is the one the headset would use.
                    if (onTheNetwork == null && properties.GatewayAddresses.Count > 0) onTheNetwork = text;
                }
            }
            if (!mine.Contains(host)) return null;
            if (host != "127.0.0.1") return "127.0.0.1";
            return onTheNetwork ?? (mine.Count > 1 ? mine[1] : null);
        }
    }
}
