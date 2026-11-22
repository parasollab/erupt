using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Erupt.Plugins.Ar.Tests
{
    /// <summary>
    /// The tracker without a camera: it stays inert, and an injected observation goes through
    /// the same bookkeeping as a detection (which tags are looked for, their sizes, filtering,
    /// ageing, the event, the transform laid on a tag).
    /// </summary>
    public class AprilTagTrackerTests
    {
        private GameObject host;
        private AprilTagTracker tracker;
        private float now;
        private readonly List<GameObject> made = new();

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("tracker");
            tracker = host.AddComponent<AprilTagTracker>();
            now = 100f;
            tracker.clock = () => now;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            foreach (GameObject go in made) Object.DestroyImmediate(go);
            made.Clear();
        }

        private static Pose At(float x, float yaw = 0f) => MarkerMathTests.FlatTag(new Vector3(x, 0.8f, 1f), yaw);

        [UnityTest]
        public IEnumerator WithoutAPassthroughCamera_TheTrackerDoesNothing_AndSaysSo()
        {
            tracker.Track(0, 0.10f);
            for (int i = 0; i < 5; i++) yield return null;   // Update runs; nothing is read, nothing is logged

            Assert.IsFalse(tracker.IsReady);
            Assert.AreEqual("no passthrough camera", tracker.StatusText);
            Assert.IsEmpty(tracker.Tags);
            Assert.IsFalse(tracker.TryGetTag(0, out _));
            Assert.IsFalse(tracker.IsVisible(0, 10f));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ATagIsLookedFor_OnceItIsTracked_WithItsOwnSize()
        {
            Assert.IsFalse(tracker.TryGetTagSize(3, out _));
            Assert.IsFalse(tracker.InjectObservation(3, At(0f)), "A tag that is not looked for is ignored: its size is unknown.");

            Assert.IsTrue(tracker.Track(3, 0.10f));
            Assert.IsTrue(tracker.Track(7, 0.05f));
            Assert.IsTrue(tracker.Track(3, 0.10f), "Asking again with the same size is fine: two robots may share a tag.");

            Assert.IsTrue(tracker.TryGetTagSize(3, out float three));
            Assert.IsTrue(tracker.TryGetTagSize(7, out float seven));
            Assert.AreEqual(0.10f, three);
            Assert.AreEqual(0.05f, seven);

            Assert.IsTrue(tracker.InjectObservation(7, At(0f)));
            Assert.IsTrue(tracker.TryGetTag(7, out TrackedTag tag));
            Assert.AreEqual(7, tag.Id);
            Assert.AreEqual(0.05f, tag.Size);
            Assert.AreEqual(1, tag.DetectionCount);
        }

        [Test]
        public void OneTagWithTwoSizes_IsAnError_AndTheFirstSizeStays()
        {
            Assert.IsTrue(tracker.Track(3, 0.10f));

            LogAssert.Expect(LogType.Error, new Regex("Tag 3 is asked for with a size of 0.05 m but is already tracked with 0.1 m"));
            Assert.IsFalse(tracker.Track(3, 0.05f));
            LogAssert.Expect(LogType.Error, new Regex("Tag 4: a tag size of 0 m is not a size"));
            Assert.IsFalse(tracker.Track(4, 0f));

            tracker.TryGetTagSize(3, out float size);
            Assert.AreEqual(0.10f, size);
            Assert.IsFalse(tracker.TryGetTagSize(4, out _));
        }

        [Test]
        public void TheListedTags_AreReadOnce_AndBadEntriesAreReported()
        {
            var listed = new List<TrackedTagEntry>
            {
                new TrackedTagEntry { id = 1, tagSize = 0.08f },
                new TrackedTagEntry { id = 1, tagSize = 0.05f },
                new TrackedTagEntry { id = 2, tagSize = -1f },
                new TrackedTagEntry { id = 5, tagSize = 0.05f }
            };
            var fresh = new GameObject("listed tracker");
            made.Add(fresh);
            fresh.SetActive(false);
            var other = fresh.AddComponent<AprilTagTracker>();
            typeof(AprilTagTracker).GetField("tags", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(other, listed);

            LogAssert.Expect(LogType.Error, new Regex("id 1 is listed twice"));
            LogAssert.Expect(LogType.Error, new Regex("id 2 has a size of -1 m"));
            fresh.SetActive(true);

            Assert.IsTrue(other.TryGetTagSize(1, out float one));
            Assert.AreEqual(0.08f, one, "The first entry of an id counts.");
            Assert.IsFalse(other.TryGetTagSize(2, out _));
            Assert.IsTrue(other.TryGetTagSize(5, out _));
        }

        [Test]
        public void Observations_AreFiltered_Counted_AndAnnounced()
        {
            tracker.Track(0, 0.10f);
            tracker.PoseFilterCoefficient = 0.5f;
            var heard = new List<TrackedTag>();
            tracker.TagObserved += heard.Add;

            tracker.InjectObservation(0, At(0f));
            now += 0.25f;
            tracker.InjectObservation(0, At(1f));

            Assert.AreEqual(2, heard.Count);
            MarkerMathTests.AssertPose(At(0f), heard[0].Pose);
            MarkerMathTests.AssertPose(At(0.5f), heard[1].Pose);
            Assert.IsTrue(tracker.TryGetTag(0, out TrackedTag tag));
            Assert.AreEqual(2, tag.DetectionCount);
            Assert.AreEqual(100.25f, tag.LastSeenTime, 1e-4f);

            tracker.PoseFilterCoefficient = 0f;
            tracker.InjectObservation(0, At(3f));
            tracker.TryGetTag(0, out tag);
            MarkerMathTests.AssertPose(At(3f), tag.Pose);
        }

        [Test]
        public void ATagIsInView_ForAsLongAsItWasSeenRecently()
        {
            tracker.Track(0, 0.10f);
            tracker.Track(1, 0.10f);
            tracker.InjectObservation(0, At(0f));

            Assert.IsTrue(tracker.IsVisible(0, 2f));
            Assert.IsFalse(tracker.IsVisible(1, 2f), "Never seen.");

            now += 1.9f;
            Assert.IsTrue(tracker.IsVisible(0, 2f));
            now += 0.2f;
            Assert.IsFalse(tracker.IsVisible(0, 2f), "Out of view: the pose is kept, but it is no longer current.");
            Assert.IsTrue(tracker.TryGetTag(0, out _));

            tracker.InjectObservation(0, At(0f));
            Assert.IsTrue(tracker.IsVisible(0, 2f), "Seen again.");
        }

        [Test]
        public void ATagsTarget_IsLaidOnTheTag_InTheMarkerFrame()
        {
            var target = new GameObject("on tag");
            made.Add(target);
            tracker.Track(4, 0.05f, target.transform);
            tracker.PoseFilterCoefficient = 0f;

            tracker.InjectObservation(4, At(2f, 90f));

            Pose marker = MarkerMath.MarkerFrame(At(2f, 90f));
            MarkerMathTests.AssertPose(marker, new Pose(target.transform.position, target.transform.rotation));
            Assert.Less(Vector3.Angle(Vector3.up, target.transform.up), 1e-2f, "It lies flat on the tag.");
        }

        [Test]
        public void SeveralTags_AreTrackedSideBySide()
        {
            tracker.PoseFilterCoefficient = 0f;
            for (int id = 0; id < 4; id++) tracker.Track(id, 0.05f + 0.01f * id);
            for (int id = 0; id < 4; id++) tracker.InjectObservation(id, At(id));

            Assert.AreEqual(4, tracker.Tags.Count);
            for (int id = 0; id < 4; id++)
            {
                Assert.IsTrue(tracker.TryGetTag(id, out TrackedTag tag));
                Assert.AreEqual(0.05f + 0.01f * id, tag.Size, 1e-6f);
                MarkerMathTests.AssertPose(At(id), tag.Pose);
            }
        }
    }
}
