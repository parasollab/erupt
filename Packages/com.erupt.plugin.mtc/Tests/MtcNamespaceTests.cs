using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.EruptInterfaces;
using RosMessageTypes.MoveitTaskConstructorMsgs;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// The MTC plugin's wire names with one robot, in the root namespace and in <c>left</c>:
    /// every action, service and topic of the pick/place server, and the joint states the
    /// solution player follows, are the robot's own.
    /// </summary>
    public class MtcNamespaceTests
    {
        private FakeRosBus bus;
        private GameObject pluginGo;
        private MtcPlugin plugin;
        private MtcSolutionPlayer player;
        private RobotRegistry robots;
        private EruptContext context;

        private IEnumerator Register(string rosNamespace)
        {
            bus = new FakeRosBus();
            pluginGo = new GameObject("mtc");
            pluginGo.AddComponent<PickPlaceClient>();
            pluginGo.AddComponent<PickPlaceTaskRecorder>();
            player = pluginGo.AddComponent<MtcSolutionPlayer>();   // before the plugin, which finds its siblings in Awake
            plugin = pluginGo.AddComponent<MtcPlugin>();
            yield return null;                                   // Start of every component

            robots = FakeRobots.Single(bus, new FakeRobotModel("fr3_joint1"), rosNamespace);
            context = new EruptContext { Ros = bus, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(context);
            Object.DestroyImmediate(pluginGo);
            FakeRobots.DestroyAll(robots);
        }

        [UnityTest]
        public IEnumerator EveryName_IsUnderTheRobotsNamespace([Values("", "left")] string rosNamespace)
        {
            string ns = rosNamespace.Length == 0 ? "" : "/" + rosNamespace;
            yield return Register(rosNamespace);

            CollectionAssert.AreEquivalent(
                new[] { ns + "/pick_place/description", ns + "/pick_place/statistics" }, bus.Subscriptions);
            CollectionAssert.Contains(bus.RegisteredServices, ns + "/get_solution");
            Assert.DoesNotThrow(() => bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>(ns + "/pick_place"));
            Assert.DoesNotThrow(() => bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>(ns + "/execute_solution"));
            Assert.IsTrue(plugin.PickPlace.Available);

            // The live mirror follows the robot's own joint states.
            player.StartLiveMirror();
            Assert.IsTrue(player.IsMirroring);
            CollectionAssert.Contains(bus.Subscriptions, ns + "/joint_states");
            player.StopLiveMirror();
        }

        [UnityTest]
        public IEnumerator WithoutARobot_ThePluginStaysIdle()
        {
            bus = new FakeRosBus();
            pluginGo = new GameObject("mtc");
            pluginGo.AddComponent<PickPlaceClient>();
            player = pluginGo.AddComponent<MtcSolutionPlayer>();
            plugin = pluginGo.AddComponent<MtcPlugin>();
            yield return null;

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No robot is registered"));
            context = new EruptContext { Ros = bus, Ui = new FakeUiHost(), Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);

            CollectionAssert.IsEmpty(bus.Subscriptions);
            Assert.IsFalse(plugin.PickPlace.Available);
        }
    }
}
