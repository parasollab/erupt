using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Erupt.Editor;
using Erupt.Robot;

namespace Erupt.Editor.Tests
{
    /// <summary>
    /// The profile creator on a small imported-style robot (URDF joint components on
    /// articulation bodies), and on the FR3 the project ships.
    /// </summary>
    public class RobotProfileCreatorTests
    {
        private GameObject robot;
        private RobotProfile profile;

        [SetUp]
        public void SetUp()
        {
            robot = new GameObject("test_arm");
            GameObject baseLink = Child(robot.transform, "base_link");
            baseLink.AddComponent<ArticulationBody>();

            GameObject link1 = Child(baseLink.transform, "link1");
            AddUrdfJoint(link1, "UrdfJointRevolute", "joint1", ArticulationJointType.RevoluteJoint, -90f, 45f);
            GameObject link2 = Child(link1.transform, "link2");
            AddUrdfJoint(link2, "UrdfJointContinuous", "joint2", ArticulationJointType.RevoluteJoint, 0f, 0f);
            GameObject hand = Child(link2.transform, "hand");
            AddUrdfJoint(hand, "UrdfJointFixed", "hand_joint", ArticulationJointType.FixedJoint, 0f, 0f);

            AddUrdfJoint(Child(hand.transform, "left_finger"), "UrdfJointPrismatic", "finger1", ArticulationJointType.PrismaticJoint, 0f, 0.04f);
            Component follower = AddUrdfJoint(Child(hand.transform, "right_finger"), "UrdfJointPrismatic", "finger2", ArticulationJointType.PrismaticJoint, 0f, 0.04f);
            Set(follower, "mimic", true);
            Set(follower, "mimicJointName", "finger1");
            Set(follower, "mimicMultiplier", 1.0);
            Set(follower, "mimicOffset", 0.0);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(robot);
            if (profile != null) UnityEngine.Object.DestroyImmediate(profile);
        }

        [Test]
        public void Build_ReadsTheJoints_WithTypeLimitsAndMimic()
        {
            profile = RobotProfileCreator.Build(robot);

            CollectionAssert.AreEqual(new[] { "joint1", "joint2", "finger1", "finger2" }, profile.joints.Select(j => j.name));
            CollectionAssert.AreEqual(new[] { "link1", "link2", "left_finger", "right_finger" }, profile.joints.Select(j => j.childLink));

            JointSpec joint1 = profile.joints[0];
            Assert.AreEqual(JointKind.Revolute, joint1.kind);
            Assert.AreEqual(-90f * Mathf.Deg2Rad, joint1.urdfLower, 1e-5f, "Revolute limits are radians.");
            Assert.AreEqual(45f * Mathf.Deg2Rad, joint1.urdfUpper, 1e-5f);

            Assert.AreEqual(JointKind.Continuous, profile.joints[1].kind);
            Assert.IsFalse(profile.joints[1].HasLimits);

            JointSpec finger2 = profile.joints[3];
            Assert.AreEqual(JointKind.Prismatic, finger2.kind);
            Assert.AreEqual(0.04f, finger2.urdfUpper, 1e-6f, "Prismatic limits are metres.");
            Assert.AreEqual("finger1", finger2.mimic.leader);
            Assert.AreEqual(1f, finger2.mimic.multiplier);
            Assert.IsFalse(profile.joints[2].IsMimic);
        }

        [Test]
        public void Build_GuessesRolesLinksAndTheGripper()
        {
            profile = RobotProfileCreator.Build(robot);

            CollectionAssert.AreEqual(
                new[] { JointRole.Arm, JointRole.Arm, JointRole.Gripper, JointRole.Gripper },
                profile.joints.Select(j => j.role));
            Assert.AreEqual("test_arm", profile.displayName);
            Assert.AreEqual("base_link", profile.baseLink);
            Assert.AreEqual("link2", profile.endEffectorLink, "The link of the last arm joint.");

            Assert.AreEqual(GripperKind.Prismatic, profile.gripper.kind);
            Assert.AreEqual("finger1", profile.gripper.driverJoint, "The finger that follows no other.");
            Assert.AreEqual(0.04f, profile.gripper.open, 1e-6f);
            Assert.AreEqual(0f, profile.gripper.closed, 1e-6f);
            Assert.AreEqual(0.08f, profile.gripper.openWidthMetres, 1e-6f);

            Assert.AreEqual("world", profile.planningFrame);
            Assert.AreEqual("joint_states", profile.jointStatesTopic);
            Assert.IsTrue(profile.Names.IsIdentity);
            CollectionAssert.IsEmpty(profile.Validate(), "What the creator writes is consistent.");
        }

        [Test]
        public void Build_ARobotWithoutFingers_HasNoGripper()
        {
            UnityEngine.Object.DestroyImmediate(robot.transform.Find("base_link/link1/link2/hand/left_finger").gameObject);
            UnityEngine.Object.DestroyImmediate(robot.transform.Find("base_link/link1/link2/hand/right_finger").gameObject);

            profile = RobotProfileCreator.Build(robot);

            Assert.AreEqual(GripperKind.None, profile.gripper.kind);
            Assert.AreEqual(2, profile.joints.Length);
        }

        // ---------- helpers ----------

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        // URDF joint components are found by type name, as the creator itself does.
        private static Component AddUrdfJoint(GameObject go, string typeName, string jointName,
            ArticulationJointType jointType, float lowerLimit, float upperLimit)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Unity.Robotics.UrdfImporter." + typeName))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, $"URDF importer type {typeName} not found");
            Component joint = go.AddComponent(type);
            Set(joint, "jointName", jointName);

            ArticulationBody body = go.GetComponent<ArticulationBody>();
            body.jointType = jointType;
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = lowerLimit;
            drive.upperLimit = upperLimit;
            body.xDrive = drive;
            return joint;
        }

        private static void Set(Component target, string field, object value) =>
            target.GetType().GetField(field).SetValue(target, value);
    }
}
