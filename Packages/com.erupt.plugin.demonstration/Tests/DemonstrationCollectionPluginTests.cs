using System.Linq;
using NUnit.Framework;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Robot;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>
    /// The plugin against a FakeRosBus / FakeRobotModel / FakeUiHost context: registration
    /// wiring, the record → stop → publish flow with no mode in front of it, the ROS-side record toggle,
    /// and the info log.
    /// </summary>
    public class DemonstrationCollectionPluginTests
    {
        private GameObject go;
        private DemonstrationCollectionPlugin plugin;
        private FakeRosBus ros;
        private FakeRobotModel robot;
        private FakeUiHost ui;
        private RobotRegistry robots;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("DemonstrationCollectionPlugin");
            go.AddComponent<JointTrajectoryPlayer>();
            plugin = go.AddComponent<DemonstrationCollectionPlugin>();
            ros = new FakeRosBus();
            robot = new FakeRobotModel("j1", "j2");
            ui = new FakeUiHost();
            robots = FakeRobots.Single(ros, robot);
            context = new EruptContext { Ros = ros, Robots = robots, Ui = ui, Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(context);
            InteractionSampleBus.Reset();
            Object.DestroyImmediate(go);
            FakeRobots.DestroyAll(robots);
        }

        [Test]
        public void Register_WiresTopics_AndContributesTheDemosTab()
        {
            Assert.IsTrue(plugin.IsRegistered);
            Assert.AreEqual("demonstration", plugin.Id);
            CollectionAssert.Contains(ui.Tabs, "demos-demonstration");
            Assert.IsEmpty(ui.Bound, "No verb on any menu: a demonstration is started from the tab or the topic.");

            CollectionAssert.IsSubsetOf(
                new[] { "/joint_trajectory", "/virtual_joint_state", "/interaction" },
                ros.RegisteredPublishers);
            CollectionAssert.IsSubsetOf(
                new[] { "/joint_states", "/record_start", "/user_info" },
                ros.Subscriptions);
            Assert.IsNotNull(plugin.Recorder);
            Assert.IsNotNull(plugin.Log);
            Assert.IsFalse(plugin.IsRecording);
        }

        // There is no mode in front of recording: a demonstration starts when it is asked for.
        [Test]
        public void StartDemonstration_StartsRecording_WithNothingToSwitchFirst()
        {
            plugin.StartDemonstration();
            Assert.IsTrue(plugin.IsRecording);
            Assert.IsTrue(plugin.Recorder.IsRecording);
            Assert.AreSame(robots.Active, plugin.RecordingRobot);
            Assert.AreEqual(false, ros.PublishedOn("/interaction").Cast<BoolMsg>().Last().data);
        }

        [Test]
        public void RecordStopPublish_ProducesAJointTrajectory_FromTheContextRobot()
        {
            plugin.StartDemonstration();
            robot.Positions[0] = 0.25f;
            plugin.Recorder.Tick(Time.timeAsDouble + 1.0);   // one more sample, deterministic
            InteractionSampleBus.Publish(new InteractionSample(Modality.Hand, 0.9f, 0, Pose.identity, "right"));
            plugin.StopDemonstration();

            Assert.IsFalse(plugin.IsRecording);
            Assert.AreEqual(1, plugin.InteractionSampleCount);
            var demo = plugin.Recorder.LastDemonstration;
            Assert.IsNotNull(demo);
            CollectionAssert.AreEqual(new[] { "j1", "j2" }, demo.joint_names);
            Assert.GreaterOrEqual(demo.points.Length, 2);
            Assert.AreEqual(0.25, demo.points.Last().positions[0], 1e-6);
            Assert.AreEqual(true, ros.PublishedOn("/interaction").Cast<BoolMsg>().Last().data);

            plugin.Publish();
            Assert.AreSame(demo.points, ros.PublishedOn("/joint_trajectory").Cast<JointTrajectoryMsg>().Single().points);
        }

        [Test]
        public void Publish_WithNothingRecorded_Warns()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Nothing to publish"));
            plugin.Publish();
            Assert.AreEqual(0, ros.CountOn("/joint_trajectory"));
        }

        // What used to be a line in the log, or nothing, is a refusal the user sees.
        [Test]
        public void WhatCannotBeDone_IsARefusal_WithTheReason()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            try
            {
                var router = routerGo.AddComponent<InteractionRouter>();
                router.RefusalCoalesceSeconds = 0f;
                var refusals = new System.Collections.Generic.List<InteractionRefusal>();
                router.Refused += refusals.Add;
                context.Router = router;

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Nothing to publish"));
                plugin.Publish();
                Assert.AreEqual("Nothing to publish; record a demonstration first.", refusals.Last().Reason);

                plugin.Replay();
                Assert.AreEqual("There is no demonstration to replay; record one first.", refusals.Last().Reason);
                Assert.AreEqual(2, refusals.Count);

                plugin.StartDemonstration();
                Assert.AreEqual(2, refusals.Count, "Starting a demonstration is no refusal.");
                // Asked for again while the demonstration is being recorded.
                plugin.StartDemonstration();
                Assert.AreEqual("A demonstration is already being recorded on this robot.", refusals.Last().Reason);
                Assert.IsTrue(plugin.IsRecording);

                // A robot the plugin has no session for.
                plugin.StopDemonstration();
                plugin.StartDemonstration(null);
                Assert.AreEqual("There is no robot to record a demonstration on.", refusals.Last().Reason);
            }
            finally
            {
                context.Router = null;
                Object.DestroyImmediate(routerGo);
            }
        }

        [Test]
        public void RecordStartTopic_TogglesRecording_AtAnyTime()
        {
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.IsTrue(plugin.IsRecording, "The first message starts a recording, with nothing to switch first.");
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.IsFalse(plugin.IsRecording, "any message toggles whatever its value");
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
        }

        [Test]
        public void Stop_FinalisesTheDemonstration()
        {
            plugin.StartDemonstration();
            Assert.IsTrue(plugin.IsRecording);
            plugin.StopDemonstration();
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsFalse(plugin.Recorder.IsRecording);
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
            Assert.AreEqual(true, ros.PublishedOn("/interaction").Cast<BoolMsg>().Last().data);
        }

        [Test]
        public void ReplayingADemonstrationOutsideTheLimits_TellsTheUserWhichJoint()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            try
            {
                var router = routerGo.AddComponent<InteractionRouter>();
                var refusals = new System.Collections.Generic.List<InteractionRefusal>();
                router.Refused += refusals.Add;
                context.Router = router;

                // Recorded at j2 = 0.9, then the limits are tightened to +-0.5.
                robot.Positions[1] = 0.9f;
                plugin.StartDemonstration();
                plugin.StopDemonstration();
                robot.WithLimits(new JointLimit("j2", JointKind.Revolute, -0.5f, 0.5f));
                Assert.IsEmpty(plugin.Recorder.LastDemonstrationViolations, "Checked when it was finished, against the limits of that moment.");
                plugin.StartDemonstration();
                plugin.StopDemonstration();
                Assert.AreEqual(1, plugin.Recorder.LastDemonstrationViolations.Count);

                plugin.Replay();

                Assert.IsTrue(plugin.IsReplaying, "The replay still plays; the robot model clamps it.");
                Assert.AreEqual(1, refusals.Count);
                Assert.AreEqual(RefusalCode.PlanOutsideLimits, refusals[0].Code);
                StringAssert.Contains("j2", refusals[0].Reason);
                Assert.LessOrEqual(robot.Positions[1], 0.5f + 1e-6f, "The replay's first pose is already clamped.");
                plugin.StopReplay();
            }
            finally
            {
                Object.DestroyImmediate(routerGo);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator StopReplay_GivesTheRobotItsPoseBack_AndAReplayCanStartRightAfter()
        {
            robot.Positions[0] = 0.1f;
            plugin.StartDemonstration();
            robot.Positions[0] = 0.6f;
            plugin.Recorder.Tick(Time.timeAsDouble + 1.0);   // two points, one second apart
            plugin.StopDemonstration();

            // The user poses the robot somewhere else, then replays.
            robot.Positions[0] = -0.3f;
            robot.Positions[1] = 0.25f;
            plugin.Replay();
            Assert.IsTrue(plugin.IsReplaying);
            yield return null;
            yield return null;
            Assert.Greater(robot.Positions[0], 0f, "The replay plays on the robot.");

            plugin.StopReplay();
            Assert.IsFalse(plugin.IsReplaying, "Stop replay stops it.");
            yield return null;
            yield return null;
            Assert.AreEqual(-0.3f, robot.Positions[0], 1e-6f, "The robot gets back the pose it had before the replay.");
            Assert.AreEqual(0.25f, robot.Positions[1], 1e-6f);

            // Replay, stop and replay again without waiting in between: the second one plays.
            plugin.Replay();
            plugin.StopReplay();
            plugin.Replay();
            Assert.IsTrue(plugin.IsReplaying);
            yield return null;
            yield return null;
            yield return null;
            Assert.Greater(robot.Positions[0], 0f, "A replay asked for right after a stop plays.");
            plugin.StopReplay();
            yield return null;
            yield return null;
            Assert.AreEqual(-0.3f, robot.Positions[0], 1e-6f);
            Assert.AreEqual(0.25f, robot.Positions[1], 1e-6f);
        }

        [Test]
        public void Replay_PlaysTheLastDemonstration_OnTheRobot()
        {
            plugin.StartDemonstration();
            plugin.Recorder.Tick(Time.timeAsDouble + 1.0);   // two points, one second apart
            plugin.StopDemonstration();
            Assert.IsFalse(plugin.IsReplaying);
            plugin.Replay();
            Assert.IsTrue(plugin.IsReplaying);
            plugin.StopReplay();
            Assert.IsFalse(plugin.IsReplaying);
        }

        [Test]
        public void UserInfo_IsLogged_AndKept()
        {
            LogAssert.Expect(LogType.Log, "[demonstration] hello");
            ros.Inbound("/user_info", new StringMsg("hello"));
            Assert.AreEqual("hello", plugin.Log.Last);
            Assert.AreEqual(1, plugin.Log.Count);
        }

        [Test]
        public void Unregister_StopsRecording_AndUnsubscribes()
        {
            plugin.StartDemonstration();
            ((IEruptPlugin)plugin).OnUnregister(context);

            Assert.IsFalse(plugin.IsRegistered);
            Assert.IsFalse(plugin.IsRecording);
            int before = ros.Publishes.Count;
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.AreEqual(before, ros.Publishes.Count);
        }

        [Test]
        public void Register_HandsTheContextBusToASiblingPointCloudPublisher()
        {
            var cloudGo = new GameObject("with point cloud");
            var cloud = cloudGo.AddComponent<PointCloudPublisher>();   // before the plugin, which finds its siblings in Awake
            var withCloud = cloudGo.AddComponent<DemonstrationCollectionPlugin>();
            var bus = new FakeRosBus();
            var cloudRobots = FakeRobots.Single(bus, robot, "left");
            var ctx = new EruptContext { Ros = bus, Robots = cloudRobots, Ui = new FakeUiHost(), Undo = new UndoStack() };

            ((IEruptPlugin)withCloud).OnRegister(ctx);

            CollectionAssert.Contains(bus.RegisteredPublishers, cloud.Topic, "The point cloud is global: it is not namespaced with the robot.");
            ((IEruptPlugin)withCloud).OnUnregister(ctx);
            Object.DestroyImmediate(cloudGo);
            FakeRobots.DestroyAll(cloudRobots);
        }

        // One robot, in the root namespace and in "left": the robot's own topics follow its
        // namespace, the plugin's global ones (record trigger, info log) do not.
        [Test]
        public void Register_PutsTheRobotsTopicsInItsNamespace([Values("", "left")] string rosNamespace)
        {
            string ns = rosNamespace.Length == 0 ? "" : "/" + rosNamespace;
            var otherGo = new GameObject("namespaced demonstration plugin");
            var other = otherGo.AddComponent<DemonstrationCollectionPlugin>();
            var bus = new FakeRosBus();
            var otherRobots = FakeRobots.Single(bus, robot, rosNamespace);
            var ctx = new EruptContext { Ros = bus, Robots = otherRobots, Ui = new FakeUiHost(), Undo = new UndoStack() };
            try
            {
                ((IEruptPlugin)other).OnRegister(ctx);

                CollectionAssert.AreEquivalent(
                    new[] { ns + "/joint_trajectory", ns + "/virtual_joint_state", ns + "/interaction" },
                    bus.RegisteredPublishers);
                CollectionAssert.AreEquivalent(
                    new[] { ns + "/joint_states", "/record_start", "/user_info" },
                    bus.Subscriptions);
                Assert.AreEqual(ns + "/joint_trajectory", other.Recorder.TrajectoryTopic);

                other.StartDemonstration();
                Assert.AreEqual(1, bus.CountOn(ns + "/interaction"));
                other.StopDemonstration();
                other.Publish();
                Assert.AreEqual(1, bus.CountOn(ns + "/joint_trajectory"));
            }
            finally
            {
                ((IEruptPlugin)other).OnUnregister(ctx);
                InteractionSampleBus.Reset();
                Object.DestroyImmediate(otherGo);
                FakeRobots.DestroyAll(otherRobots);
            }
        }

        // A mirror with a robot assigned in the inspector keeps it; the plugin's robot is only the default.
        [Test]
        public void Register_LeavesAnExplicitlyAssignedMirrorRobotAlone()
        {
            var otherGo = new GameObject("demonstration plugin with mirrors");
            var other = otherGo.AddComponent<DemonstrationCollectionPlugin>();
            var hand = new GameObject("hand mirror").AddComponent<HandMirror>();
            hand.transform.SetParent(otherGo.transform, false);
            var explicitHand = new GameObject("hand mirror with a robot").AddComponent<HandMirror>();
            explicitHand.transform.SetParent(otherGo.transform, false);
            var gripper = new GameObject("gripper mirror with a robot").AddComponent<Robotiq2fGripperMirror>();
            gripper.transform.SetParent(otherGo.transform, false);

            var bus = new FakeRosBus();
            var otherRobots = FakeRobots.Registry(bus);
            RobotInstance active = FakeRobots.Add(otherRobots, "left", "left", robot);
            RobotInstance assigned = FakeRobots.Add(otherRobots, "right", "right", new FakeRobotModel("k1"));
            explicitHand.SetRobotOverride(assigned);
            gripper.SetRobotOverride(assigned);
            var ctx = new EruptContext { Ros = bus, Robots = otherRobots, Ui = new FakeUiHost(), Undo = new UndoStack() };
            try
            {
                ((IEruptPlugin)other).OnRegister(ctx);

                Assert.AreSame(active, hand.Target, "A mirror without a robot gets the active one.");
                Assert.AreSame(assigned, explicitHand.Target, "An assigned robot wins over the plugin's.");
                Assert.AreSame(assigned, gripper.Target);
                Assert.AreSame(assigned.Model, gripper.Robot);
            }
            finally
            {
                ((IEruptPlugin)other).OnUnregister(ctx);
                InteractionSampleBus.Reset();
                Object.DestroyImmediate(otherGo);
                FakeRobots.DestroyAll(otherRobots);
            }
        }

        [Test]
        public void Register_WithoutRobot_StillLogsInfo_ButCannotRecord()
        {
            var bare = new GameObject("bare").AddComponent<DemonstrationCollectionPlugin>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No robot"));
            var ctx = new EruptContext { Ros = new FakeRosBus(), Ui = new FakeUiHost(), Undo = new UndoStack() };
            ((IEruptPlugin)bare).OnRegister(ctx);
            Assert.IsNull(bare.Recorder);
            Assert.IsNotNull(bare.Log);
            bare.StartDemonstration();
            Assert.IsFalse(bare.IsRecording);
            ((IEruptPlugin)bare).OnUnregister(ctx);
            Object.DestroyImmediate(bare.gameObject);
        }
    }
}
