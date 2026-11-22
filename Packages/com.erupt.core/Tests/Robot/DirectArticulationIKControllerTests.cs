using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The articulation controller on a small URDF-style robot: one revolute arm joint, a hand
    /// and two prismatic fingers. Covers what joint states do to the fingers, how the robot's
    /// colliders are kept out of physics contacts, and the link lookup cache.
    /// </summary>
    public class DirectArticulationIKControllerTests
    {
        private GameObject robot, host;
        private Transform hand;
        private DirectArticulationIKController ik;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            robot = new GameObject("robot");
            robot.AddComponent<ArticulationBody>();

            GameObject link1 = Child(robot.transform, "fr3_link1");
            ArticulationBody joint1 = AddUrdfJoint(link1, "UrdfJointRevolute", "fr3_joint1");
            joint1.jointType = ArticulationJointType.RevoluteJoint;
            link1.AddComponent<BoxCollider>();

            GameObject handObject = Child(link1.transform, "fr3_hand");
            handObject.AddComponent<ArticulationBody>().jointType = ArticulationJointType.FixedJoint;
            hand = handObject.transform;

            AddFinger("fr3_leftfinger", "fr3_finger_joint1");
            AddFinger("fr3_rightfinger", "fr3_finger_joint2");

            // The articulation exists in the physics scene after a step.
            yield return new WaitForFixedUpdate();
            yield return null;

            host = new GameObject("ik");
            ik = host.AddComponent<DirectArticulationIKController>();
            Set("robotRoot", robot.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            if (robot != null) UnityEngine.Object.DestroyImmediate(robot);
        }

        [Test]
        public void Configure_FindsTheArmAndTheFingers()
        {
            ik.Configure(robot.transform, hand);

            CollectionAssert.AreEqual(new[] { "fr3_joint1" }, ik.JointNames);
            CollectionAssert.AreEqual(new[] { "fr3_joint1", "fr3_finger_joint1", "fr3_finger_joint2" }, ik.GetJointStateNames());
            Assert.IsTrue(ik.HasGripper);
        }

        // MoveIt trajectories and joint states name the active finger only; the other one
        // mimics it in the URDF and has to follow here.
        [Test]
        public void JointState_NamingOneFinger_MovesTheOtherAlong()
        {
            ik.Configure(robot.transform, hand);

            ik.ApplyJointState(new[] { "fr3_joint1", "fr3_finger_joint1" }, new[] { 0.2, 0.03 });

            Assert.AreEqual(0.03f, Angle("fr3_finger_joint1"), 1e-6f);
            Assert.AreEqual(0.03f, Angle("fr3_finger_joint2"), 1e-6f, "The finger the state does not name follows the one it does.");
            Assert.AreEqual(0.06f, ik.GripperWidth, 1e-6f);

            // The same through the float overload.
            ik.ApplyJointState(new[] { "fr3_finger_joint1" }, new[] { 0.01f });
            Assert.AreEqual(0.01f, Angle("fr3_finger_joint2"), 1e-6f);
        }

        [Test]
        public void JointState_NamingBothFingers_OrNone_LeavesThemAsGiven()
        {
            ik.Configure(robot.transform, hand);

            ik.ApplyJointState(new[] { "fr3_finger_joint1", "fr3_finger_joint2" }, new[] { 0.01, 0.02 });
            Assert.AreEqual(0.01f, Angle("fr3_finger_joint1"), 1e-6f);
            Assert.AreEqual(0.02f, Angle("fr3_finger_joint2"), 1e-6f);

            ik.ApplyJointState(new[] { "fr3_joint1" }, new[] { 0.3 });
            Assert.AreEqual(0.01f, Angle("fr3_finger_joint1"), 1e-6f, "An arm-only state does not touch the fingers.");
            Assert.AreEqual(0.02f, Angle("fr3_finger_joint2"), 1e-6f);
        }

        [Test]
        public void Configure_RemembersTheRobotItWasGiven()
        {
            Set("robotRoot", null);
            ik.Configure(robot.transform, hand);

            Assert.AreSame(robot.transform, ik.Root);
            Assert.AreSame(hand, ik.EndEffector);
            Assert.IsTrue(ik.TryFindLinkTransform("fr3_hand", out Transform link), "Links are looked up under the configured root.");
            Assert.AreSame(hand, link);
        }

        [Test]
        public void WithoutAProfile_TheRobotKeepsThePoseItHas()
        {
            ArticulationBody joint1 = robot.transform.Find("fr3_link1").GetComponent<ArticulationBody>();
            joint1.jointPosition = new ArticulationReducedSpace(0.3f);

            ik.Configure(robot.transform, hand);

            Assert.IsNull(ik.Profile);
            Assert.AreEqual(0.3f, Angle("fr3_joint1"), 1e-5f, "No pose is built into the controller.");
        }

        // The fingers behave the same whether they are guessed (no profile) or described by one:
        // same joints in the same order, same split of a width, same open width.
        [Test]
        public void ADescribedPrismaticGripper_BehavesLikeTheGuessedOne()
        {
            ik.Configure(robot.transform, hand);
            string[] guessedNames = ik.GetJointStateNames();
            float guessedOpen = ik.GripperOpenWidth;
            ik.SetGripperWidth(0.06f);
            float[] guessed = ik.GetJointStatePositions();
            float guessedWidth = ik.GripperWidth;

            RobotProfile profile = ScriptableObject.CreateInstance<RobotProfile>();
            try
            {
                profile.joints = new[]
                {
                    new JointSpec { name = "fr3_joint1", childLink = "fr3_link1", role = JointRole.Arm },
                    new JointSpec { name = "fr3_finger_joint1", childLink = "fr3_leftfinger", kind = JointKind.Prismatic, role = JointRole.Gripper, urdfUpper = 0.04f },
                    new JointSpec
                    {
                        name = "fr3_finger_joint2", childLink = "fr3_rightfinger", kind = JointKind.Prismatic, role = JointRole.Gripper, urdfUpper = 0.04f,
                        mimic = new MimicSpec { leader = "fr3_finger_joint1" }
                    },
                };
                profile.gripper = new GripperSpec
                {
                    kind = GripperKind.Prismatic, driverJoint = "fr3_finger_joint1", open = 0.04f, closed = 0f, openWidthMetres = 0.08f
                };
                CollectionAssert.IsEmpty(profile.Validate());

                ik.SetGripperWidth(0f);
                ik.Configure(robot.transform, hand, profile);

                CollectionAssert.AreEqual(guessedNames, ik.GetJointStateNames());
                Assert.AreEqual(guessedOpen, ik.GripperOpenWidth, 1e-6f);
                ik.SetGripperWidth(0.06f);
                CollectionAssert.AreEqual(guessed, ik.GetJointStatePositions());
                Assert.AreEqual(guessedWidth, ik.GripperWidth, 1e-6f);
                Assert.AreEqual(0.03f, Angle("fr3_finger_joint2"), 1e-6f);

                ik.SetGripperWidth(1f);
                Assert.AreEqual(0.04f, Angle("fr3_finger_joint1"), 1e-6f, "Clamped to fully open.");
                Assert.AreEqual(0.04f, Angle("fr3_finger_joint2"), 1e-6f);

                ik.ApplyJointState(new[] { "fr3_finger_joint1" }, new[] { 0.01 });
                Assert.AreEqual(0.01f, Angle("fr3_finger_joint2"), 1e-6f, "The mimic finger follows a state that names only the driver.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Configure_TakesTheRobotsCollidersOutOfPhysicsContacts()
        {
            ik.Configure(robot.transform, hand);

            Collider[] colliders = robot.GetComponentsInChildren<Collider>(true);
            Assert.IsNotEmpty(colliders);
            foreach (Collider collider in colliders)
            {
                Assert.AreEqual(~0, collider.excludeLayers.value, $"{collider.name} must not take part in contacts.");
                Assert.IsTrue(collider.enabled, "The colliders stay enabled so the robot can still be ray-selected.");
            }
        }

        [Test]
        public void Configure_LeavesTheCollidersAlone_WhenContactsAreWanted()
        {
            Set("ignorePhysicsContacts", false);
            ik.Configure(robot.transform, hand);

            foreach (Collider collider in robot.GetComponentsInChildren<Collider>(true))
                Assert.AreEqual(0, collider.excludeLayers.value);
        }

        [Test]
        public void ClearLinkCache_ResolvesLinksAfresh()
        {
            GameObject tool = Child(hand, "tool");
            Assert.IsTrue(ik.TryFindLinkTransform("tool", out Transform first));
            Assert.AreSame(tool.transform, first);

            // The hierarchy changes: another object now carries the name.
            tool.name = "old_tool";
            GameObject replacement = Child(hand, "tool");
            Assert.IsTrue(ik.TryFindLinkTransform("tool", out Transform cached));
            Assert.AreSame(tool.transform, cached, "A resolved link is remembered.");

            ik.ClearLinkCache();
            Assert.IsTrue(ik.TryFindLinkTransform("tool", out Transform fresh));
            Assert.AreSame(replacement.transform, fresh);
        }

        // ---------- helpers ----------

        private float Angle(string jointName)
        {
            Assert.IsTrue(ik.TryGetJointAngle(jointName, out float position), $"no joint '{jointName}'");
            return position;
        }

        private void Set(string field, object value) =>
            typeof(DirectArticulationIKController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ik, value);

        private void AddFinger(string linkName, string jointName)
        {
            GameObject finger = Child(hand, linkName);
            ArticulationBody body = AddUrdfJoint(finger, "UrdfJointPrismatic", jointName);
            body.jointType = ArticulationJointType.PrismaticJoint;
            body.linearLockX = ArticulationDofLock.LimitedMotion;
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = 0f;
            drive.upperLimit = 0.04f;
            body.xDrive = drive;
            finger.AddComponent<BoxCollider>();
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        // The controller recognises URDF joints by type name, without referencing the importer;
        // the test builds them the same way.
        private static ArticulationBody AddUrdfJoint(GameObject go, string typeName, string jointName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Unity.Robotics.UrdfImporter." + typeName))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, $"URDF importer type {typeName} not found");
            Component joint = go.AddComponent(type);
            type.GetField("jointName").SetValue(joint, jointName);
            return go.GetComponent<ArticulationBody>();
        }
    }
}
