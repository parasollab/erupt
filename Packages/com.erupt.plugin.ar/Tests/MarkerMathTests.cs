using System;
using NUnit.Framework;
using UnityEngine;

namespace Erupt.Plugins.Ar.Tests
{
    /// <summary>The arithmetic of marker tracking and placement: no camera, no detector, no scene.</summary>
    public class MarkerMathTests
    {
        // A printed tag lying on a table at `position`, its top edge towards `heading` (a
        // horizontal direction): tag x to the right, y to the top edge, z into the tag (down).
        internal static Pose FlatTag(Vector3 position, float headingDegrees = 0f)
        {
            Quaternion yaw = Quaternion.Euler(0f, headingDegrees, 0f);
            return new Pose(position, Quaternion.LookRotation(Vector3.down, yaw * Vector3.forward));
        }

        internal static void AssertPose(Pose expected, Pose actual, float distance = 1e-4f, float angle = 0.05f)
        {
            Assert.Less(Vector3.Distance(expected.position, actual.position), distance, $"position: expected {expected.position:F4}, was {actual.position:F4}");
            Assert.Less(Quaternion.Angle(expected.rotation, actual.rotation), angle,
                $"rotation: expected {expected.rotation.eulerAngles}, was {actual.rotation.eulerAngles}");
        }

        // ---- frames ----

        [Test]
        public void AFlatTagsFrame_HasXRight_YToTheTopEdge_ZIntoTheTable()
        {
            Pose tag = FlatTag(new Vector3(1f, 0.8f, 2f));
            Assert.Less(Vector3.Distance(Vector3.right, tag.rotation * Vector3.right), 1e-5f);
            Assert.Less(Vector3.Distance(Vector3.forward, tag.rotation * Vector3.up), 1e-5f);
            Assert.Less(Vector3.Distance(Vector3.down, tag.rotation * Vector3.forward), 1e-5f);
        }

        [Test]
        public void TheMarkerFrame_OfAFlatTag_ReadsLikeAnObjectOnTheTable()
        {
            Pose marker = MarkerMath.MarkerFrame(FlatTag(new Vector3(1f, 0.8f, 2f), 90f));

            Assert.AreEqual(new Vector3(1f, 0.8f, 2f), marker.position);
            Assert.Less(Vector3.Distance(Vector3.up, marker.rotation * Vector3.up), 1e-5f, "y is the tag's normal: up.");
            Assert.Less(Vector3.Distance(Vector3.right, marker.rotation * Vector3.forward), 1e-5f, "z is the tag's top edge, which points along world x here.");
            Assert.Less(Vector3.Distance(Vector3.back, marker.rotation * Vector3.right), 1e-5f, "x is the tag's right edge.");
        }

        [Test]
        public void ObjectOnTag_WithTheDefaultRotation_IsTheMarkerFrame_AndWithNone_IsTheTagFrame()
        {
            Pose tag = FlatTag(new Vector3(0.3f, 1f, -2f), 37f);
            AssertPose(MarkerMath.MarkerFrame(tag), MarkerMath.ObjectOnTag(tag, new Vector3(-90f, 0f, 0f)));
            AssertPose(tag, MarkerMath.ObjectOnTag(tag, Vector3.zero));
        }

        // ---- filtering ----

        [Test]
        public void Filter_Zero_TakesTheObservation_One_KeepsThePrevious_Half_IsHalfway()
        {
            var previous = new Pose(new Vector3(0f, 0f, 0f), Quaternion.identity);
            var seen = new Pose(new Vector3(1f, 2f, -4f), Quaternion.Euler(0f, 90f, 0f));

            AssertPose(seen, MarkerMath.Filter(previous, seen, 0f));
            AssertPose(previous, MarkerMath.Filter(previous, seen, 1f));
            AssertPose(new Pose(new Vector3(0.5f, 1f, -2f), Quaternion.Euler(0f, 45f, 0f)), MarkerMath.Filter(previous, seen, 0.5f));
            // Out-of-range coefficients are clamped, not extrapolated.
            AssertPose(seen, MarkerMath.Filter(previous, seen, -3f));
            AssertPose(previous, MarkerMath.Filter(previous, seen, 7f));
        }

        [Test]
        public void Filter_TurnsTheShortWay()
        {
            var previous = new Pose(Vector3.zero, Quaternion.Euler(0f, 170f, 0f));
            var seen = new Pose(Vector3.zero, Quaternion.Euler(0f, -170f, 0f));
            // 170 and -170 are 20 degrees apart, through 180.
            Assert.Less(Quaternion.Angle(Quaternion.Euler(0f, 180f, 0f), MarkerMath.Filter(previous, seen, 0.5f).rotation), 0.05f);
        }

        [Test]
        public void RepeatedObservationsOfAStillTag_ConvergeOnIt()
        {
            var truth = new Pose(new Vector3(1f, 1f, 1f), Quaternion.Euler(10f, 20f, 30f));
            Pose filtered = new Pose(Vector3.zero, Quaternion.identity);
            for (int i = 0; i < 30; i++) filtered = MarkerMath.Filter(filtered, truth, 0.5f);
            AssertPose(truth, filtered);
        }

        // ---- one detection pass for tags of any size ----

        [Test]
        public void RescaleToTagSize_ScalesTheTranslation_ByRealOverAssumed()
        {
            var seen = new Vector3(0.2f, -0.1f, 1.5f);
            Assert.AreEqual(seen * 0.5f, MarkerMath.RescaleToTagSize(seen, 0.05f, 0.10f));
            Assert.AreEqual(seen, MarkerMath.RescaleToTagSize(seen, 0.10f, 0.10f));
            Assert.AreEqual(seen * 3f, MarkerMath.RescaleToTagSize(seen, 0.30f, 0.10f));
            Assert.Throws<ArgumentOutOfRangeException>(() => MarkerMath.RescaleToTagSize(seen, 0.05f, 0f));
        }

        [Test]
        public void ARescaledTag_ProjectsToTheSamePixels_AsTheAssumedOneDid()
        {
            // The claim the single detection pass rests on: corners at ± real / 2 around the
            // rescaled position land where corners at ± assumed / 2 around the estimated one did.
            const float assumed = 0.10f, real = 0.05f, fx = 430f, fy = 432f, cx = 318f, cy = 245f;
            var position = new Vector3(0.12f, -0.07f, 0.9f);
            Quaternion rotation = Quaternion.Euler(25f, -40f, 10f);
            Vector3 rescaled = MarkerMath.RescaleToTagSize(position, real, assumed);

            foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
            {
                Vector3 before = position + rotation * new Vector3(corner.x, corner.y, 0f) * (assumed / 2f);
                Vector3 after = rescaled + rotation * new Vector3(corner.x, corner.y, 0f) * (real / 2f);
                Assert.AreEqual(fx * before.x / before.z + cx, fx * after.x / after.z + cx, 1e-2f);
                Assert.AreEqual(fy * before.y / before.z + cy, fy * after.y / after.z + cy, 1e-2f);
            }
        }

        [Test]
        public void ToWorld_LiftsACameraLocalPose_WithTheCameraPoseTheImageWasTakenAt()
        {
            var camera = new Pose(new Vector3(1f, 1.6f, 0f), Quaternion.Euler(0f, 90f, 0f));
            Pose world = MarkerMath.ToWorld(camera, new Vector3(0f, 0f, 2f), Quaternion.Euler(0f, 180f, 0f));
            // Two metres ahead of a camera that looks along world x.
            AssertPose(new Pose(new Vector3(3f, 1.6f, 0f), Quaternion.Euler(0f, 270f, 0f)), world);
        }

        // ---- image rows ----

        [Test]
        public void FlipRows_ReversesTheRowOrder_AndKeepsEachRow()
        {
            int[] source = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };   // 4 wide, 3 high
            var flipped = new int[12];
            MarkerMath.FlipRows(source, flipped, 4, 3);
            CollectionAssert.AreEqual(new[] { 9, 10, 11, 12, 5, 6, 7, 8, 1, 2, 3, 4 }, flipped);

            var back = new int[12];
            MarkerMath.FlipRows(flipped, back, 4, 3);
            CollectionAssert.AreEqual(source, back, "Flipping twice is the image again.");
        }

        [Test]
        public void FlipRows_RefusesBuffersThatDoNotFit()
        {
            var four = new Color32[4];
            Assert.Throws<ArgumentException>(() => MarkerMath.FlipRows(four, new Color32[4], 4, 3));
            Assert.Throws<ArgumentException>(() => MarkerMath.FlipRows(four, four, 2, 2), "Not in place.");
            Assert.Throws<ArgumentNullException>(() => MarkerMath.FlipRows(null, four, 2, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => MarkerMath.FlipRows(four, new Color32[4], 0, 2));
        }

        // ---- intrinsics ----

        [Test]
        public void Intrinsics_OfAnImageAtHalfTheSensorResolution_AreHalved()
        {
            Assert.IsTrue(MarkerMath.TryScaleIntrinsics(new Vector2(1280, 960), new Vector2(870, 872), new Vector2(644, 470),
                640, 480, out float fx, out float fy, out float cx, out float cy));
            Assert.AreEqual(435f, fx, 1e-3f);
            Assert.AreEqual(436f, fy, 1e-3f);
            Assert.AreEqual(322f, cx, 1e-3f);
            Assert.AreEqual(235f, cy, 1e-3f);
        }

        [Test]
        public void Intrinsics_OfAnImageWithAnotherAspectRatio_AreThoseOfTheCentredCrop()
        {
            // A 1280x960 sensor delivering 1280x720: the image is the sensor's middle 720 rows, unscaled.
            Assert.IsTrue(MarkerMath.TryScaleIntrinsics(new Vector2(1280, 960), new Vector2(870, 872), new Vector2(644, 470),
                1280, 720, out float fx, out float fy, out float cx, out float cy));
            Assert.AreEqual(870f, fx, 1e-3f);
            Assert.AreEqual(872f, fy, 1e-3f);
            Assert.AreEqual(644f, cx, 1e-3f);
            Assert.AreEqual(470f - 120f, cy, 1e-3f, "120 rows are cropped above the image.");
        }

        [Test]
        public void Intrinsics_OfNothing_AreRefused()
        {
            Assert.IsFalse(MarkerMath.TryScaleIntrinsics(Vector2.zero, Vector2.one, Vector2.one, 640, 480, out _, out _, out _, out _));
            Assert.IsFalse(MarkerMath.TryScaleIntrinsics(new Vector2(1280, 960), Vector2.one, Vector2.one, 0, 480, out _, out _, out _, out _));
        }

        // ---- where the base goes ----

        [Test]
        public void WithNoOffset_TheBaseStandsOnTheTag_FacingItsTopEdge()
        {
            Pose tag = FlatTag(new Vector3(1f, 0.8f, 2f), 90f);
            Pose proposed = MarkerMath.ProposeBase(tag, new Pose(Vector3.zero, Quaternion.identity), level: true);
            AssertPose(new Pose(new Vector3(1f, 0.8f, 2f), Quaternion.Euler(0f, 90f, 0f)), proposed);
        }

        [Test]
        public void TheOffset_IsAppliedInTheMarkerFrame_RightUpAndTowardsTheTopEdge()
        {
            // The tag's top edge points along world x; the base is 30 cm to the tag's right, 5 cm
            // up and 50 cm towards its top edge, turned a quarter turn clockwise.
            Pose tag = FlatTag(new Vector3(1f, 0.8f, 2f), 90f);
            var offset = new Pose(new Vector3(0.3f, 0.05f, 0.5f), Quaternion.Euler(0f, 90f, 0f));

            Pose proposed = MarkerMath.ProposeBase(tag, offset, level: true);

            // Right of a tag that looks along +x is world -z.
            AssertPose(new Pose(new Vector3(1.5f, 0.85f, 1.7f), Quaternion.Euler(0f, 180f, 0f)), proposed);
        }

        [Test]
        public void Level_RemovesTheTiltOfATagThatLiesFlat_AndKeepsItsHeading()
        {
            // The detector reports the flat tag four degrees off level.
            Pose flat = FlatTag(new Vector3(0f, 0.8f, 0f), 30f);
            var tilted = new Pose(flat.position, Quaternion.AngleAxis(4f, Vector3.right) * flat.rotation);
            var offset = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);

            Pose levelled = MarkerMath.ProposeBase(tilted, offset, level: true);
            Pose asSeen = MarkerMath.ProposeBase(tilted, offset, level: false);

            Assert.Less(Vector3.Angle(Vector3.up, levelled.rotation * Vector3.up), 1e-3f, "The robot stands upright.");
            Assert.AreEqual(0.8f, levelled.position.y, 1e-5f, "A metre away it is still at the tag's height.");
            Assert.Less(Mathf.Abs(Mathf.DeltaAngle(30f, levelled.rotation.eulerAngles.y)), 0.5f, "The heading is the tag's.");
            Assert.Greater(Vector3.Angle(Vector3.up, asSeen.rotation * Vector3.up), 3f, "Unlevelled, the tilt is taken as it is.");
            Assert.Greater(Mathf.Abs(asSeen.position.y - 0.8f), 0.03f, "...and swings the offset off the table.");
        }

        [Test]
        public void Level_OfATagStandingUpright_TakesTheHeadingFromItsRightEdge()
        {
            // On a wall facing world -z: top edge up, normal towards the viewer (-z).
            var onWall = new Pose(Vector3.zero, Quaternion.LookRotation(Vector3.forward, Vector3.up));
            Pose level = MarkerMath.Level(MarkerMath.MarkerFrame(onWall));
            Assert.Less(Vector3.Angle(Vector3.up, level.rotation * Vector3.up), 1e-3f);
            Assert.Less(Vector3.Angle(Vector3.right, level.rotation * Vector3.right), 1e-3f, "Its right edge stays its right.");
        }

        [Test]
        public void WithoutLevelling_ATagOnAWall_PlacesTheBaseOnTheWall()
        {
            var onWall = new Pose(new Vector3(0f, 1.2f, 3f), Quaternion.LookRotation(Vector3.forward, Vector3.up));
            Pose proposed = MarkerMath.ProposeBase(onWall, new Pose(new Vector3(0f, 0.1f, 0f), Quaternion.identity), level: false);
            // The marker frame's y is the tag's normal, which points out of the wall (world -z).
            AssertPose(new Pose(new Vector3(0f, 1.2f, 2.9f), MarkerMath.MarkerFrame(onWall).rotation), proposed);
        }

        [Test]
        public void BaseInMarker_IsTheOffsetThatPutsTheBaseBackWhereItWas()
        {
            Pose tag = FlatTag(new Vector3(1f, 0.8f, 2f), 200f);
            var basePose = new Pose(new Vector3(1.4f, 0.8f, 1.1f), Quaternion.Euler(0f, 75f, 0f));

            Pose offset = MarkerMath.BaseInMarker(tag, basePose);

            AssertPose(basePose, MarkerMath.ProposeBase(tag, offset, level: false));
            AssertPose(basePose, MarkerMath.ProposeBase(tag, offset, level: true), 1e-4f, 0.05f);
        }

        [Test]
        public void Agree_IsWithinBothTheDistanceAndTheAngle()
        {
            var a = new Pose(Vector3.zero, Quaternion.identity);
            Assert.IsTrue(MarkerMath.Agree(a, new Pose(new Vector3(0.01f, 0f, 0f), Quaternion.Euler(0f, 1f, 0f)), 0.02f, 2f));
            Assert.IsFalse(MarkerMath.Agree(a, new Pose(new Vector3(0.03f, 0f, 0f), Quaternion.identity), 0.02f, 2f));
            Assert.IsFalse(MarkerMath.Agree(a, new Pose(Vector3.zero, Quaternion.Euler(0f, 3f, 0f)), 0.02f, 2f));
        }
    }
}
