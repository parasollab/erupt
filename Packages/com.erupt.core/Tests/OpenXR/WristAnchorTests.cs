using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ui;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// Where tier 1 sits on the left controller: behind the controller's origin, lifted and
    /// leaned back like a watch face, so that the controller's own ray cannot cross it and its
    /// front faces the user's head. With tracked hands the wrist-joint placement is unchanged.
    /// </summary>
    /// <remarks>
    /// "Held naturally" is taken to mean: the user stands, eyes at 1.6 m, looking ahead; the
    /// left controller is in front of the body, 20 cm left of the midline, 45 cm below the eyes
    /// and 35 cm ahead of them, thumb on top (no roll), pointing ahead: level or up to 30
    /// degrees down, and up to 15 degrees to either side. The controller's transform is its
    /// pointing pose (XRI's Position / Rotation actions bind the pointer pose first): the ray
    /// leaves the origin along +Z.
    /// </remarks>
    public class WristAnchorTests
    {
        private static readonly Vector3 Head = new Vector3(0f, 1.6f, 0f);
        private static readonly Vector3 HeldAt = Head + new Vector3(-0.20f, -0.45f, 0.35f);

        private GameObject rigGo, controllerGo, wristGo, anchorGo, barGo;
        private WristAnchor wrist;
        private TierOneBar bar;
        private readonly Vector3[] corners = new Vector3[4];

        private IEnumerator Build(Vector3 controllerPosition, Quaternion controllerRotation)
        {
            // As in the scenes: the controller and the wrist anchor are siblings under the rig's
            // offset; the tier 1 anchor starts under the wrist anchor and the bar hangs on it.
            rigGo = new GameObject("Camera Offset");
            controllerGo = new GameObject("Left Controller");
            controllerGo.transform.SetParent(rigGo.transform, false);
            controllerGo.transform.SetPositionAndRotation(controllerPosition, controllerRotation);
            wristGo = new GameObject("Wrist Anchor");
            wristGo.transform.SetParent(rigGo.transform, false);
            anchorGo = new GameObject("Tier 1 Anchor");
            anchorGo.transform.SetParent(wristGo.transform, false);
            wrist = wristGo.AddComponent<WristAnchor>();
            wrist.Configure(anchorGo.transform, controllerGo.transform);
            barGo = new GameObject("Tier 1");
            barGo.transform.SetParent(anchorGo.transform, false);
            bar = barGo.AddComponent<TierOneBar>();
            yield return null;
            yield return null;
            Canvas.ForceUpdateCanvases();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(rigGo);

        // The bar's rectangle in the world: bottom left, top left, top right, bottom right.
        private Vector3[] BarCorners()
        {
            bar.Canvas.GetComponent<RectTransform>().GetWorldCorners(corners);
            return corners;
        }

        private Vector3 BarCentre()
        {
            Vector3[] c = BarCorners();
            return (c[0] + c[2]) * 0.5f;
        }

        // Whether a ray meets the rectangle (either face), grown by a margin on every side.
        private static bool Hits(Ray ray, Vector3[] c, float margin)
        {
            Vector3 u = c[3] - c[0], v = c[1] - c[0];
            Vector3 normal = Vector3.Cross(u, v).normalized;
            float facing = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(facing) < 1e-6f) return false;
            float t = Vector3.Dot(c[0] - ray.origin, normal) / facing;
            if (t < 0f) return false;
            Vector3 p = ray.origin + ray.direction * t - c[0];
            float along = Vector3.Dot(p, u.normalized), up = Vector3.Dot(p, v.normalized);
            return along >= -margin && along <= u.magnitude + margin && up >= -margin && up <= v.magnitude + margin;
        }

        [UnityTest]
        public IEnumerator OnTheController_TheAnchorTakesTheSerializedOffsets()
        {
            yield return Build(new Vector3(3f, 1f, -2f), Quaternion.Euler(20f, 130f, 5f));

            Assert.AreSame(controllerGo.transform, anchorGo.transform.parent, "The anchor rides the controller while it is active.");
            Assert.IsTrue(anchorGo.activeSelf);
            Assert.AreEqual(new Vector3(0f, 0.05f, -0.12f), wrist.ControllerPose.position, "The default position: behind the origin and lifted.");
            Assert.Less(Quaternion.Angle(Quaternion.Euler(45f, -20f, 0f), wrist.ControllerPose.rotation), 1e-3f,
                "The default rotation: leaned back 45 degrees and turned 20 degrees toward the body's midline.");
            Assert.AreEqual(0.4f, wrist.ControllerScale, 1e-6f);
            Assert.Less(Vector3.Distance(wrist.ControllerPose.position, anchorGo.transform.localPosition), 1e-6f);
            Assert.Less(Quaternion.Angle(wrist.ControllerPose.rotation, anchorGo.transform.localRotation), 1e-3f);
            Assert.AreEqual(0.4f, anchorGo.transform.localScale.x, 1e-6f);

            // The numbers are tuned on a headset: a change applies on the next frame.
            wrist.ControllerPose = new Pose(new Vector3(0.01f, 0.07f, -0.15f), Quaternion.Euler(60f, 10f, 0f));
            wrist.ControllerScale = 0.5f;
            yield return null;
            Assert.Less(Vector3.Distance(new Vector3(0.01f, 0.07f, -0.15f), anchorGo.transform.localPosition), 1e-6f);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(60f, 10f, 0f), anchorGo.transform.localRotation), 1e-3f);
            Assert.AreEqual(0.5f, anchorGo.transform.localScale.x, 1e-6f);
        }

        [UnityTest]
        public IEnumerator TheControllersOwnRay_CannotCrossTheBar()
        {
            // Wherever the controller is and however it is turned.
            yield return Build(new Vector3(3f, 1f, -2f), Quaternion.Euler(20f, 130f, 5f));
            Vector3[] c = BarCorners();
            Transform controller = controllerGo.transform;

            // All of the bar is behind the controller's origin: no ray that leaves the origin
            // forward of the controller's own XY plane can reach it.
            foreach (Vector3 corner in c)
                Assert.Less(controller.InverseTransformPoint(corner).z, -0.05f, "Every corner of the bar is well behind the controller's origin.");

            // The ray itself, and a cone of 60 degrees round it (twice the 30 asked for),
            // against the bar grown by 2 cm on every side.
            int rays = 0;
            for (float off = 0f; off <= 60f; off += 2.5f)
            {
                for (float around = 0f; around < 360f; around += 10f)
                {
                    Vector3 local = Quaternion.AngleAxis(around, Vector3.forward) * (Quaternion.AngleAxis(off, Vector3.right) * Vector3.forward);
                    var ray = new Ray(controller.position, controller.TransformDirection(local));
                    Assert.IsFalse(Hits(ray, c, 0.02f), $"A ray {off} degrees off the controller's forward axis (turned {around}) meets the bar.");
                    rays++;
                    if (off == 0f) break;
                }
            }
            Assert.Greater(rays, 800);

            // The check is not vacuous: a ray aimed at the bar's centre does meet it, and so
            // does the controller's ray with the bar where it used to be, at the origin.
            Assert.IsTrue(Hits(new Ray(controller.position, (BarCentre() - controller.position).normalized), c, 0f));
            wrist.ControllerPose = new Pose(Vector3.zero, Quaternion.identity);
            wrist.ControllerScale = 1f;
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(Hits(new Ray(controller.position + controller.forward * -0.001f, controller.forward), BarCorners(), 0f),
                "At the controller's origin with no rotation the bar stands across the ray.");
        }

        [UnityTest]
        public IEnumerator HeldNaturally_TheBarsFrontFacesTheUsersHead_AndReadsUpright()
        {
            yield return Build(HeldAt, Quaternion.identity);
            Transform canvas = bar.Canvas.transform;

            foreach (float pitchDown in new[] { 0f, 10f, 20f, 30f })
            {
                foreach (float yaw in new[] { -15f, 0f, 15f })
                {
                    controllerGo.transform.SetPositionAndRotation(HeldAt, Quaternion.Euler(pitchDown, yaw, 0f));
                    yield return null;
                    string pose = $"controller {pitchDown} degrees down, {yaw} to the side";

                    // A world-space canvas is read from the side its -Z points to.
                    Vector3 centre = BarCentre();
                    Vector3 toHead = (Head - centre).normalized;
                    Vector3 front = -canvas.forward;
                    float angle = Vector3.Angle(front, toHead);
                    Assert.Less(angle, 40f, $"The bar's front points at the head within 40 degrees ({pose}): {angle:F1}.");

                    // Seen from the head, the text runs left to right and stands upright.
                    Vector3 gaze = -toHead;
                    Vector3 viewRight = Vector3.Cross(Vector3.up, gaze).normalized;
                    Vector3 viewUp = Vector3.Cross(gaze, viewRight);
                    Assert.Greater(Vector3.Dot(canvas.right, viewRight), 0.7f, $"The bar reads left to right ({pose}).");
                    Assert.Greater(Vector3.Dot(canvas.up, viewUp), 0.7f, $"The bar is upright in the user's view ({pose}).");

                    // Within reach of a glance: below the eyes, in front of the body, at a watch's distance.
                    Assert.That(Vector3.Distance(Head, centre), Is.InRange(0.3f, 0.7f), pose);
                }
            }

            // On the wrist it is a wrist-sized thing, not the arm's-length bar.
            Vector3[] c = BarCorners();
            Assert.That(Vector3.Distance(c[0], c[3]), Is.InRange(0.12f, 0.25f), "The bar's width on the controller, in metres.");
            Assert.That(Vector3.Distance(c[0], c[1]), Is.InRange(0.02f, 0.06f), "The bar's height on the controller, in metres.");
        }

        [UnityTest]
        public IEnumerator WithTheControllerPutDown_TheAnchorIsOnTheWrist_AsBefore()
        {
            yield return Build(HeldAt, Quaternion.identity);
            Assert.AreSame(controllerGo.transform, anchorGo.transform.parent);

            // XRI deactivates the controller object when the user puts it down for hands.
            controllerGo.SetActive(false);
            yield return null;

            Assert.AreSame(wristGo.transform, anchorGo.transform.parent, "The anchor moves to the wrist anchor, which follows the wrist joint.");
            Assert.Less(Vector3.Distance(new Vector3(0f, 0.06f, 0f), anchorGo.transform.localPosition), 1e-6f, "The hand placement is what it was: 6 cm off the back of the hand.");
            Assert.Less(Quaternion.Angle(Quaternion.identity, anchorGo.transform.localRotation), 1e-3f);
            Assert.AreEqual(Vector3.one, anchorGo.transform.localScale, "And at its own size.");
            Assert.IsFalse(anchorGo.activeSelf, "With no tracked hand either, tier 1 is hidden rather than left at the rig's origin.");

            controllerGo.SetActive(true);
            yield return null;
            Assert.AreSame(controllerGo.transform, anchorGo.transform.parent, "Picked up again: back on the controller.");
            Assert.IsTrue(anchorGo.activeSelf);
            Assert.Less(Vector3.Distance(wrist.ControllerPose.position, anchorGo.transform.localPosition), 1e-6f);
        }
    }
}
