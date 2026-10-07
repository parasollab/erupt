using NUnit.Framework;
using UnityEngine;

namespace Erupt.Ui.Tests
{
    /// <summary>Where tier 3 opens relative to the head: in front, level, facing the user.</summary>
    public class TierUiRigPlacementTests
    {
        [Test]
        public void Placement_IsAtDistanceAlongFlattenedForward()
        {
            var head = new Vector3(1f, 1.6f, 2f);
            Vector3 lookingDown = (Vector3.forward + Vector3.down).normalized;   // at the table

            Vector3 position = TierUiRig.PlacementPositionFor(head, lookingDown, 1.1f, -0.1f);

            // Horizontal distance is the summon distance; the pitch of the gaze is ignored.
            Assert.AreEqual(1.1f, Vector3.Distance(new Vector3(position.x, 0f, position.z), new Vector3(head.x, 0f, head.z)), 1e-4f);
            Assert.AreEqual(1.6f - 0.1f, position.y, 1e-4f);
            Assert.Greater(position.z, head.z);
        }

        [Test] // Quaternion.LookRotation is native: this one only runs in the Editor's Test Runner.
        public void Placement_FacesTheUser_AndStaysUpright()
        {
            var head = Vector3.up * 1.6f;
            Vector3 gaze = new Vector3(1f, -0.3f, 1f).normalized;

            Pose pose = TierUiRig.PlacementFor(head, gaze, 1f, 0f);
            Vector3 panelForward = pose.rotation * Vector3.forward;

            // uGUI reads correctly when the canvas's +Z points away from the viewer.
            Vector3 awayFromHead = (pose.position - head); awayFromHead.y = 0f;
            Assert.Greater(Vector3.Dot(panelForward, awayFromHead.normalized), 0.999f);
            Assert.AreEqual(0f, panelForward.y, 1e-4f, "panel is not tilted with the gaze");
        }

        [Test]
        public void Placement_LookingStraightDown_StillHasADirection()
        {
            Vector3 position = TierUiRig.PlacementPositionFor(Vector3.zero, Vector3.down, 1f, 0f);
            Assert.AreEqual(1f, position.magnitude, 1e-4f);
            Assert.AreEqual(Vector3.forward, TierUiRig.FlatForward(Vector3.down));
        }
    }
}
