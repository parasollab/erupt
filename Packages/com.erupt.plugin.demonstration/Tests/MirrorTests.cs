using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>
    /// The hand and gripper mirrors: which robot they drive (an assigned one wins over the
    /// plugin's), and that the gripper mirror takes its joint and angles from the robot profile.
    /// </summary>
    public class MirrorTests
    {
        private RobotRegistry robots;
        private RobotProfile profile;
        private FakeRobotModel leftModel, rightModel;
        private RobotInstance left, right;
        private GameObject go;

        [SetUp]
        public void SetUp()
        {
            // A Robotiq-style gripper: one revolute driver joint, closed at 0.8 rad.
            profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.gripper = new GripperSpec
            {
                kind = GripperKind.Revolute, driverJoint = "finger_joint", open = 0f, closed = 0.8f, openWidthMetres = 0.085f
            };

            robots = FakeRobots.Registry(new FakeRosBus());
            leftModel = new FakeRobotModel("j1").WithGripper("finger_joint");
            rightModel = new FakeRobotModel("j1").WithGripper("finger_joint");
            left = FakeRobots.Add(robots, "left", "left", leftModel, profile);
            right = FakeRobots.Add(robots, "right", "right", rightModel, profile);
            go = new GameObject("mirrors");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(go);
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void TheHandMirror_DrivesTheRobotItWasHanded()
        {
            var mirror = go.AddComponent<HandMirror>();
            mirror.SetRobot(left);

            mirror.Mirror(true, new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.identity));

            Assert.AreEqual(1, leftModel.SolveTargets.Count);
            Assert.AreEqual(new Vector3(0.1f, 0.2f, 0.3f), leftModel.SolveTargets[0]);
            Assert.AreEqual(1, leftModel.BeginCalls);
        }

        [Test]
        public void AnAssignedRobot_WinsOverTheOneThePluginHandsOver()
        {
            var mirror = go.AddComponent<HandMirror>();
            mirror.SetRobotOverride(right);
            mirror.SetRobot(left);

            Assert.AreSame(right, mirror.Target);
            mirror.Mirror(true, new Pose(Vector3.one, Quaternion.identity));

            Assert.AreEqual(1, rightModel.SolveTargets.Count);
            CollectionAssert.IsEmpty(leftModel.SolveTargets, "The plugin's robot is only the default.");
        }

        [Test]
        public void TheGripperMirror_TakesItsJointAndAnglesFromTheProfile()
        {
            var mirror = go.AddComponent<Robotiq2fGripperMirror>();
            mirror.SetRobot(left);
            Vector3 thumb = Vector3.zero;

            // A closed pinch is the closed angle.
            mirror.Mirror(thumb, thumb, Quaternion.identity, Quaternion.identity);
            var applied = leftModel.Applied[^1];
            CollectionAssert.AreEqual(new[] { "finger_joint" }, applied.names);
            Assert.AreEqual(0.8, applied.positions[0], 1e-6);
            Assert.AreEqual(0.8f * Mathf.Rad2Deg, mirror.LastJointAngleDegrees, 1e-3f);

            // Half of the widest pinch (0.11 m by default) is half way.
            mirror.Mirror(thumb, new Vector3(0.055f, 0f, 0f), Quaternion.identity, Quaternion.identity);
            Assert.AreEqual(0.4, leftModel.Applied[^1].positions[0], 1e-6);

            // A wide pinch is fully open.
            mirror.Mirror(thumb, new Vector3(0.5f, 0f, 0f), Quaternion.identity, Quaternion.identity);
            Assert.AreEqual(0.0, leftModel.Applied[^1].positions[0], 1e-6);
            Assert.AreEqual(3, leftModel.SolveTargets.Count, "The pinch midpoint is the end-effector target.");
        }

        [Test]
        public void TheGripperMirror_KeepsAnAssignedRobot()
        {
            var mirror = go.AddComponent<Robotiq2fGripperMirror>();
            mirror.SetRobotOverride(right);
            mirror.SetRobot(left);

            mirror.Mirror(Vector3.zero, Vector3.zero, Quaternion.identity, Quaternion.identity);

            Assert.AreEqual(1, rightModel.Applied.Count);
            CollectionAssert.IsEmpty(leftModel.Applied);
        }

        [Test]
        public void ARobotWithoutAGripperDescription_IsNotDriven_AndSaysSoOnce()
        {
            profile.gripper = new GripperSpec();
            var mirror = go.AddComponent<Robotiq2fGripperMirror>();
            mirror.SetRobot(left);
            LogAssert.Expect(LogType.Warning, new Regex("no gripper description"));
            int warnings = 0;
            Application.LogCallback count = (message, _, type) =>
            {
                if (type == LogType.Warning && message.Contains("no gripper description")) warnings++;
            };
            Application.logMessageReceived += count;
            try
            {
                mirror.Mirror(Vector3.zero, Vector3.zero, Quaternion.identity, Quaternion.identity);
                mirror.Mirror(Vector3.zero, Vector3.zero, Quaternion.identity, Quaternion.identity);
            }
            finally
            {
                Application.logMessageReceived -= count;
            }

            CollectionAssert.IsEmpty(leftModel.Applied);
            Assert.AreEqual(1, warnings);
        }
    }
}
