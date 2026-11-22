using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// The refusal pulse: a refusal attributed to a controller is answered with two short
    /// impulses on that controller and nothing on the other; a source without haptics (tracked
    /// hands) gets nothing.
    /// </summary>
    public class RefusalHapticsTests
    {
        private GameObject routerGo, rightGo, leftGo, handGo;
        private InteractionRouter router;
        private FakeSource right, left, hand;

        [SetUp]
        public void SetUp()
        {
            rightGo = Source("right", Capability.Ray | Capability.Haptics, out right);
            leftGo = Source("left", Capability.Ray | Capability.Haptics, out left);
            handGo = Source("right-hand", Capability.Ray | Capability.Pinch, out hand);

            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();

            foreach (GameObject go in new[] { rightGo, leftGo, handGo })
                go.AddComponent<RefusalHaptics>().SetRouter(router);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in new[] { rightGo, leftGo, handGo, routerGo })
                Object.DestroyImmediate(go);
        }

        [UnityTest]
        public IEnumerator ARefusalCausedByAController_IsADoublePulseOnThatController()
        {
            router.ReportRefusal(InteractionRefusal
                .Refuse(RefusalCode.JointLimit, "j1 is at its upper limit.", Vector3.zero)
                .From("right"));

            Assert.AreEqual(1, right.Pulses.Count, "The first pulse is immediate.");
            yield return Until(() => right.Pulses.Count >= 2);

            Assert.AreEqual(2, right.Pulses.Count, "Two pulses, no more.");
            Assert.AreEqual(2, rightGo.GetComponent<RefusalHaptics>().PulsesSent);
            foreach ((float amplitude, float seconds) in right.Pulses)
            {
                Assert.That(amplitude, Is.InRange(0.1f, 1f));
                Assert.That(seconds, Is.InRange(0.01f, 0.2f), "Short.");
            }
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.AreEqual(2, right.Pulses.Count);
            Assert.IsEmpty(left.Pulses, "The other controller stays quiet.");
        }

        [UnityTest]
        public IEnumerator ARefusalCausedByAHand_PulsesNothing()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.OutOfReach, "far", Vector3.zero).From("right-hand"));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.IsEmpty(hand.Pulses, "Tracked hands have no haptics.");
            Assert.IsEmpty(right.Pulses);
            Assert.IsEmpty(left.Pulses);
        }

        [UnityTest]
        public IEnumerator ARefusalNoSourceCaused_PulsesNothing()
        {
            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.PlanOutsideLimits, "plan leaves the limits", Vector3.zero));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.IsEmpty(right.Pulses);
            Assert.IsEmpty(left.Pulses);
        }

        [Test]
        public void ADisabledComponent_StopsListening()
        {
            rightGo.GetComponent<RefusalHaptics>().enabled = false;

            router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "limit", Vector3.zero).From("right"));

            Assert.IsEmpty(right.Pulses);
        }

        [Test]
        public void TheControllerBackend_BringsItsOwnRefusalHaptics()
        {
            var controller = new GameObject("controller");
            try
            {
                var backend = controller.AddComponent<XriControllerBackend>();

                Assert.IsNotNull(controller.GetComponent<RefusalHaptics>(), "The reference rig pulses on refusals without extra setup.");
                Assert.IsTrue(backend.Has(Capability.Haptics));
                Assert.IsInstanceOf<IHapticOutput>(backend);
                Assert.AreEqual("right", backend.SourceId);
                // No haptic action is bound in a test: the impulse goes nowhere and nothing throws.
                Assert.DoesNotThrow(() => backend.SendHapticImpulse(0.5f, 0.05f));
            }
            finally
            {
                Object.DestroyImmediate(controller);
            }
        }

        private static IEnumerator Until(System.Func<bool> condition)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        private static GameObject Source(string id, Capability capabilities, out FakeSource source)
        {
            var go = new GameObject(id);
            source = go.AddComponent<FakeSource>();
            source.Id = id;
            source.Offered = capabilities;
            return go;
        }

        /// <summary>An input source that records the impulses it is sent.</summary>
        private sealed class FakeSource : InteractionSourceBehaviour, IHapticOutput
        {
            public string Id;
            public Capability Offered;
            public readonly List<(float amplitude, float seconds)> Pulses = new List<(float, float)>();

            public override Modality Modality => Modality.Controller;
            public override Capability Capabilities => Offered;
            public override InteractionSample Current =>
                new InteractionSample(Modality.Controller, 1f, 0.0, Pose.identity, Id);

            public void SendHapticImpulse(float amplitude, float seconds) => Pulses.Add((amplitude, seconds));
        }
    }
}
