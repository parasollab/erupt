using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The joint-limit tint: a joint-limit refusal raised on the router tints the link it is
    /// about, on the robot that link belongs to, for a moment; the material underneath is left
    /// as it was and no material is created.
    /// </summary>
    public class JointLimitIndicatorTests
    {
        private GameObject routerGo, leftGo, rightGo;
        private InteractionRouter router;
        private JointLimitIndicator left, right;
        private Renderer leftLink, leftForearm, rightLink;

        [SetUp]
        public void SetUp()
        {
            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();

            leftGo = Robot("left", out leftLink, out leftForearm);
            rightGo = Robot("right", out rightLink, out _);
            left = leftGo.AddComponent<JointLimitIndicator>();
            right = rightGo.AddComponent<JointLimitIndicator>();
            left.SetRouter(router);
            right.SetRouter(router);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(leftGo);
            Object.DestroyImmediate(rightGo);
            Object.DestroyImmediate(routerGo);
        }

        [Test]
        public void AJointLimitRefusal_TintsTheLink_OnItsOwnRobotOnly()
        {
            Material before = leftLink.sharedMaterial;

            router.ReportRefusal(Limit(leftLink));

            Assert.IsTrue(left.IsIndicating(leftLink.transform.parent));
            AssertSameColor(left.LimitColor, TintOf(leftLink));
            Assert.IsFalse(leftForearm.HasPropertyBlock(), "The link further down the arm is a different joint's.");
            Assert.IsFalse(rightLink.HasPropertyBlock(), "The other robot's link is untouched.");
            Assert.AreEqual(0, right.ActiveCount);
            Assert.AreSame(before, leftLink.sharedMaterial, "The tint is laid over the material: no material is created or swapped.");
        }

        [Test]
        public void EachRobot_ShowsItsOwnJoint()
        {
            router.ReportRefusal(Limit(rightLink));

            Assert.IsTrue(right.IsIndicating(rightLink.transform.parent));
            Assert.AreEqual(0, left.ActiveCount);
            Assert.IsFalse(leftLink.HasPropertyBlock());
        }

        [Test]
        public void OtherRefusals_AndRefusalsWithoutASubject_TintNothing()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.OutOfReach, "far", Vector3.zero, leftLink.transform.parent));
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "nowhere", Vector3.zero));
            router.ReportRefusal(InteractionRefusal.Refuse("plain", Vector3.zero));

            Assert.AreEqual(0, left.ActiveCount);
            Assert.IsFalse(leftLink.HasPropertyBlock());
        }

        [Test]
        public void AMismatchAndAPlanOutsideLimits_AlsoPointAtTheJoint()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.LimitMismatch, "robot disagrees", Vector3.zero, leftLink.transform.parent));
            Assert.IsTrue(left.IsIndicating(leftLink.transform.parent));

            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.PlanOutsideLimits, "plan leaves", Vector3.zero, leftForearm.transform.parent));
            Assert.IsTrue(left.IsIndicating(leftForearm.transform.parent));
            Assert.AreEqual(2, left.ActiveCount);
        }

        [UnityTest]
        public IEnumerator TheTint_LastsWhileRefusalsKeepComing_AndClearsAfterwards()
        {
            left.HoldSeconds = 0.3f;
            router.RefusalCoalesceSeconds = 0.05f;
            var underneath = new MaterialPropertyBlock();
            underneath.SetFloat("_Marker", 7f);
            leftLink.SetPropertyBlock(underneath);

            // Pushing against the limit: a refusal on every frame for longer than the hold.
            float until = Time.realtimeSinceStartup + 0.6f;
            while (Time.realtimeSinceStartup < until)
            {
                router.ReportRefusal(Limit(leftLink));
                yield return null;
                Assert.IsTrue(left.IsIndicating(leftLink.transform.parent), "Still pinned: still tinted.");
            }

            float deadline = Time.realtimeSinceStartup + 3f;
            while (left.ActiveCount > 0 && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.AreEqual(0, left.ActiveCount);
            var after = new MaterialPropertyBlock();
            leftLink.GetPropertyBlock(after);
            Assert.AreEqual(7f, after.GetFloat("_Marker"), "What the renderer carried before the tint is back.");
            Assert.IsFalse(after.HasColor(MaterialColor.BaseColorId), "The tint colour is gone.");
        }

        [Test]
        public void DisablingTheIndicator_RemovesTheTint()
        {
            router.ReportRefusal(Limit(leftLink));
            Assert.IsTrue(leftLink.HasPropertyBlock());

            left.enabled = false;

            Assert.IsFalse(leftLink.HasPropertyBlock());
            router.ReportRefusal(Limit(leftForearm));
            Assert.IsFalse(leftForearm.HasPropertyBlock(), "A disabled indicator does not listen.");
        }

        [Test]
        public void TheRouterBinding_GivesItsRobotAnIndicator()
        {
            var robots = FakeRobots.Registry(new FakeRosBus());
            var robotGo = new GameObject("arm");
            try
            {
                FakeRobots.Attach(robots, robotGo, "arm", "", null, new FakeRobotModel("j1"));
                var child = new GameObject("interaction");
                child.transform.SetParent(robotGo.transform, false);
                child.SetActive(false);
                child.AddComponent<RobotInteractionController>();
                var binding = child.AddComponent<RobotInteractionRouterBinding>();
                child.SetActive(true);

                Assert.IsTrue(binding.enabled);
                Assert.IsNotNull(robotGo.GetComponent<JointLimitIndicator>(), "The robot shows its joint limits without extra setup.");
            }
            finally
            {
                FakeRobots.DestroyAll(robots);
                if (robotGo != null) Object.DestroyImmediate(robotGo);
            }
        }

        private static InteractionRefusal Limit(Renderer linkVisual) =>
            InteractionRefusal.Refuse(RefusalCode.JointLimit, "at its limit", Vector3.zero, linkVisual.transform.parent);

        private static void AssertSameColor(Color expected, Color actual)
        {
            for (int channel = 0; channel < 4; channel++)
                Assert.AreEqual(expected[channel], actual[channel], 1e-3f, $"channel {channel}");
        }

        private static Color TintOf(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor(MaterialColor.BaseColorId);
        }

        // A robot root with two nested links, each an articulation body with one visual under it.
        private static GameObject Robot(string name, out Renderer linkVisual, out Renderer forearmVisual)
        {
            var root = new GameObject(name);
            root.AddComponent<ArticulationBody>();
            linkVisual = Link(root.transform, "link1", out Transform link1);
            forearmVisual = Link(link1, "link2", out _);
            return root;
        }

        private static Renderer Link(Transform parent, string name, out Transform link)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<ArticulationBody>().jointType = ArticulationJointType.RevoluteJoint;
            link = go.transform;

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = name + " visual";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(link, false);
            return visual.GetComponent<Renderer>();
        }
    }
}
