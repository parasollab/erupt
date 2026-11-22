using System.Linq;
using NUnit.Framework;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>The recorder against a fake bus and robot: topics, sampling cadence, wire conventions.</summary>
    public class DemonstrationRecorderTests
    {
        private FakeRosBus ros;
        private FakeRobotModel robot;
        private DemonstrationRecorder recorder;

        [SetUp]
        public void SetUp()
        {
            ros = new FakeRosBus();
            robot = new FakeRobotModel("shoulder", "elbow", "wrist");
            recorder = new DemonstrationRecorder(ros.Scoped("ur5e"), robot, "base_link") { RecordInterval = 0.1f, PublishStateInterval = 0.2f };
            recorder.Start();
            ros.Clear();   // drop registrations so counts below are about traffic
        }

        [TearDown]
        public void TearDown() => recorder.Dispose();

        [Test]
        public void Topics_AreNamespaced()
        {
            Assert.AreEqual("/ur5e/joint_trajectory", recorder.TrajectoryTopic);
            Assert.AreEqual("/ur5e/joint_states", recorder.InputStateTopic);
            Assert.AreEqual("/ur5e/virtual_joint_state", recorder.OutputStateTopic);
            Assert.AreEqual("/ur5e/interaction", recorder.InteractionTopic);
        }

        [Test]
        public void Namespace_ToleratesLeadingAndTrailingSlashes_AndMayBeEmpty()
        {
            var slashed = new DemonstrationRecorder(ros.Scoped("/fr3/"), robot, "b");
            Assert.AreEqual("/fr3/joint_trajectory", slashed.TrajectoryTopic);
            Assert.AreEqual("/fr3/joint_states", slashed.InputStateTopic);

            var bare = new DemonstrationRecorder(ros.Root, robot, "b");
            Assert.AreEqual("/joint_trajectory", bare.TrajectoryTopic);
            Assert.AreEqual("/interaction", bare.InteractionTopic);
        }

        [Test]
        public void Start_RegistersPublishers_AndSubscribesToJointStates()
        {
            var fresh = new FakeRosBus();
            var r = new DemonstrationRecorder(fresh.Root, robot, "b");
            r.Start();
            CollectionAssert.AreEquivalent(new[] { "/joint_trajectory", "/virtual_joint_state", "/interaction" }, fresh.RegisteredPublishers);
            CollectionAssert.AreEquivalent(new[] { "/joint_states" }, fresh.Subscriptions);
            r.Dispose();
        }

        [Test]
        public void TheJointStateTopic_IsTheRobotsOwn()
        {
            var fresh = new FakeRosBus();
            var r = new DemonstrationRecorder(fresh.Scoped("left"), robot, "b", "state/joints");
            r.Start();
            CollectionAssert.AreEquivalent(new[] { "/left/state/joints" }, fresh.Subscriptions);
            Assert.AreEqual("/left/state/joints", r.InputStateTopic);
            r.Dispose();
        }

        [Test]
        public void Record_SamplesAtTheInterval_AndBuildsATrajectory()
        {
            recorder.PublishState = false;
            recorder.StartRecording(10.0);
            Assert.IsTrue(recorder.IsRecording);
            Assert.AreEqual(1, recorder.PointCount, "first point at start");

            robot.Positions[0] = 0.5f;
            recorder.Tick(10.05);
            Assert.AreEqual(1, recorder.PointCount, "not due yet");
            recorder.Tick(10.1);
            Assert.AreEqual(2, recorder.PointCount);
            recorder.Tick(10.35);
            Assert.AreEqual(3, recorder.PointCount, "a late tick yields one point at its real time");

            JointTrajectoryMsg demo = recorder.StopRecording(10.4);
            Assert.IsFalse(recorder.IsRecording);
            Assert.AreSame(demo, recorder.LastDemonstration);
            CollectionAssert.AreEqual(new[] { "shoulder", "elbow", "wrist" }, demo.joint_names);
            Assert.AreEqual("base_link", demo.header.frame_id);
            Assert.AreEqual(3, demo.points.Length);
            Assert.AreEqual(0.5, demo.points[1].positions[0], 1e-6);
            Assert.AreEqual(0, demo.points[0].time_from_start.sec);
            Assert.AreEqual(0u, demo.points[0].time_from_start.nanosec);
            Assert.AreEqual(100_000_000u, demo.points[1].time_from_start.nanosec, 2_000u);
            Assert.AreEqual(0, demo.points[2].time_from_start.sec);
            Assert.AreEqual(350_000_000u, demo.points[2].time_from_start.nanosec, 2_000u);
        }

        [Test]
        public void Interaction_IsFalseOnStart_TrueOnStop()
        {
            recorder.PublishState = false;
            recorder.StartRecording(0);
            recorder.StopRecording(1);
            var flags = ros.PublishedOn(recorder.InteractionTopic).Cast<BoolMsg>().Select(m => m.data).ToArray();
            CollectionAssert.AreEqual(new[] { false, true }, flags);
        }

        [Test]
        public void Publish_SendsTheLastDemonstration_OnTheTrajectoryTopic()
        {
            recorder.PublishState = false;
            Assert.IsFalse(recorder.Publish(), "nothing recorded yet");
            Assert.AreEqual(0, ros.CountOn(recorder.TrajectoryTopic));

            recorder.StartRecording(0);
            recorder.Tick(0.1);
            recorder.StopRecording(0.2);
            Assert.IsTrue(recorder.Publish());

            var sent = ros.PublishedOn(recorder.TrajectoryTopic).Cast<JointTrajectoryMsg>().Single();
            Assert.AreSame(recorder.LastDemonstration.points, sent.points, "The demonstration's own points go out.");
            CollectionAssert.AreEqual(recorder.LastDemonstration.joint_names, sent.joint_names, "Without a name rule the names are the robot's.");
            Assert.AreEqual(2, sent.points.Length);
        }

        [Test]
        public void WhatGoesOnTheWire_NamesTheJointsAsRosDoes()
        {
            // Unity knows the joints as shoulder / elbow / wrist; the ROS side prefixes them.
            var names = new Erupt.Robot.RobotNameMap { localPrefix = "", rosPrefix = "left_" };
            var prefixed = new DemonstrationRecorder(ros.Scoped("left"), robot, "base_link", names: names) { RecordInterval = 0.1f };
            prefixed.Start();
            ros.Clear();

            prefixed.StartRecording(0);
            prefixed.Tick(0.1);
            prefixed.StopRecording(0.2);
            CollectionAssert.AreEqual(new[] { "shoulder", "elbow", "wrist" }, prefixed.LastDemonstration.joint_names,
                "The demonstration is kept under the Unity robot's names: it is replayed on that robot.");

            Assert.IsTrue(prefixed.Publish());
            var sent = ros.PublishedOn(prefixed.TrajectoryTopic).Cast<JointTrajectoryMsg>().Single();
            CollectionAssert.AreEqual(new[] { "left_shoulder", "left_elbow", "left_wrist" }, sent.joint_names);

            prefixed.PublishVirtualState(0.3);
            var state = ros.PublishedOn(prefixed.OutputStateTopic).Cast<JointStateMsg>().Last();
            CollectionAssert.AreEqual(new[] { "left_shoulder", "left_elbow", "left_wrist" }, state.name);
            prefixed.Dispose();
        }

        [Test]
        public void Effort_IsInertiaTimesAngularVelocity()
        {
            recorder.PublishState = false;
            recorder.MomentOfInertia = 2f;
            recorder.StartRecording(0);
            robot.Positions[1] = 1f;            // 1 rad in 0.1 s → 10 rad/s → effort 20
            recorder.Tick(0.1);
            var demo = recorder.StopRecording(0.2);
            Assert.AreEqual(0.0, demo.points[0].effort[1], 1e-6, "no velocity at the first point");
            Assert.AreEqual(20.0, demo.points[1].effort[1], 1e-3);
        }

        [Test]
        public void VirtualState_IsPublishedAtItsInterval_WhenEnabled()
        {
            recorder.PublishState = true;
            recorder.Tick(0);
            recorder.Tick(0.1);
            recorder.Tick(0.2);
            recorder.Tick(0.45);
            Assert.AreEqual(3, ros.CountOn(recorder.OutputStateTopic));
            var state = ros.PublishedOn(recorder.OutputStateTopic).Cast<JointStateMsg>().First();
            CollectionAssert.AreEqual(new[] { "shoulder", "elbow", "wrist" }, state.name);
            Assert.AreEqual(3, state.position.Length);

            recorder.PublishState = false;
            recorder.Tick(5);
            Assert.AreEqual(3, ros.CountOn(recorder.OutputStateTopic), "off");
        }

        [Test]
        public void JointStates_MirrorOntoTheRobot_OnlyWhenEnabled()
        {
            var msg = new JointStateMsg { name = new[] { "elbow" }, position = new[] { 0.7 } };
            ros.Inbound(recorder.InputStateTopic, msg);
            Assert.IsEmpty(robot.Applied, "mirror off by default");

            recorder.MirrorInput = true;
            ros.Inbound(recorder.InputStateTopic, msg);
            Assert.AreEqual(1, robot.Applied.Count);
            Assert.AreEqual(0.7f, robot.Positions[1], 1e-6f);
            Assert.AreEqual(1, recorder.MirroredStates);
        }

        [Test]
        public void MirroredJointStates_OutsideTheModel_AreClamped_AndRaiseOneMismatch()
        {
            robot.WithLimits(new Erupt.Robot.JointLimit("elbow", Erupt.Robot.JointKind.Revolute, -1f, 1f));
            var refusals = new System.Collections.Generic.List<Erupt.Interaction.InteractionRefusal>();
            recorder.Refused += refusals.Add;
            recorder.MirrorInput = true;

            for (int message = 0; message < 100; message++)
                ros.Inbound(recorder.InputStateTopic, new JointStateMsg { name = new[] { "elbow" }, position = new[] { 1.6 } });

            Assert.AreEqual(1f, robot.Positions[1], 1e-6f, "The mirrored robot stays inside its limits.");
            Assert.AreEqual(100, recorder.MirroredStates);
            Assert.AreEqual(1, refusals.Count, "A hundred joint states, one refusal.");
            Assert.AreEqual(Erupt.Interaction.RefusalCode.LimitMismatch, refusals[0].Code);
            StringAssert.Contains("elbow", refusals[0].Reason);
        }

        [Test]
        public void AFinishedDemonstration_IsCheckedAgainstTheLimitsOnce()
        {
            recorder.StartRecording(0);
            recorder.Tick(0.1);
            recorder.StopRecording(0.2);
            CollectionAssert.IsEmpty(recorder.LastDemonstrationViolations, "Recorded from the robot, a demonstration is inside its limits.");

            // The limits are tightened after the fact (a profile edit): the next demonstration
            // recorded at the same pose is outside them.
            robot.Positions[1] = 0.9f;
            robot.WithLimits(new Erupt.Robot.JointLimit("elbow", Erupt.Robot.JointKind.Revolute, -0.5f, 0.5f));
            recorder.StartRecording(1);
            recorder.Tick(1.1);
            recorder.StopRecording(1.2);

            Assert.AreEqual(1, recorder.LastDemonstrationViolations.Count);
            Assert.AreEqual("elbow", recorder.LastDemonstrationViolations[0].Joint);
            Assert.AreEqual(0.4f, recorder.LastDemonstrationViolations[0].Excess, 1e-5f);
        }

        [Test]
        public void Dispose_Unsubscribes_AndDropsAnOpenRecording()
        {
            recorder.MirrorInput = true;
            recorder.StartRecording(0);
            recorder.Dispose();
            Assert.IsFalse(recorder.IsRecording);
            ros.Inbound(recorder.InputStateTopic, new JointStateMsg { name = new[] { "elbow" }, position = new[] { 0.7 } });
            Assert.IsEmpty(robot.Applied);
        }
    }
}
