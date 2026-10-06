using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    public class PinchTrackerTests
    {
        private const float Open = 0.08f;
        private const float Closed = 0.005f;

        private PinchTracker pinch;

        [SetUp]
        public void SetUp()
        {
            pinch = new PinchTracker
            {
                PressDistance = 0.02f,
                ReleaseDistance = 0.035f,
                DragStartDistance = 0.02f,
                DragStartSeconds = 0.3f
            };
        }

        [Test]
        public void OpenHand_EmitsNothing()
        {
            Assert.AreEqual(PinchEvents.None, pinch.Update(Open, Vector3.zero, 0.0));
            Assert.IsFalse(pinch.IsPinching);
        }

        [Test]
        public void Closing_EmitsDownOnce()
        {
            Assert.AreEqual(PinchEvents.Down, pinch.Update(Closed, Vector3.zero, 0.0));
            Assert.AreEqual(PinchEvents.None, pinch.Update(Closed, Vector3.zero, 0.01));
            Assert.IsTrue(pinch.IsPinching);
        }

        [Test]
        public void QuickPinch_IsAPressWithoutADrag()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);
            pinch.Update(Closed, new Vector3(0.005f, 0f, 0f), 0.1);

            Assert.AreEqual(PinchEvents.Up, pinch.Update(Open, Vector3.zero, 0.2));
            Assert.IsFalse(pinch.IsPinching);
        }

        [Test]
        public void GapBetweenThresholds_DoesNotChatter()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);

            // Wider than the press distance but inside the release distance: still held.
            Assert.AreEqual(PinchEvents.None, pinch.Update(0.03f, Vector3.zero, 0.01));
            Assert.IsTrue(pinch.IsPinching);

            // And an open hand at that same gap does not start a pinch.
            pinch.Release();
            Assert.AreEqual(PinchEvents.None, pinch.Update(0.03f, Vector3.zero, 0.02));
        }

        [Test]
        public void MovingWhilePinched_BeginsDragThenDrags()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);

            PinchEvents begin = pinch.Update(Closed, new Vector3(0f, 0.03f, 0f), 0.05);
            Assert.AreEqual(PinchEvents.BeginDrag | PinchEvents.Drag, begin);

            Assert.AreEqual(PinchEvents.Drag, pinch.Update(Closed, new Vector3(0f, 0.04f, 0f), 0.06));
            Assert.IsTrue(pinch.IsDragging);
        }

        [Test]
        public void HoldingStill_BeginsDragAfterTheHoldTime()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);

            Assert.AreEqual(PinchEvents.None, pinch.Update(Closed, Vector3.zero, 0.29));
            Assert.AreEqual(PinchEvents.BeginDrag | PinchEvents.Drag, pinch.Update(Closed, Vector3.zero, 0.31));
        }

        [Test]
        public void OpeningDuringDrag_EndsDragAndReleases()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);
            pinch.Update(Closed, new Vector3(0.05f, 0f, 0f), 0.05);

            Assert.AreEqual(PinchEvents.EndDrag | PinchEvents.Up, pinch.Update(Open, Vector3.zero, 0.1));
            Assert.IsFalse(pinch.IsDragging);
        }

        [Test]
        public void Release_EndsAnActiveDrag_AndIsIdempotent()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);
            pinch.Update(Closed, new Vector3(0.05f, 0f, 0f), 0.05);

            Assert.AreEqual(PinchEvents.EndDrag | PinchEvents.Up, pinch.Release());
            Assert.AreEqual(PinchEvents.None, pinch.Release());
        }

        [Test]
        public void DownPoint_IsWhereThePinchClosed()
        {
            var start = new Vector3(0.1f, 1.2f, 0.3f);
            pinch.Update(Closed, start, 0.0);
            pinch.Update(Closed, start + Vector3.up * 0.1f, 0.05);

            Assert.AreEqual(start, pinch.DownPoint);
        }

        [Test]
        public void NewPinch_StartsFresh()
        {
            pinch.Update(Closed, Vector3.zero, 0.0);
            pinch.Update(Closed, new Vector3(0.05f, 0f, 0f), 0.05);
            pinch.Update(Open, Vector3.zero, 0.1);

            var second = new Vector3(1f, 1f, 1f);
            Assert.AreEqual(PinchEvents.Down, pinch.Update(Closed, second, 0.2));
            Assert.AreEqual(second, pinch.DownPoint);
            Assert.IsFalse(pinch.IsDragging);
        }
    }
}
