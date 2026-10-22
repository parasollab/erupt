using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using Factory;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// The self-centring vise computes what its jaws do to a part from where the part really
    /// is: a slightly skewed, off-centre part is turned flat and centred; a badly skewed one is
    /// clamped on its corners and reported; closing on nothing is reported too.
    /// </summary>
    public class MachineViseTests
    {
        const float OpenGap = 0.12f, JawWidth = 0.16f, JawHeight = 0.03f;
        static readonly Vector3 BilletSize = new Vector3(0.08f, 0.06f, 0.05f);

        FakeRosBus bus;
        GameObject rig, part;
        MachineVise vise;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            rig = new GameObject("vise rig");
            vise = BuildVise(rig.transform, new Vector3(0.3f, 1f, 0.5f));
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (part != null) Object.DestroyImmediate(part);
            if (rig != null) Object.DestroyImmediate(rig);
            RosBus.Reset();
        }

        // Same construction as MtcSceneBuilder.BuildVise, cubes only.
        static MachineVise BuildVise(Transform root, Vector3 baseCentre)
        {
            var baseSize = new Vector3(0.16f, 0.03f, 0.20f);
            var parallelsSize = new Vector3(0.16f, 0.015f, 0.09f);
            var jawSize = new Vector3(JawWidth, 0.045f, 0.026f);
            const float plateThickness = 0.004f;
            Transform fixture = new GameObject("MachineFixture").transform;
            fixture.SetParent(root, false);
            fixture.position = baseCentre;
            Box("Base", fixture, baseCentre, baseSize);
            float baseTop = baseCentre.y + baseSize.y * 0.5f;
            Box("Parallels", fixture, new Vector3(baseCentre.x, baseTop + parallelsSize.y * 0.5f, baseCentre.z), parallelsSize);
            float parallelsTop = baseTop + parallelsSize.y;
            var jaws = new SlidingPart[2];
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                float faceZ = baseCentre.z + side * OpenGap * 0.5f;
                Transform jaw = new GameObject(i == 0 ? "JawFront" : "JawBack").transform;
                jaw.SetParent(fixture, false);
                jaw.position = new Vector3(baseCentre.x, baseTop, faceZ);
                Box("Body", jaw, new Vector3(baseCentre.x, baseTop + jawSize.y * 0.5f, faceZ + side * (plateThickness + jawSize.z * 0.5f)), jawSize);
                Box("Plate", jaw, new Vector3(baseCentre.x, parallelsTop + JawHeight * 0.5f, faceZ + side * plateThickness * 0.5f), new Vector3(JawWidth, JawHeight, plateThickness));
                var slide = jaw.gameObject.AddComponent<SlidingPart>();
                slide.openOffset = new Vector3(0f, 0f, -side * OpenGap * 0.5f);
                slide.travelTime = 0.05f;
                jaws[i] = slide;
            }
            Transform window = new GameObject("JawWindow").transform;
            window.SetParent(fixture, false);
            window.position = new Vector3(baseCentre.x, parallelsTop, baseCentre.z);
            var vise = fixture.gameObject.AddComponent<MachineVise>();
            vise.jaws = jaws;
            vise.jawWindow = window;
            vise.openGap = OpenGap;
            vise.jawWidth = JawWidth;
            vise.jawHeight = JawHeight;
            vise.requiredStickout = 0.03f;
            vise.travelTime = 0.05f;
            return vise;
        }

        static GameObject Box(string name, Transform parent, Vector3 centre, Vector3 size)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            go.transform.localScale = size;
            return go;
        }

        // A billet resting on the parallels (with the station's 5 mm rest gap), yawed and offset.
        GameObject Billet(float yawDegrees, float offsetX, float offsetZ, Quaternion? extraRotation = null)
        {
            Quaternion rot = Quaternion.AngleAxis(yawDegrees, Vector3.up) * (extraRotation ?? Quaternion.identity);
            Vector3 half = Vector3.Scale(BilletSize, Vector3.one) * 0.5f;
            // Height of the bottom face for this rotation: project the half extents onto up.
            float rise = Mathf.Abs(Vector3.Dot(rot * Vector3.right, Vector3.up)) * half.x
                       + Mathf.Abs(Vector3.Dot(rot * Vector3.up, Vector3.up)) * half.y
                       + Mathf.Abs(Vector3.Dot(rot * Vector3.forward, Vector3.up)) * half.z;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "blank_1";
            go.transform.localScale = BilletSize;
            go.transform.SetPositionAndRotation(vise.jawWindow.position + new Vector3(offsetX, rise + 0.005f, offsetZ), rot);
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            var pub = go.AddComponent<CollisionObjectPublisher>();
            pub.isMesh = false;
            pub.objectId = "blank_1";
            pub.publishRateHz = 100f;
            return go;
        }

        IEnumerator WaitForJaws(float timeout = 5f)
        {
            float deadline = Time.time + timeout;
            yield return null;
            while (vise.IsMoving && Time.time < deadline) yield return null;
            Assert.IsFalse(vise.IsMoving, "vise still moving after the timeout");
        }

        static float YawError(Transform t)
        {
            Vector3 r = t.right; r.y = 0f;
            float theta = Mathf.Atan2(r.z, r.x) * Mathf.Rad2Deg;
            return Mathf.Abs(Mathf.Repeat(theta + 45f, 90f) - 45f);
        }

        [UnityTest]
        public IEnumerator SkewedOffCentrePart_IsSquaredAndCentred()
        {
            part = Billet(12f, 0.01f, 0.02f);
            Physics.SyncTransforms();
            vise.ScanPresence();
            Assert.AreSame(part, vise.PresentPart, "part between the open jaws is detected");
            Assert.IsTrue(vise.CanClamp);

            vise.Clamp();
            yield return WaitForJaws();

            Assert.AreEqual(MachineVise.ViseState.Clamped, vise.State, vise.Describe());
            Assert.Less(YawError(part.transform), 0.5f, "part face turned parallel to the jaw plates");
            Vector3 local = vise.jawWindow.InverseTransformPoint(part.transform.position);
            Assert.Less(Mathf.Abs(local.z), 1e-3f, "part centred on the clamp axis");
            Assert.AreEqual(0.01f, local.x, 1e-3f, "position along the jaws is left alone");
            var r = vise.LastReport;
            Assert.IsTrue(r.squared && r.seated && !r.overhang && r.stickoutOk, string.Join("; ", r.problems));
            Assert.AreEqual(12f, Mathf.Abs(r.skewDegrees), 0.5f);
            // 6 cm billet on the parallels (+5 mm gap) above 3 cm plates.
            Assert.AreEqual(BilletSize.y + 0.005f - JawHeight, r.stickout, 2e-3f);
            // The jaws stopped on the part's 5 cm faces (plus the hidden clamp gap).
            Assert.AreEqual(BilletSize.z * 0.5f + vise.clampGap, vise.CurrentHalfGap, 1e-3f);
        }

        [UnityTest]
        public IEnumerator BadlySkewedPart_IsClampedOnItsCorners()
        {
            part = Billet(40f, 0f, 0f);
            Physics.SyncTransforms();
            Quaternion before = part.transform.rotation;

            vise.Clamp();
            yield return WaitForJaws();

            Assert.AreEqual(MachineVise.ViseState.ClampedPoor, vise.State, vise.Describe());
            Assert.Less(Quaternion.Angle(before, part.transform.rotation), 0.1f, "no squaring beyond the skew limit");
            Assert.IsFalse(vise.LastReport.squared);
            StringAssert.Contains("corners", vise.Describe());
            Assert.Greater(vise.CurrentHalfGap, BilletSize.z * 0.5f + 0.01f, "jaws stop on the corners, wider than the face");
        }

        [UnityTest]
        public IEnumerator PartLaidOnItsSide_FailsTheStickout()
        {
            // 6 cm side down: 5 cm tall (+5 mm rest gap), 2.5 cm above the 3 cm plates, under the 3 cm the job wants.
            part = Billet(0f, 0f, 0f, Quaternion.AngleAxis(90f, Vector3.right));
            Physics.SyncTransforms();

            vise.Clamp();
            yield return WaitForJaws();

            Assert.AreEqual(MachineVise.ViseState.ClampedPoor, vise.State, vise.Describe());
            Assert.IsTrue(vise.LastReport.squared, "it still lies flat and squares");
            Assert.IsFalse(vise.LastReport.stickoutOk);
            Assert.AreEqual(BilletSize.z + 0.005f - JawHeight, vise.LastReport.stickout, 2e-3f);
            StringAssert.Contains("stickout", vise.Describe());
        }

        [UnityTest]
        public IEnumerator NoPart_ClosesOnNothing_ThenReleases()
        {
            vise.ScanPresence();
            Assert.IsNull(vise.PresentPart);

            vise.Clamp();
            yield return WaitForJaws();
            Assert.AreEqual(MachineVise.ViseState.Empty, vise.State);
            Assert.Less(vise.CurrentHalfGap, 1e-3f, "jaws meet in the middle");
            Assert.IsFalse(vise.CanClamp);
            Assert.IsTrue(vise.CanRelease);

            vise.Release();
            yield return WaitForJaws();
            Assert.AreEqual(MachineVise.ViseState.Open, vise.State);
            Assert.AreEqual(OpenGap * 0.5f, vise.CurrentHalfGap, 1e-4f);
        }

        // A real cell interlocks the clamp on "robot clear": with the gripper still between the
        // jaws (e.g. a retreat that never happened) the jaws must not close on it.
        [UnityTest]
        public IEnumerator GripperInTheJaws_CannotBeClamped_UntilItLeaves()
        {
            part = Billet(0f, 0f, 0f);
            // Beside the billet (half width 0.04) but inside the jaw window (half width 0.08). An
            // articulation root ignores transform writes (TeleportRoot moves it), so place it first.
            var gripper = new GameObject("fr3_hand");
            gripper.transform.position = vise.jawWindow.position + new Vector3(0.062f, 0.05f, 0f);
            var finger = GameObject.CreatePrimitive(PrimitiveType.Cube);
            finger.transform.SetParent(gripper.transform, false);
            finger.transform.localScale = new Vector3(0.02f, 0.04f, 0.02f);
            var body = gripper.AddComponent<ArticulationBody>();
            body.immovable = true; // a free articulation root would fall and be shoved by the billet
            body.useGravity = false;
            // A new articulation body joins the physics scene on the next simulation step.
            yield return new WaitForFixedUpdate();
            yield return null;
            vise.ScanPresence();

            Assert.AreSame(part, vise.PresentPart);
            Assert.IsTrue(vise.RobotInJaws, "a robot link inside the window is detected");
            Assert.IsFalse(vise.CanClamp);
            StringAssert.Contains("robot not clear", vise.Describe());
            vise.Clamp();
            yield return null;
            Assert.AreEqual(MachineVise.ViseState.Open, vise.State, "the jaws did not close on the gripper");

            body.TeleportRoot(gripper.transform.position + Vector3.up * 0.5f, gripper.transform.rotation);
            yield return new WaitForFixedUpdate();
            vise.ScanPresence();
            Assert.IsFalse(vise.RobotInJaws);
            Assert.IsTrue(vise.CanClamp, "clamp allowed once the robot is clear");
            Object.DestroyImmediate(gripper);
        }

        [UnityTest]
        public IEnumerator PartStillInTheGripper_CannotBeClamped()
        {
            part = Billet(0f, 0f, 0f);
            part.GetComponent<CollisionObjectPublisher>().attachedToRobot = true;
            Physics.SyncTransforms();
            vise.ScanPresence();
            yield return null;

            Assert.AreSame(part, vise.PresentPart);
            Assert.IsTrue(vise.PresentPartHeldByRobot);
            Assert.IsFalse(vise.CanClamp);
            vise.Clamp();
            yield return null;
            Assert.AreEqual(MachineVise.ViseState.Open, vise.State);
        }
    }
}
