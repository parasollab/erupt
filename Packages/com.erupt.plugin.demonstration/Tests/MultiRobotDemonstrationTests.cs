using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>
    /// One demonstration plugin, three robots (left, right, third): each has its own recorder
    /// and replay player in its own namespace, a demonstration is recorded on the robot it was
    /// started for, and the facade and the mirrors follow the active robot.
    /// </summary>
    public class MultiRobotDemonstrationTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private readonly List<RobotProfile> profiles = new();
        private GameObject go;
        private DemonstrationCollectionPlugin plugin;
        private JointTrajectoryPlayer ownPlayer;
        private HandMirror hand;
        private FakeUiHost ui;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            profiles.Clear();
            // The third robot's ROS side names its joints with another prefix.
            robots = FakeRobots.Three(bus, profile: id =>
            {
                if (id != "third") return null;
                RobotProfile profile = FakeRobots.ProfileWithPrefixes("third_", "ros_");
                profiles.Add(profile);
                return profile;
            });
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];

            go = new GameObject("demonstration plugin");
            ownPlayer = go.AddComponent<JointTrajectoryPlayer>();
            plugin = go.AddComponent<DemonstrationCollectionPlugin>();
            hand = new GameObject("hand mirror").AddComponent<HandMirror>();
            hand.transform.SetParent(go.transform, false);
            ui = new FakeUiHost();
            context = new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(context);
            InteractionSampleBus.Reset();
            Object.DestroyImmediate(go);
            FakeRobots.DestroyAll(robots);
            foreach (RobotProfile profile in profiles) Object.DestroyImmediate(profile);
        }

        [Test]
        public void EveryRobot_HasItsOwnRecorderAndPlayer_InItsOwnNamespace()
        {
            Assert.AreEqual(3, plugin.Sessions.Count);
            Assert.AreEqual(new[] { "demos-demonstration" }, ui.Tabs, "One plugin, one tab.");

            var publishers = new List<string>();
            var subscriptions = new List<string> { "/record_start", "/user_info" };
            foreach (string ns in FakeRobots.ThreeIds)
            {
                publishers.AddRange(new[] { $"/{ns}/joint_trajectory", $"/{ns}/virtual_joint_state", $"/{ns}/interaction" });
                subscriptions.Add($"/{ns}/joint_states");
            }
            CollectionAssert.AreEquivalent(publishers, bus.RegisteredPublishers);
            CollectionAssert.AreEquivalent(subscriptions, bus.Subscriptions, "The record trigger and the info log are global; the rest is per robot.");

            var sessions = plugin.Sessions.ToList();
            Assert.AreEqual(3, sessions.Select(s => s.Recorder).Distinct().Count());
            Assert.AreEqual(3, sessions.Select(s => s.Player).Distinct().Count());
            Assert.AreSame(ownPlayer, sessions[0].Player, "The plugin's own player serves the first robot.");
            Assert.AreEqual("/right/joint_trajectory", plugin.SessionFor(right).Recorder.TrajectoryTopic);
        }

        [Test]
        public void ADemonstration_IsRecordedAndPublished_OnTheRobotItWasStartedFor()
        {
            ui.Bound["correct"](FakeRobots.EndEffectorOf(right));

            Assert.IsTrue(plugin.IsRecording);
            Assert.AreSame(right, plugin.RecordingRobot);
            Assert.IsTrue(plugin.SessionFor(right).Recorder.IsRecording);
            Assert.IsFalse(plugin.SessionFor(left).Recorder.IsRecording);
            Assert.IsFalse(plugin.SessionFor(third).Recorder.IsRecording);
            Assert.AreEqual(1, bus.CountOn("/right/interaction"));
            Assert.AreEqual(0, bus.CountOn("/left/interaction") + bus.CountOn("/third/interaction"));

            // Stopping "another robot's" demonstration does nothing; stopping this one does.
            plugin.StopDemonstration(third);
            Assert.IsTrue(plugin.IsRecording);
            plugin.StopDemonstration(right);
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsNull(plugin.RecordingRobot);
            Assert.IsNotNull(plugin.SessionFor(right).Recorder.LastDemonstration);
            Assert.IsNull(plugin.SessionFor(left).Recorder.LastDemonstration);
            CollectionAssert.AreEqual(
                new[] { "right_joint1", "right_joint2", "right_finger" },
                plugin.SessionFor(right).Recorder.LastDemonstration.joint_names);

            plugin.Publish(right);
            Assert.AreEqual(1, bus.CountOn("/right/joint_trajectory"));
            Assert.AreEqual(0, bus.CountOn("/left/joint_trajectory") + bus.CountOn("/third/joint_trajectory"));
        }

        [Test]
        public void TheFacadeTheRecordTriggerAndTheMirrors_FollowTheActiveRobot()
        {
            Assert.AreSame(plugin.SessionFor(left).Recorder, plugin.Recorder);
            Assert.AreSame(left, hand.Target);
            Assert.AreEqual("Demonstrations · left", ui.TabsById["demos-demonstration"].Label);

            robots.SetActive(third);

            Assert.AreSame(plugin.SessionFor(third).Recorder, plugin.Recorder);
            Assert.AreSame(plugin.SessionFor(third).Player, plugin.Player);
            Assert.AreSame(third, hand.Target, "A mirror without a robot of its own drives the active one.");
            Assert.AreEqual("Demonstrations · third", ui.TabsById["demos-demonstration"].Label);

            // The global record trigger starts on the active robot.
            bus.Inbound("/record_start", new BoolMsg(true));
            Assert.AreSame(third, plugin.RecordingRobot);
            bus.Inbound("/record_start", new BoolMsg(true));
            Assert.IsFalse(plugin.IsRecording);
        }

        [Test]
        public void MirroredJointStates_AreTranslatedThroughTheRobotsNameMap_AndMoveOnlyThatRobot()
        {
            foreach (DemonstrationCollectionPlugin.RobotSession session in plugin.Sessions) session.Recorder.MirrorInput = true;
            var thirdModel = (FakeRobotModel)third.Model;

            // ROS calls the joint ros_joint1; the Unity robot calls it third_joint1.
            bus.Inbound("/third/joint_states", new JointStateMsg { name = new[] { "ros_joint1" }, position = new[] { 0.4 } });

            Assert.AreEqual(1, thirdModel.Applied.Count);
            CollectionAssert.AreEqual(new[] { "third_joint1" }, thirdModel.Applied[0].names);
            Assert.AreEqual(0, ((FakeRobotModel)left.Model).Applied.Count);
            Assert.AreEqual(0, ((FakeRobotModel)right.Model).Applied.Count);

            // A robot without a name map takes the names as they are.
            bus.Inbound("/left/joint_states", new JointStateMsg { name = new[] { "left_joint1" }, position = new[] { 0.2 } });
            CollectionAssert.AreEqual(new[] { "left_joint1" }, ((FakeRobotModel)left.Model).Applied[0].names);
        }

        [Test]
        public void ARobotThatLeaves_TakesItsRecorderWithIt()
        {
            plugin.StartDemonstration(right);
            Object.DestroyImmediate(right.gameObject);

            Assert.AreEqual(2, plugin.Sessions.Count);
            Assert.IsFalse(plugin.IsRecording, "Its demonstration ends with it.");
            plugin.StartDemonstration(third);
            Assert.AreSame(third, plugin.RecordingRobot);
        }
    }
}
