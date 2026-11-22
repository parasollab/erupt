using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The robot description on its own: what <see cref="RobotProfile.Validate"/> reports, and
    /// how joint and link names translate between the Unity robot and the ROS robot.
    /// </summary>
    public class RobotProfileTests
    {
        private RobotProfile profile;

        [SetUp]
        public void SetUp() => profile = TwoFingerArm();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(profile);

        // ---------- validation ----------

        [Test]
        public void AConsistentProfile_HasNoProblems()
        {
            CollectionAssert.IsEmpty(profile.Validate());
        }

        [Test]
        public void DuplicateJointNames_AreReported()
        {
            profile.joints[1].name = "j1";
            profile.poses[0].joints[1].joint = "j1";
            AssertOneProblem("'j1' is listed more than once");
        }

        [Test]
        public void AMimicLeaderThatIsNotAJoint_IsReported()
        {
            profile.joints[3].mimic.leader = "missing";
            AssertOneProblem("mimics 'missing'");
        }

        [Test]
        public void AJointThatMimicsItself_IsReported()
        {
            profile.joints[3].mimic.leader = "finger2";
            AssertOneProblem("mimics itself");
        }

        [Test]
        public void AnInitialPoseThatIsNotANamedPose_IsReported()
        {
            profile.initialPose = "nowhere";
            AssertOneProblem("initial pose 'nowhere'");
        }

        [Test]
        public void APoseNamingAnUnknownJoint_IsReported()
        {
            profile.poses[0].joints[0].joint = "j9";
            AssertOneProblem("names 'j9'");
        }

        [Test]
        public void APoseValueOutsideTheLimits_IsReported()
        {
            profile.poses[0].joints[1].value = 3f;
            AssertOneProblem("puts 'j2' at 3");
        }

        [Test]
        public void OverriddenLimits_AreTheOnesChecked()
        {
            profile.poses[0].joints[1].value = 1.5f;
            Assert.AreEqual(1, profile.Validate().Count, "1.5 is outside the URDF limits [-1, 1].");

            profile.joints[1].overrideLimits = true;
            profile.joints[1].lower = -2f;
            profile.joints[1].upper = 2f;
            CollectionAssert.IsEmpty(profile.Validate());
            Assert.AreEqual(2f, profile.joints[1].Upper);
        }

        [Test]
        public void AContinuousJoint_HasNoLimitsToViolate()
        {
            profile.joints[0].kind = JointKind.Continuous;
            profile.joints[0].urdfLower = 0f;
            profile.joints[0].urdfUpper = 0f;
            profile.poses[0].joints[0].value = 20f;
            CollectionAssert.IsEmpty(profile.Validate());
        }

        [Test]
        public void AGripperDriverThatIsNotAGripperJoint_IsReported()
        {
            profile.gripper.driverJoint = "j1";
            List<string> problems = profile.Validate();
            Assert.That(problems, Has.Some.Contains("has the role Arm"));
            Assert.That(problems, Has.Some.Contains("is Prismatic but its driver joint 'j1' is Revolute"));
        }

        [Test]
        public void AGripperDriverThatIsNotAJoint_IsReported()
        {
            profile.gripper.driverJoint = "thumb";
            AssertOneProblem("driver joint 'thumb' is not a joint");
        }

        [Test]
        public void AGripperOpenPositionOutsideTheDriversLimits_IsReported()
        {
            profile.gripper.open = 0.05f;
            AssertOneProblem("open position 0.05");
        }

        [Test]
        public void LimitsTheWrongWayRound_AreReported()
        {
            profile.joints[0].urdfLower = 2f;
            profile.joints[0].urdfUpper = -2f;
            Assert.That(profile.Validate(), Has.Some.Contains("lower limit (2) above its upper limit (-2)"));
        }

        [Test]
        public void AProfileWithoutAGripper_NeedsNoGripperData()
        {
            profile.gripper = new GripperSpec();
            CollectionAssert.IsEmpty(profile.Validate());
        }

        // ---------- lookups ----------

        [Test]
        public void JointsPosesAndRoles_AreLookedUpByName()
        {
            Assert.IsTrue(profile.TryGetJoint("finger1", out JointSpec finger));
            Assert.AreEqual(JointKind.Prismatic, finger.kind);
            Assert.IsFalse(profile.TryGetJoint("thumb", out _));

            Assert.AreSame(profile.poses[0], profile.InitialPose);
            CollectionAssert.AreEqual(new[] { "j1", "j2" }, profile.JointNames(JointRole.Arm));
            CollectionAssert.AreEqual(new[] { "finger1", "finger2" }, profile.JointNames(JointRole.Gripper));
        }

        [Test]
        public void TheGripperSpec_ConvertsBetweenWidthAndDriverPosition()
        {
            GripperSpec gripper = profile.gripper;
            Assert.AreEqual(0.04f, gripper.DriverAt(0.08f), 1e-6f);
            Assert.AreEqual(0.02f, gripper.DriverAt(0.04f), 1e-6f);
            Assert.AreEqual(0.04f, gripper.DriverAt(1f), 1e-6f, "Clamped to fully open.");
            Assert.AreEqual(0.08f, gripper.WidthAt(0.04f), 1e-6f);
            Assert.AreEqual(0f, gripper.WidthAt(-1f), 1e-6f, "Clamped to closed.");

            // A revolute gripper closes as its driver angle grows.
            var revolute = new GripperSpec { kind = GripperKind.Revolute, driverJoint = "finger_joint", open = 0f, closed = 0.8f, openWidthMetres = 0.085f };
            Assert.AreEqual(0.8f, revolute.DriverAt(0f), 1e-6f);
            Assert.AreEqual(0f, revolute.DriverAt(0.085f), 1e-6f);
            Assert.AreEqual(0.0425f, revolute.WidthAt(0.4f), 1e-6f);
        }

        [Test]
        public void AMimic_FollowsItsLeader_AndCanBeInverted()
        {
            var mimic = new MimicSpec { leader = "a", multiplier = -2f, offset = 0.5f };
            Assert.AreEqual(-1.5f, mimic.Follow(1f), 1e-6f);
            Assert.AreEqual(1f, mimic.LeaderFor(-1.5f), 1e-6f);
            Assert.IsFalse(new MimicSpec().IsSet);
        }

        // ---------- names ----------

        [Test]
        public void WithoutARule_NamesAreTheSameOnBothSides()
        {
            Assert.IsTrue(profile.Names.IsIdentity);
            Assert.AreEqual("fr3_joint1", profile.ToRos("fr3_joint1"));
            Assert.AreEqual("fr3_joint1", profile.ToLocal("fr3_joint1"));

            string[] names = { "a", "b" };
            Assert.AreSame(names, profile.Names.ToRos(names), "Nothing to translate: no copy is made.");
            Assert.IsTrue(RobotNameMap.Identity.IsIdentity);
        }

        [Test]
        public void APrefixRule_SwapsThePrefixInBothDirections()
        {
            profile.names = new RobotNameMap { localPrefix = "fr3_", rosPrefix = "panda_" };

            Assert.AreEqual("panda_joint1", profile.ToRos("fr3_joint1"));
            Assert.AreEqual("fr3_hand", profile.ToLocal("panda_hand"));
            Assert.AreEqual("camera_pan", profile.ToRos("camera_pan"), "A name without the prefix is left alone.");
            Assert.AreEqual("fr3_joint1", profile.ToLocal("fr3_joint1"), "A name already in the Unity form is left alone.");
            CollectionAssert.AreEqual(new[] { "panda_joint1", "world" }, profile.Names.ToRos(new[] { "fr3_joint1", "world" }));
            CollectionAssert.AreEqual(new[] { "fr3_joint1", "world" }, profile.Names.ToLocal(new[] { "panda_joint1", "world" }));
        }

        [Test]
        public void ExplicitPairs_WinOverThePrefixRule()
        {
            profile.names = new RobotNameMap
            {
                localPrefix = "fr3_",
                rosPrefix = "panda_",
                pairs = new[] { new NamePair { local = "fr3_hand_tcp", ros = "tool0" } }
            };

            Assert.AreEqual("tool0", profile.ToRos("fr3_hand_tcp"));
            Assert.AreEqual("fr3_hand_tcp", profile.ToLocal("tool0"));
            Assert.AreEqual("panda_hand", profile.ToRos("fr3_hand"));
            Assert.IsFalse(profile.Names.IsIdentity);
        }

        [Test]
        public void APrefixOnOneSideOnly_IsAddedAndRemoved()
        {
            profile.names = new RobotNameMap { localPrefix = "", rosPrefix = "left_" };
            Assert.AreEqual("left_joint1", profile.ToRos("joint1"));
            Assert.AreEqual("joint1", profile.ToLocal("left_joint1"));
            Assert.AreEqual("joint1", profile.ToLocal("joint1"));
        }

        [Test]
        public void EmptyAndNullNames_PassThrough()
        {
            profile.names = new RobotNameMap { localPrefix = "fr3_", rosPrefix = "panda_" };
            Assert.IsNull(profile.ToRos(null));
            Assert.AreEqual("", profile.ToLocal(""));
            CollectionAssert.IsEmpty(profile.Names.ToRos((string[])null));
        }

        // ---------- helpers ----------

        private void AssertOneProblem(string containing)
        {
            List<string> problems = profile.Validate();
            Assert.AreEqual(1, problems.Count, string.Join("\n", problems));
            StringAssert.Contains(containing, problems[0]);
        }

        // Two revolute arm joints and a prismatic two-finger hand whose second finger mimics the first.
        internal static RobotProfile TwoFingerArm()
        {
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.displayName = "Test arm";
            profile.baseLink = "base";
            profile.endEffectorLink = "hand";
            profile.armGroup = "arm";
            profile.gripperGroup = "hand";
            profile.joints = new[]
            {
                new JointSpec { name = "j1", childLink = "link1", kind = JointKind.Revolute, role = JointRole.Arm, urdfLower = -2f, urdfUpper = 2f, maxVelocity = 2f },
                new JointSpec { name = "j2", childLink = "link2", kind = JointKind.Revolute, role = JointRole.Arm, urdfLower = -1f, urdfUpper = 1f, maxVelocity = 2f },
                new JointSpec { name = "finger1", childLink = "leftfinger", kind = JointKind.Prismatic, role = JointRole.Gripper, urdfLower = 0f, urdfUpper = 0.04f },
                new JointSpec
                {
                    name = "finger2", childLink = "rightfinger", kind = JointKind.Prismatic, role = JointRole.Gripper, urdfLower = 0f, urdfUpper = 0.04f,
                    mimic = new MimicSpec { leader = "finger1", multiplier = 1f }
                },
            };
            profile.poses = new[]
            {
                new NamedPose
                {
                    name = "ready",
                    joints = new[] { new JointValue { joint = "j1", value = 0.5f }, new JointValue { joint = "j2", value = -0.5f } }
                }
            };
            profile.initialPose = "ready";
            profile.gripper = new GripperSpec
            {
                kind = GripperKind.Prismatic, driverJoint = "finger1", open = 0.04f, closed = 0f, openWidthMetres = 0.08f, speed = 0.1f
            };
            profile.jointStatesTopic = "joint_states";
            profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
            return profile;
        }
    }
}
