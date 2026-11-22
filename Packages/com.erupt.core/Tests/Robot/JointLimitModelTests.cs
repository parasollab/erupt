using System.Collections.Generic;
using NUnit.Framework;
using RosMessageTypes.Trajectory;
using UnityEngine;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The limit model on its own: what has limits, what a clamp does, how the profile's
    /// overrides and margin shape the limits, the velocity step, and the check of joint states
    /// and trajectories.
    /// </summary>
    public class JointLimitModelTests
    {
        private const float TwoPi = 2f * Mathf.PI;

        private RobotProfile profile;

        [TearDown]
        public void TearDown()
        {
            if (profile != null) Object.DestroyImmediate(profile);
        }

        private RobotProfile Profile(params JointSpec[] joints)
        {
            profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.joints = joints;
            return profile;
        }

        // ---------- one joint ----------

        [Test]
        public void ALimitedJoint_IsClampedToItsRange()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -1f, 2f);

            Assert.IsTrue(joint.HasLimits);
            Assert.AreEqual(2f, joint.Clamp(5f));
            Assert.AreEqual(-1f, joint.Clamp(-5f));
            Assert.AreEqual(0.3f, joint.Clamp(0.3f));
            Assert.AreEqual(2f, joint.Clamp(float.PositiveInfinity));
        }

        [Test]
        public void AContinuousJoint_PassesThrough_AndIsNotWrapped()
        {
            // A continuous joint keeps whatever range its description carries; it is never used.
            var joint = new JointLimit("j", JointKind.Continuous, -1f, 1f);

            Assert.IsFalse(joint.HasLimits);
            Assert.AreEqual(25f, joint.Clamp(25f), "Several turns stay several turns.");
            Assert.AreEqual(-25f, joint.Clamp(-25f));
            Assert.AreEqual(0.0, joint.Excess(1000.0));
        }

        [Test]
        public void AJointWithoutARange_HasNoLimits_InsteadOfBeingFrozen()
        {
            // 0/0 is what a joint imported without limits carries; clamping to it would pin the joint at zero.
            var none = new JointLimit("j", JointKind.Revolute, 0f, 0f);
            var inverted = new JointLimit("j", JointKind.Prismatic, 0.5f, 0.1f);

            Assert.IsFalse(none.HasLimits);
            Assert.AreEqual(1.3f, none.Clamp(1.3f));
            Assert.IsFalse(inverted.HasLimits);
            Assert.AreEqual(0.3f, inverted.Clamp(0.3f));
            Assert.IsFalse(default(JointLimit).HasLimits, "The default value is a joint without limits.");
            Assert.AreEqual(7f, default(JointLimit).Clamp(7f));
        }

        [Test]
        public void AJointBeyondAHalfTurn_KeepsItsFullRange()
        {
            var joint = new JointLimit("shoulder_pan", JointKind.Revolute, -TwoPi, TwoPi);

            Assert.AreEqual(5f, joint.Clamp(5f), "5 rad is inside +-2 pi and is not wrapped to 5 - 2 pi.");
            Assert.AreEqual(TwoPi, joint.Clamp(7f));
            Assert.AreEqual(-TwoPi, joint.Clamp(-7f));
        }

        [Test]
        public void TheMargin_ShrinksBothEnds()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -1f, 2f, 0f, 0.1f);

            Assert.AreEqual(-0.9f, joint.Lower, 1e-6f);
            Assert.AreEqual(1.9f, joint.Upper, 1e-6f);
            Assert.AreEqual(1.9f, joint.Clamp(2f), 1e-6f);
        }

        [Test]
        public void AMarginWiderThanTheRange_LeavesTheMiddle()
        {
            var joint = new JointLimit("j", JointKind.Prismatic, 0f, 0.04f, 0f, 0.5f);

            Assert.IsTrue(joint.HasLimits);
            Assert.AreEqual(0.02f, joint.Lower, 1e-6f);
            Assert.AreEqual(0.02f, joint.Upper, 1e-6f);
            Assert.AreEqual(0.02f, joint.Clamp(1f), 1e-6f);
        }

        [Test]
        public void Excess_IsSignedAndZeroInside()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -1f, 2f);

            Assert.AreEqual(0.5, joint.Excess(2.5), 1e-9);
            Assert.AreEqual(-0.25, joint.Excess(-1.25), 1e-9);
            Assert.AreEqual(0.0, joint.Excess(0.0));
            Assert.IsTrue(double.IsPositiveInfinity(joint.Excess(double.NaN)), "Not a number is outside any range.");
            Assert.IsTrue(joint.IsAtUpper(2f));
            Assert.IsTrue(joint.IsAtLower(-1f));
            Assert.IsFalse(joint.IsAtUpper(1.9f));
        }

        // ---------- velocity ----------

        [Test]
        public void StepToward_MovesAtMostMaxVelocityTimesDeltaTime()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -3f, 3f, maxVelocity: 2f);

            Assert.AreEqual(0.2f, joint.StepToward(0f, 1f, 0.1f), 1e-6f);
            Assert.AreEqual(-0.2f, joint.StepToward(0f, -1f, 0.1f), 1e-6f);
            Assert.AreEqual(0.05f, joint.StepToward(0f, 0.05f, 0.1f), 1e-6f, "A target inside the budget is reached.");
            Assert.AreEqual(0f, joint.StepToward(0f, 1f, 0f), 1e-6f, "No time, no motion.");
        }

        [Test]
        public void StepToward_StaysInsideThePositionLimits()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -3f, 3f, maxVelocity: 100f);

            Assert.AreEqual(3f, joint.StepToward(2.9f, 10f, 1f), 1e-6f);
        }

        [Test]
        public void StepToward_WithoutAVelocityLimit_GoesStraightToTheClampedTarget()
        {
            var joint = new JointLimit("j", JointKind.Revolute, -3f, 3f);

            Assert.AreEqual(2f, joint.StepToward(0f, 2f, 0.001f), 1e-6f);
            Assert.AreEqual(3f, joint.StepToward(0f, 9f, 0.001f), 1e-6f);
        }

        [Test]
        public void StepToward_LimitsAContinuousJointsSpeedToo()
        {
            var joint = new JointLimit("j", JointKind.Continuous, 0f, 0f, maxVelocity: 1f);

            Assert.AreEqual(10.5f, joint.StepToward(10f, 20f, 0.5f), 1e-6f);
        }

        // ---------- from a profile ----------

        [Test]
        public void FromProfile_TakesUrdfLimits_OverridesAndVelocities()
        {
            JointLimitModel model = Profile(
                new JointSpec { name = "a", urdfLower = -1f, urdfUpper = 1f, maxVelocity = 2.5f },
                new JointSpec { name = "b", urdfLower = -1f, urdfUpper = 1f, overrideLimits = true, lower = -0.2f, upper = 0.4f },
                new JointSpec { name = "c", kind = JointKind.Continuous, urdfLower = -1f, urdfUpper = 1f },
                new JointSpec { name = "d", kind = JointKind.Prismatic, urdfLower = 0f, urdfUpper = 0.04f }).BuildLimits();

            Assert.AreEqual(4, model.Joints.Count);
            Assert.IsTrue(model.TryGet("a", out JointLimit a));
            Assert.AreEqual(2.5f, a.MaxVelocity);
            Assert.AreEqual(1f, model.Clamp("a", 3f));
            Assert.AreEqual(0.4f, model.Clamp("b", 3f), "The override replaces the URDF limit.");
            Assert.AreEqual(-0.2f, model.Clamp("b", -3f));
            Assert.AreEqual(3f, model.Clamp("c", 3f), "Continuous.");
            Assert.AreEqual(0.04f, model.Clamp("d", 3f));
            Assert.AreEqual(3f, model.Clamp("not a joint", 3f), "A joint the model does not know is left alone.");
            Assert.IsFalse(model.TryGet("not a joint", out _));
            Assert.IsFalse(model.LimitUserVelocity, "Off unless the profile turns it on.");
        }

        [Test]
        public void FromProfile_AppliesTheMarginPerKind()
        {
            RobotProfile p = Profile(
                new JointSpec { name = "turn", urdfLower = -1f, urdfUpper = 1f },
                new JointSpec { name = "slide", kind = JointKind.Prismatic, urdfLower = 0f, urdfUpper = 0.1f },
                new JointSpec { name = "spin", kind = JointKind.Continuous });
            p.limitMarginRadians = 0.05f;
            p.limitMarginMetres = 0.01f;
            p.limitUserVelocity = true;
            JointLimitModel model = p.BuildLimits();

            Assert.AreEqual(0.95f, model.Clamp("turn", 9f), 1e-6f);
            Assert.AreEqual(-0.95f, model.Clamp("turn", -9f), 1e-6f);
            Assert.AreEqual(0.09f, model.Clamp("slide", 9f), 1e-6f);
            Assert.AreEqual(0.01f, model.Clamp("slide", -9f), 1e-6f);
            Assert.AreEqual(9f, model.Clamp("spin", 9f));
            Assert.IsTrue(model.LimitUserVelocity);
        }

        [Test]
        public void WithoutAProfile_NothingIsClamped()
        {
            Assert.AreSame(JointLimitModel.Unlimited, JointLimitModel.FromProfile(null));
            Assert.AreEqual(42f, JointLimitModel.Unlimited.Clamp("anything", 42f));
            Assert.AreEqual(42f, JointLimitModel.Unlimited.StepToward("anything", 0f, 42f, 0.01f));
        }

        // ---------- checking joint states and trajectories ----------

        private static JointLimitModel UrArm() => new JointLimitModel(new[]
        {
            new JointLimit("shoulder_pan", JointKind.Revolute, -TwoPi, TwoPi),
            new JointLimit("elbow", JointKind.Revolute, -Mathf.PI, Mathf.PI),
            new JointLimit("wrist_3", JointKind.Continuous, 0f, 0f),
            new JointLimit("slide", JointKind.Prismatic, 0f, 0.04f),
        });

        [Test]
        public void AJointState_ListsTheJointsOutsideTheirLimits()
        {
            List<JointLimitViolation> violations = UrArm().Validate(
                new[] { "shoulder_pan", "elbow", "wrist_3", "slide", "unknown" },
                new[] { 6.0, 3.5, 40.0, -0.01, 99.0 },
                JointLimitTolerance.Default);

            Assert.AreEqual(2, violations.Count, "6 rad is inside +-2 pi, a continuous joint has no limits, an unknown joint is skipped.");
            Assert.AreEqual("elbow", violations[0].Joint);
            Assert.AreEqual(3.5f - Mathf.PI, violations[0].Excess, 1e-5f);
            Assert.IsTrue(violations[0].AboveUpper);
            Assert.AreEqual("slide", violations[1].Joint);
            Assert.AreEqual(0.01f, violations[1].Excess, 1e-6f);
            Assert.IsFalse(violations[1].AboveUpper);
            Assert.AreEqual("m", violations[1].Unit);
        }

        [Test]
        public void ATrajectory_ReportsEachJointsWorstPoint()
        {
            var trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "shoulder_pan", "elbow" },
                points = new[]
                {
                    Point(0.0, 0.0),
                    Point(5.0, 3.3),     // elbow out by 0.158
                    Point(6.5, 3.6),     // shoulder out by 0.217, elbow out by 0.458 (its worst)
                    Point(7.0, 3.0),     // shoulder out by 0.717 (its worst)
                    Point(-6.0, -3.0),
                }
            };

            List<JointLimitViolation> violations = UrArm().Validate(trajectory, JointLimitTolerance.Default);

            Assert.AreEqual(2, violations.Count);
            JointLimitViolation shoulder = violations[0], elbow = violations[1];
            Assert.AreEqual("shoulder_pan", shoulder.Joint);
            Assert.AreEqual(3, shoulder.Point);
            Assert.AreEqual(7.0 - TwoPi, shoulder.Excess, 1e-5);
            Assert.AreEqual(TwoPi, shoulder.Bound, 1e-6f);
            Assert.AreEqual(2, shoulder.Count, "Two of its points are outside.");
            Assert.AreEqual("elbow", elbow.Joint);
            Assert.AreEqual(2, elbow.Point);
            Assert.AreEqual(3.6 - Mathf.PI, elbow.Excess, 1e-5);
            Assert.AreEqual(2, elbow.Count);

            Assert.AreEqual("shoulder_pan", JointLimitViolation.Worst(violations).Value.Joint);
            StringAssert.Contains("shoulder_pan goes 0.717 rad past its upper limit", JointLimitViolation.Describe(violations));
            StringAssert.Contains("1 more joint", JointLimitViolation.Describe(violations));
        }

        [Test]
        public void ATrajectoryInsideAPlusMinusTwoPiRange_IsClean()
        {
            var trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "shoulder_pan", "wrist_3" },
                points = new[] { Point(-6.2, -100.0), Point(4.0, 0.0), Point(6.2, 100.0) }
            };

            CollectionAssert.IsEmpty(UrArm().Validate(trajectory, JointLimitTolerance.Default));
        }

        [Test]
        public void TheTolerance_LetsPlannerRoundingPass_PerKind()
        {
            JointLimitModel model = UrArm();
            string[] names = { "elbow", "slide" };

            CollectionAssert.IsEmpty(
                model.Validate(names, new[] { Mathf.PI + 0.0005, 0.04005 }, JointLimitTolerance.Default),
                "Half a milliradian and 50 micrometres are rounding.");
            Assert.AreEqual(2, model.Validate(names, new[] { Mathf.PI + 0.002, 0.0402 }, JointLimitTolerance.Default).Count);
            Assert.AreEqual(1, model.Validate(names, new[] { Mathf.PI + 0.002, 0.0402 }, new JointLimitTolerance(0.01f, 0.0001f)).Count,
                "The angle and length tolerances are separate.");
        }

        [Test]
        public void ANaNInATrajectory_IsAViolation()
        {
            var trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "elbow" },
                points = new[] { new JointTrajectoryPointMsg { positions = new[] { double.NaN } } }
            };

            Assert.AreEqual(1, UrArm().Validate(trajectory, JointLimitTolerance.Default).Count);
        }

        [Test]
        public void EmptyOrMalformedInput_IsClean()
        {
            JointLimitModel model = UrArm();

            CollectionAssert.IsEmpty(model.Validate((JointTrajectoryMsg)null, JointLimitTolerance.Default));
            CollectionAssert.IsEmpty(model.Validate(new JointTrajectoryMsg(), JointLimitTolerance.Default));
            CollectionAssert.IsEmpty(model.Validate(null, null, JointLimitTolerance.Default));
            var ragged = new JointTrajectoryMsg
            {
                joint_names = new[] { "shoulder_pan", "elbow" },
                points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.0 } }, new JointTrajectoryPointMsg() }
            };
            CollectionAssert.IsEmpty(model.Validate(ragged, JointLimitTolerance.Default));
        }

        [Test]
        public void Merge_KeepsOneEntryPerJoint_TheWorse()
        {
            var first = new List<JointLimitViolation>
            {
                new JointLimitViolation("a", JointKind.Revolute, 0, 1, 1.2, 1f, 0.2f, 1),
            };
            var second = new List<JointLimitViolation>
            {
                new JointLimitViolation("a", JointKind.Revolute, 0, 4, 1.5, 1f, 0.5f, 3).InSegment(2),
                new JointLimitViolation("b", JointKind.Revolute, 0, 0, -2.0, -1f, 1f, 1).InSegment(2),
            };

            JointLimitViolation.Merge(first, second);

            Assert.AreEqual(2, first.Count);
            Assert.AreEqual(0.5f, first[0].Excess);
            Assert.AreEqual(2, first[0].Segment);
            Assert.AreEqual(4, first[0].Point);
            Assert.AreEqual(4, first[0].Count);
            Assert.AreEqual("b", first[1].Joint);
        }

        private static JointTrajectoryPointMsg Point(params double[] positions) =>
            new JointTrajectoryPointMsg { positions = positions };
    }
}
