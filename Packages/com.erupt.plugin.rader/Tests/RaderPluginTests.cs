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

namespace Erupt.Plugins.Rader.Tests
{
    /// <summary>
    /// The plugin against a FakeRosBus / FakeRobotModel / FakeUiHost context: registration
    /// wiring, the Teach gate, the record → stop → publish flow, the ROS-side record toggle,
    /// and the FERL handshake.
    /// </summary>
    public class RaderPluginTests
    {
        private GameObject go;
        private RaderPlugin plugin;
        private FakeRosBus ros;
        private FakeRobotModel robot;
        private FakeUiHost ui;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("RaderPlugin");
            go.AddComponent<JointTrajectoryPlayer>();
            plugin = go.AddComponent<RaderPlugin>();
            ros = new FakeRosBus();
            robot = new FakeRobotModel("j1", "j2");
            ui = new FakeUiHost();
            context = new EruptContext { Ros = ros, Robot = robot, Ui = ui, Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        [TearDown]
        public void TearDown()
        {
            if (plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(context);
            InteractionSampleBus.Reset();
            Object.DestroyImmediate(go);
        }

        private void EnterTeach() => ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);

        [Test]
        public void Register_WiresTopics_AndContributesTheDemosTab()
        {
            Assert.IsTrue(plugin.IsRegistered);
            Assert.AreEqual("rader", plugin.Id);
            CollectionAssert.Contains(ui.Tabs, "demos-rader");
            Assert.Contains("correct", ui.Bound.Keys.ToList());

            CollectionAssert.IsSubsetOf(
                new[] { "/joint_trajectory", "/virtual_joint_state", "/interaction", "/feedback_response" },
                ros.RegisteredPublishers);
            CollectionAssert.IsSubsetOf(
                new[] { "/joint_states", "/record_start", "/feedback_request", "/req_satisfied", "/user_info" },
                ros.Subscriptions);
            Assert.IsNotNull(plugin.Recorder);
            Assert.IsNotNull(plugin.Feedback);
            Assert.IsNotNull(plugin.Log);
            Assert.IsFalse(plugin.IsRecording);
        }

        [Test]
        public void StartDemonstration_OutsideTeach_IsRefused()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Teach mode only"));
            plugin.StartDemonstration();
            Assert.IsFalse(plugin.IsRecording);
            Assert.AreEqual(0, ros.CountOn("/interaction"));
        }

        [Test]
        public void CorrectVerb_InTeach_StartsRecording()
        {
            EnterTeach();
            ui.Bound["correct"](null);
            Assert.IsTrue(plugin.IsRecording);
            Assert.IsTrue(plugin.Recorder.IsRecording);
            Assert.AreEqual(false, ros.PublishedOn("/interaction").Cast<BoolMsg>().Last().data);
        }

        [Test]
        public void RecordStopPublish_ProducesAJointTrajectory_FromTheContextRobot()
        {
            EnterTeach();
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
            Assert.AreSame(demo, ros.PublishedOn("/joint_trajectory").Cast<JointTrajectoryMsg>().Single());
        }

        [Test]
        public void Publish_WithNothingRecorded_Warns()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Nothing to publish"));
            plugin.Publish();
            Assert.AreEqual(0, ros.CountOn("/joint_trajectory"));
        }

        [Test]
        public void RecordStartTopic_TogglesRecording_InTeachOnly()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Teach mode only"));
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.IsFalse(plugin.IsRecording, "outside Teach");

            EnterTeach();
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.IsTrue(plugin.IsRecording);
            ros.Inbound("/record_start", new BoolMsg(true));
            Assert.IsFalse(plugin.IsRecording, "any message toggles (RADER convention)");
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
        }

        [Test]
        public void LeavingTeach_FinalisesTheDemonstration()
        {
            EnterTeach();
            plugin.StartDemonstration();
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Plan);
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsFalse(plugin.Recorder.IsRecording);
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
            Assert.AreEqual(true, ros.PublishedOn("/interaction").Cast<BoolMsg>().Last().data);
        }

        [Test]
        public void Replay_PlaysTheLastDemonstration_OnTheRobot()
        {
            EnterTeach();
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
        public void Ferl_RequestSummonsTheTab_SatisfiedEnablesResume_RespondPublishes()
        {
            ros.Inbound("/feedback_request", new BoolMsg(true));
            Assert.IsTrue(plugin.Feedback.RequestPending);
            Assert.IsFalse(plugin.Feedback.CanRespond);
            Assert.AreEqual("demos-rader", ui.Summoned);
            Assert.IsFalse(plugin.Feedback.Respond(), "not satisfied yet");

            ros.Inbound("/req_satisfied", new BoolMsg(true));
            Assert.IsTrue(plugin.Feedback.CanRespond);
            Assert.IsTrue(plugin.Feedback.Respond());
            Assert.AreEqual(true, ros.PublishedOn("/feedback_response").Cast<BoolMsg>().Single().data);
            Assert.IsFalse(plugin.Feedback.RequestPending);
        }

        [Test]
        public void UserInfo_IsLogged_AndKept()
        {
            LogAssert.Expect(LogType.Log, "[rader] hello");
            ros.Inbound("/user_info", new StringMsg("hello"));
            Assert.AreEqual("hello", plugin.Log.Last);
            Assert.AreEqual(1, plugin.Log.Count);
        }

        [Test]
        public void Unregister_StopsRecording_AndUnsubscribes()
        {
            EnterTeach();
            plugin.StartDemonstration();
            ((IEruptPlugin)plugin).OnUnregister(context);

            Assert.IsFalse(plugin.IsRegistered);
            Assert.IsFalse(plugin.IsRecording);
            int before = ros.Publishes.Count;
            ros.Inbound("/record_start", new BoolMsg(true));
            ros.Inbound("/feedback_request", new BoolMsg(true));
            Assert.AreEqual(before, ros.Publishes.Count);
            Assert.IsFalse(plugin.Feedback.RequestPending);
        }

        [Test]
        public void Register_WithoutRobot_StillHandlesFeedback_ButCannotRecord()
        {
            var bare = new GameObject("bare").AddComponent<RaderPlugin>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No robot"));
            var ctx = new EruptContext { Ros = new FakeRosBus(), Ui = new FakeUiHost(), Undo = new UndoStack() };
            ((IEruptPlugin)bare).OnRegister(ctx);
            Assert.IsNull(bare.Recorder);
            Assert.IsNotNull(bare.Feedback);
            ((IEruptPlugin)bare).OnModeChanged(AppMode.Teach);
            bare.StartDemonstration();
            Assert.IsFalse(bare.IsRecording);
            ((IEruptPlugin)bare).OnUnregister(ctx);
            Object.DestroyImmediate(bare.gameObject);
        }
    }
}
