using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The articulation controller driven by a robot profile: joint roles decide what is arm,
    /// gripper or left alone; the initial pose and the gripper come from the profile. The robot
    /// is two revolute arm joints, a hand, and a revolute two-finger gripper (a driver joint and
    /// a follower that mirrors it), the shape the prismatic-finger guess gets wrong.
    /// </summary>
    public class RobotProfileControllerTests
    {
        private const float Closed = 0.8f;      // driver angle, radians
        private const float OpenWidth = 0.085f; // metres

        private GameObject robot;
        private Transform hand;
        private RobotProfile profile;
        private RobotRegistry robots;
        private DirectArticulationIKController ik;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profile = RevoluteGripperArm();
            robots = FakeRobots.Registry(new FakeRosBus());

            robot = new GameObject("robot");
            FakeRobots.Attach(robots, robot, "arm", "", profile);
            robot.AddComponent<ArticulationBody>();

            GameObject link1 = Child(robot.transform, "link1");
            AddUrdfJoint(link1, "UrdfJointRevolute", "j1", ArticulationJointType.RevoluteJoint, -120f, 120f);
            GameObject link2 = Child(link1.transform, "link2");
            AddUrdfJoint(link2, "UrdfJointRevolute", "j2", ArticulationJointType.RevoluteJoint, -120f, 120f);

            GameObject handObject = Child(link2.transform, "hand");
            handObject.AddComponent<ArticulationBody>().jointType = ArticulationJointType.FixedJoint;
            hand = handObject.transform;

            AddUrdfJoint(Child(hand, "left_knuckle"), "UrdfJointRevolute", "finger_joint", ArticulationJointType.RevoluteJoint, 0f, 50f);
            AddUrdfJoint(Child(hand, "right_knuckle"), "UrdfJointRevolute", "right_finger_joint", ArticulationJointType.RevoluteJoint, -50f, 0f);

            // The articulation exists in the physics scene after a step.
            yield return new WaitForFixedUpdate();
            yield return null;

            ik = Child(robot.transform, "ik").AddComponent<DirectArticulationIKController>();
        }

        [TearDown]
        public void TearDown()
        {
            FakeRobots.DestroyAll(robots);
            if (robot != null) UnityEngine.Object.DestroyImmediate(robot);
            UnityEngine.Object.DestroyImmediate(profile);
        }

        // ---------- roles ----------

        [Test]
        public void JointRoles_KeepARevoluteGripperOutOfTheArm()
        {
            ik.Configure(robot.transform, hand);

            Assert.AreSame(profile, ik.Profile, "The profile is the RobotInstance's.");
            CollectionAssert.AreEqual(new[] { "j1", "j2" }, ik.JointNames, "IK drives the arm joints only.");
            CollectionAssert.AreEqual(new[] { "j1", "j2", "finger_joint", "right_finger_joint" }, ik.GetJointStateNames());
            Assert.IsTrue(ik.HasGripper);
            Assert.AreEqual(2, ik.Joints.Count);
        }

        [Test]
        public void WithoutAProfile_EveryRevoluteJointIsGuessedToBeArm()
        {
            ik.Configure(robot.transform, hand, null);

            CollectionAssert.AreEqual(new[] { "j1", "j2", "finger_joint", "right_finger_joint" }, ik.JointNames);
            Assert.IsFalse(ik.HasGripper, "Only prismatic joints are guessed to be fingers.");
        }

        [Test]
        public void APassiveJoint_IsNotDriven()
        {
            profile.joints[1].role = JointRole.Passive;
            ik.Configure(robot.transform, hand);

            CollectionAssert.AreEqual(new[] { "j1" }, ik.JointNames);
            CollectionAssert.DoesNotContain(ik.GetJointStateNames(), "j2");
        }

        [Test]
        public void AJointTheProfileDoesNotList_IsLeftAlone_WithAWarning()
        {
            profile.joints = profile.joints.Where(j => j.name != "j2").ToArray();
            profile.poses = Array.Empty<NamedPose>();
            profile.initialPose = "";
            LogAssert.Expect(LogType.Warning, new Regex("joint 'j2' is not in the profile"));

            ik.Configure(robot.transform, hand);

            CollectionAssert.AreEqual(new[] { "j1" }, ik.JointNames);
        }

        // ---------- initial pose ----------

        [Test]
        public void TheInitialPose_ComesFromTheProfile()
        {
            ik.Configure(robot.transform, hand);

            Assert.AreEqual(0.5f, Angle("j1"), 1e-5f);
            Assert.AreEqual(-0.5f, Angle("j2"), 1e-5f);
        }

        [Test]
        public void WithoutAnInitialPose_TheRobotStaysAsImported()
        {
            profile.initialPose = "";
            ik.Configure(robot.transform, hand);

            Assert.AreEqual(0f, Angle("j1"), 1e-6f);
            Assert.AreEqual(0f, Angle("j2"), 1e-6f);
        }

        // ---------- gripper ----------

        [Test]
        public void TheGripper_OpensAndClosesByItsDescription_AndTheFollowerMirrorsTheDriver()
        {
            ik.Configure(robot.transform, hand);
            Assert.AreEqual(OpenWidth, ik.GripperOpenWidth, 1e-6f);

            ik.SetGripperWidth(0f);
            Assert.AreEqual(Closed, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-Closed, Angle("right_finger_joint"), 1e-5f, "The follower mimics the driver with multiplier -1.");
            Assert.AreEqual(0f, ik.GripperWidth, 1e-6f);

            ik.SetGripperWidth(OpenWidth);
            Assert.AreEqual(0f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(0f, Angle("right_finger_joint"), 1e-5f);
            Assert.AreEqual(OpenWidth, ik.GripperWidth, 1e-6f);

            ik.SetGripperWidth(OpenWidth / 2f);
            Assert.AreEqual(Closed / 2f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-Closed / 2f, Angle("right_finger_joint"), 1e-5f);

            Assert.AreEqual(0.5f, Angle("j1"), 1e-5f, "The gripper does not move the arm.");
        }

        [UnityTest]
        public IEnumerator OpenAndClose_MoveAtTheProfilesSpeed()
        {
            profile.gripper.speed = 1e6f; // done within a frame, however short the frame
            ik.Configure(robot.transform, hand);
            ik.SetGripperWidth(OpenWidth);

            ik.CloseGripper();
            yield return null;
            yield return null;
            Assert.AreEqual(Closed, Angle("finger_joint"), 1e-4f);

            ik.ToggleGripper();
            yield return null;
            yield return null;
            Assert.AreEqual(0f, Angle("finger_joint"), 1e-4f);
        }

        [Test]
        public void GripperCommands_AreAnnounced_SoThatTheRealGripperCanFollow()
        {
            ik.Configure(robot.transform, hand);
            ik.SetGripperWidth(OpenWidth);
            var commands = new System.Collections.Generic.List<bool>();
            ik.GripperCommanded += commands.Add;

            ik.ToggleGripper();   // open, so: close
            ik.ToggleGripper();   // on its way to closed, so: open
            ik.CloseGripper();
            ik.OpenGripper();
            CollectionAssert.AreEqual(new[] { false, true, false, true }, commands);

            ik.SetGripperWidth(0f);
            Assert.AreEqual(4, commands.Count, "Setting the width directly (a mirrored hand, a joint state) is not a command to the real gripper.");
        }

        [Test]
        public void AnInteraction_AnnouncesThatTheRobotsPoseWasTaken()
        {
            ik.Configure(robot.transform, hand);
            int taken = 0;
            ik.ControlTaken += () => taken++;

            ik.BeginInteraction();
            ik.EndInteraction();
            Assert.AreEqual(1, taken, "A joint-state mirror lets go when the user starts to move the robot.");
            ik.TakeControl();
            Assert.AreEqual(2, taken, "A playback announces itself the same way.");
        }

        [Test]
        public void AJointState_ThatNamesOnlyTheDriver_MovesTheFollowerToo()
        {
            ik.Configure(robot.transform, hand);

            ik.ApplyJointState(new[] { "finger_joint" }, new[] { 0.3 });
            Assert.AreEqual(0.3f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-0.3f, Angle("right_finger_joint"), 1e-5f);

            // A state that names only the follower moves the driver to match.
            ik.ApplyJointState(new[] { "right_finger_joint" }, new[] { -0.6 });
            Assert.AreEqual(0.6f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-0.6f, Angle("right_finger_joint"), 1e-5f);

            // A state that names both is applied as given.
            ik.ApplyJointState(new[] { "finger_joint", "right_finger_joint" }, new[] { 0.1, -0.2 });
            Assert.AreEqual(0.1f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-0.2f, Angle("right_finger_joint"), 1e-5f);
        }

        [Test]
        public void GripperPositions_AreClampedToTheProfilesLimits()
        {
            ik.Configure(robot.transform, hand);

            ik.ApplyJointState(new[] { "finger_joint", "right_finger_joint" }, new[] { 3.0, -3.0 });

            // The profile says 0.87 rad; the drive on the body says 50 degrees (0.8727 rad). The profile is the limit model.
            Assert.AreEqual(0.87f, Angle("finger_joint"), 1e-5f);
            Assert.AreEqual(-0.87f, Angle("right_finger_joint"), 1e-5f);
        }

        // ---------- helpers ----------

        private float Angle(string jointName)
        {
            Assert.IsTrue(ik.TryGetJointAngle(jointName, out float position), $"no joint '{jointName}'");
            return position;
        }

        private static RobotProfile RevoluteGripperArm()
        {
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.baseLink = "robot";
            profile.endEffectorLink = "hand";
            profile.joints = new[]
            {
                new JointSpec { name = "j1", childLink = "link1", role = JointRole.Arm, urdfLower = -2.09f, urdfUpper = 2.09f },
                new JointSpec { name = "j2", childLink = "link2", role = JointRole.Arm, urdfLower = -2.09f, urdfUpper = 2.09f },
                new JointSpec { name = "finger_joint", childLink = "left_knuckle", role = JointRole.Gripper, urdfLower = 0f, urdfUpper = 0.87f },
                new JointSpec
                {
                    name = "right_finger_joint", childLink = "right_knuckle", role = JointRole.Gripper, urdfLower = -0.87f, urdfUpper = 0f,
                    mimic = new MimicSpec { leader = "finger_joint", multiplier = -1f }
                },
            };
            profile.poses = new[]
            {
                new NamedPose
                {
                    name = "home",
                    joints = new[] { new JointValue { joint = "j1", value = 0.5f }, new JointValue { joint = "j2", value = -0.5f } }
                }
            };
            profile.initialPose = "home";
            profile.gripper = new GripperSpec
            {
                kind = GripperKind.Revolute, driverJoint = "finger_joint", open = 0f, closed = Closed, openWidthMetres = OpenWidth, speed = 0.1f
            };
            return profile;
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        // The controller recognises URDF joints by type name, without referencing the importer;
        // the test builds them the same way.
        private static void AddUrdfJoint(GameObject go, string typeName, string jointName,
            ArticulationJointType jointType, float lowerLimit, float upperLimit)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Unity.Robotics.UrdfImporter." + typeName))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, $"URDF importer type {typeName} not found");
            Component joint = go.AddComponent(type);
            type.GetField("jointName").SetValue(joint, jointName);

            ArticulationBody body = go.GetComponent<ArticulationBody>();
            body.jointType = jointType;
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = lowerLimit;
            drive.upperLimit = upperLimit;
            body.xDrive = drive;
        }
    }
}
