using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    /// <summary>
    /// Router policy tests. These are the behavior-preservation checks that do not need a
    /// headset: deadzone, precision scaling, filter parameters, refusal plumbing, and the
    /// guarantee that samples carry modality and confidence.
    /// </summary>
    public class InteractionRouterTests
    {
        private GameObject host;
        private GameObject target;
        private InteractionRouter router;
        private TestInteractionSource source;

        [SetUp]
        public void SetUp()
        {
            InteractionSampleBus.Reset();
            host = new GameObject("router-host");
            source = host.AddComponent<TestInteractionSource>();
            router = host.AddComponent<InteractionRouter>();
            router.Register(source);
        }

        [TearDown]
        public void TearDown()
        {
            InteractionSampleBus.Reset();
            if (target != null) Object.DestroyImmediate(target);
            Object.DestroyImmediate(host);
        }

        // The deleted Quest3ControllerRayInteractor applied a 0.18 deadzone inline. The
        // controller profile must reproduce it, or joint jog gains phantom drift.
        [Test]
        public void ControllerAxis_BelowDeadzone_IsZeroed()
        {
            Vector2 received = Vector2.one;
            router.Axis += i => received = i.Axis;

            source.EmitAxis(new Vector2(0f, 0.17f), Modality.Controller);

            Assert.AreEqual(0f, received.y, 1e-5f);
        }

        [Test]
        public void ControllerAxis_AboveDeadzone_PassesThroughUnscaled()
        {
            Vector2 received = Vector2.zero;
            router.Axis += i => received = i.Axis;

            source.EmitAxis(new Vector2(0f, 0.5f), Modality.Controller);

            // Controller precisionScale is 1, so the value must survive untouched.
            Assert.AreEqual(0.5f, received.y, 1e-5f);
        }

        [Test]
        public void EverySample_CarriesModalityAndConfidence()
        {
            var samples = new List<InteractionSample>();
            InteractionSampleBus.Sample += samples.Add;

            source.EmitSelect(Modality.Hand, confidence: 0.4f);

            Assert.AreEqual(1, samples.Count);
            Assert.AreEqual(Modality.Hand, samples[0].Modality);
            Assert.AreEqual(0.4f, samples[0].Confidence, 1e-5f);
            Assert.Greater(samples[0].Timestamp, 0.0);
        }

        [Test]
        public void Confidence_IsClampedToUnitRange()
        {
            var sample = new InteractionSample(Modality.Hand, 4.2f, 1.0, Pose.identity, "x");
            Assert.AreEqual(1f, sample.Confidence, 1e-5f);
        }

        // Discrete presses must resolve from the live pose, or the drawn ray and the
        // selected target disagree while the controller is moving.
        [Test]
        public void Select_IsNotSmoothed()
        {
            Vector3 last = Vector3.zero;
            router.Select += i => last = i.Sample.Pose.position;

            source.EmitAt(IntentKind.Select, new Vector3(1f, 0f, 0f), Modality.Controller, 1.0);
            source.EmitAt(IntentKind.Select, new Vector3(9f, 0f, 0f), Modality.Controller, 1.011);

            Assert.AreEqual(9f, last.x, 1e-4f);
        }

        [Test]
        public void Select_HittingUi_IsConsumedInsteadOfDispatchedAsAnEmptyMiss()
        {
            const int isolatedLayer = 29;
            SetLayerMask("raycastLayers", 1 << isolatedLayer);
            SetLayerMask("uiLayers", 1 << isolatedLayer);

            target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.layer = isolatedLayer;
            target.transform.position = new Vector3(0f, 0f, 2f);
            Physics.SyncTransforms();

            int selects = 0;
            router.Select += _ => selects++;

            source.EmitAt(IntentKind.Select, Vector3.zero, Modality.Controller, 1.0);

            Assert.AreEqual(0, selects,
                "A trigger press consumed by wrist UI must not become a world miss that clears selection.");
        }

        [Test]
        public void Select_MissingEverything_IsStillDispatchedForEmptySpaceDeselection()
        {
            const int isolatedLayer = 29;
            SetLayerMask("raycastLayers", 1 << isolatedLayer);
            SetLayerMask("uiLayers", 1 << isolatedLayer);

            int selects = 0;
            GameObject selectedTarget = null;
            router.Select += intent =>
            {
                selects++;
                selectedTarget = intent.Target;
            };

            source.EmitAt(IntentKind.Select, Vector3.zero, Modality.Controller, 1.0);

            Assert.AreEqual(1, selects);
            Assert.IsNull(selectedTarget,
                "A genuine empty-space press must remain distinguishable from a UI-consumed press.");
        }

        [Test]
        public void Refusal_IsBroadcastWithReason()
        {
            InteractionRefusal seen = InteractionRefusal.None;
            router.Refused += r => seen = r;

            router.ReportRefusal(InteractionRefusal.Refuse("joint at limit", new Vector3(1f, 2f, 3f)));

            Assert.IsTrue(seen.IsRefused);
            Assert.AreEqual("joint at limit", seen.Reason);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), seen.WorldPoint);
        }

        [Test]
        public void Refusal_None_IsNotBroadcast()
        {
            int count = 0;
            router.Refused += _ => count++;

            router.ReportRefusal(InteractionRefusal.None);

            Assert.AreEqual(0, count);
        }

        [Test]
        public void Refusal_CarriesItsCodeSubjectAndSource()
        {
            InteractionRefusal seen = InteractionRefusal.None;
            router.Refused += r => seen = r;
            target = new GameObject("link");

            bool raised = router.ReportRefusal(InteractionRefusal
                .Refuse(RefusalCode.JointLimit, "j1 is at its upper limit.", Vector3.one, target.transform)
                .From("left"));

            Assert.IsTrue(raised);
            Assert.AreEqual(RefusalCode.JointLimit, seen.Code);
            Assert.AreSame(target.transform, seen.Subject);
            Assert.AreEqual("left", seen.SourceId);

            Assert.AreEqual(RefusalCode.Unspecified, InteractionRefusal.Refuse("plain", Vector3.zero).Code);
            Assert.AreEqual(RefusalCode.None, InteractionRefusal.None.Code);
            Assert.IsNull(InteractionRefusal.Refuse("plain", Vector3.zero).Subject);
        }

        // A drag against a joint limit reports the same refusal on every frame.
        [Test]
        public void Refusals_WithTheSameCodeAndSubject_AreCoalesced()
        {
            int count = 0, onBus = 0;
            router.Refused += _ => count++;
            InteractionSampleBus.Refusal += _ => onBus++;
            target = new GameObject("link");

            for (int frame = 0; frame < 30; frame++)
                router.ReportRefusal(InteractionRefusal.Refuse(
                    RefusalCode.OutOfReach, $"Target is out of reach by {frame} cm.", Vector3.zero, target.transform));

            Assert.AreEqual(1, count, "Thirty frames of the same refusal are one refusal, whatever the wording.");
            Assert.AreEqual(1, onBus);
        }

        [Test]
        public void Refusals_WithAnotherCodeOrSubject_AreNotCoalesced()
        {
            var seen = new List<InteractionRefusal>();
            router.Refused += seen.Add;
            target = new GameObject("link");
            var other = new GameObject("other link");
            try
            {
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "a", Vector3.zero, target.transform));
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "b", Vector3.zero, other.transform));
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.OutOfReach, "c", Vector3.zero, target.transform));
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "d", Vector3.zero));
                router.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.JointLimit, "a again", Vector3.zero, target.transform));

                CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, seen.ConvertAll(r => r.Reason));
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Refusals_WithoutACode_AreToldApartByTheirText()
        {
            int count = 0;
            router.Refused += _ => count++;

            router.ReportRefusal(InteractionRefusal.Refuse("Nothing is selected.", Vector3.zero));
            router.ReportRefusal(InteractionRefusal.Refuse("MoveIt is not connected.", Vector3.zero));
            router.ReportRefusal(InteractionRefusal.Refuse("Nothing is selected.", Vector3.zero));

            Assert.AreEqual(2, count);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator ARefusal_IsRaisedAgain_OnceTheWindowHasPassed()
        {
            int count = 0;
            router.Refused += _ => count++;
            router.RefusalCoalesceSeconds = 0.1f;
            InteractionRefusal refusal = InteractionRefusal.Refuse(RefusalCode.JointLimit, "j1 is at its upper limit.", Vector3.zero);

            Assert.IsTrue(router.ReportRefusal(refusal));
            Assert.IsFalse(router.ReportRefusal(refusal), "Inside the window.");
            yield return new WaitForSecondsRealtime(0.25f);
            Assert.IsTrue(router.ReportRefusal(refusal), "The joint is still at its limit half a second later: say so again.");
            Assert.AreEqual(2, count);

            router.RefusalCoalesceSeconds = 0f;
            router.ReportRefusal(refusal);
            router.ReportRefusal(refusal);
            Assert.AreEqual(4, count, "A window of zero turns coalescing off.");
        }

        [Test]
        public void AnySourceHas_ReportsCapabilityNotPlatform()
        {
            Assert.IsTrue(router.AnySourceHas(Capability.Ray));
            Assert.IsFalse(router.AnySourceHas(Capability.Gaze));
        }

        // Guidelines Part 3: filter behavior is identical across devices; only parameters
        // vary. A steady pose must converge regardless of modality.
        [Test]
        public void Filter_ConvergesOnSteadyInput()
        {
            var target = new Vector3(1f, 0f, 0f);
            Vector3 last = Vector3.zero;
            router.Drag += i => last = i.Sample.Pose.position;

            for (int i = 0; i < 200; i++)
                source.EmitAt(IntentKind.Drag, target, Modality.Controller, timestamp: 1.0 + i * 0.011);

            Assert.AreEqual(target.x, last.x, 0.01f);
        }

        [Test]
        public void Filter_FirstSampleIsNotSmoothed()
        {
            var target = new Vector3(5f, 0f, 0f);
            Vector3 last = Vector3.zero;
            router.Drag += i => last = i.Sample.Pose.position;

            source.EmitAt(IntentKind.Drag, target, Modality.Controller, timestamp: 1.0);

            Assert.AreEqual(target.x, last.x, 1e-4f);
        }

        private void SetLayerMask(string fieldName, int value)
        {
            FieldInfo field = typeof(InteractionRouter).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"InteractionRouter.{fieldName} was not found.");
            field.SetValue(router, (LayerMask)value);
        }

    }
}
