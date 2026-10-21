using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using Factory;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// A part the robot lets go of falls onto what is under it and is frozen again (kinematic,
    /// lifted by the rest gap) where it lands, with the drop reported.
    /// </summary>
    public class ReleasedPartGravityTests
    {
        FakeRosBus bus;
        GameObject host, floor, part;
        ReleasedPartGravity gravity;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            host = new GameObject("gravity host");
            gravity = host.AddComponent<ReleasedPartGravity>();
            gravity.restGap = 0.005f;
            gravity.settleTimeout = 4f;

            floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "parallels";
            floor.transform.position = new Vector3(2f, 0.5f, 2f);
            floor.transform.localScale = new Vector3(0.5f, 0.1f, 0.5f); // top at 0.55

            part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = "blank_1";
            part.transform.localScale = new Vector3(0.08f, 0.06f, 0.05f);
            part.transform.position = new Vector3(2f, 0.75f, 2f); // bottom face 0.17 above the top
            var rb = part.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            var pub = part.AddComponent<CollisionObjectPublisher>();
            pub.isMesh = false;
            pub.objectId = "blank_1";
            pub.publishRateHz = 100f;
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (part != null) Object.DestroyImmediate(part);
            if (floor != null) Object.DestroyImmediate(floor);
            if (host != null) Object.DestroyImmediate(host);
            RosBus.Reset();
        }

        IEnumerator WaitForSettle(float timeout = 8f)
        {
            float deadline = Time.time + timeout;
            yield return null;
            while (gravity.IsSettling(part) && Time.time < deadline) yield return null;
            Assert.IsFalse(gravity.IsSettling(part), "part still settling after the timeout");
        }

        [UnityTest]
        public IEnumerator DroppedPart_LandsOnTheSurface_AndFreezesWithTheRestGap()
        {
            ReleasedPartGravity.DropReport reported = default;
            bool settled = false;
            gravity.Settled += r => { reported = r; settled = true; };

            gravity.Drop(part);
            Assert.IsTrue(gravity.IsSettling(part));
            Assert.IsFalse(part.GetComponent<Rigidbody>().isKinematic, "dynamic while it falls");
            Assert.IsTrue(part.GetComponent<CollisionObjectPublisher>().pausePublishing, "no mid-air MOVEs");
            yield return WaitForSettle();

            var rb = part.GetComponent<Rigidbody>();
            Assert.IsTrue(rb.isKinematic, "frozen again where it landed");
            Assert.IsFalse(rb.useGravity);
            Assert.IsFalse(part.GetComponent<CollisionObjectPublisher>().pausePublishing, "publisher resumed");
            // Resting on the top face (0.55) + half height (0.03) + rest gap (0.005).
            Assert.AreEqual(0.585f, part.transform.position.y, 0.006f, "rests on the surface with the rest gap");
            Assert.Less(Vector3.Angle(part.transform.up, Vector3.up), 3f, "landed flat");
            Assert.IsTrue(settled, "Settled raised");
            Assert.AreEqual("blank_1", reported.partId);
            Assert.AreEqual(0.75f - 0.585f, reported.dropHeight, 0.01f);
            Assert.IsFalse(reported.timedOut);
            Assert.IsTrue(gravity.HasDrop);
        }

        [UnityTest]
        public IEnumerator PartOutsideThePartsRoot_IsIgnored()
        {
            gravity.partsRoot = new GameObject("Parts").transform;
            gravity.partsRoot.SetParent(host.transform, false);
            Vector3 before = part.transform.position;

            gravity.Drop(part);
            yield return null;
            yield return new WaitForSeconds(0.3f);

            Assert.IsFalse(gravity.IsSettling(part));
            Assert.AreEqual(before, part.transform.position, "a part that is not the station's stays put");
            Assert.IsTrue(part.GetComponent<Rigidbody>().isKinematic);
        }

        [UnityTest]
        public IEnumerator PreviewDrop_IsFrozenWhenThePreviewEnds()
        {
            var playerHost = new GameObject("player");
            var player = playerHost.AddComponent<MTCTrajectoryPlayer>();
            player.enabled = false;
            gravity.trajectoryPlayer = player;
            // Re-run Start's subscription through a fresh component so the player event is wired.
            Object.DestroyImmediate(gravity);
            gravity = host.AddComponent<ReleasedPartGravity>();
            gravity.trajectoryPlayer = player;
            yield return null;

            gravity.Drop(part, preview: true);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.IsTrue(gravity.IsSettling(part));
            Assert.IsFalse(part.GetComponent<Rigidbody>().isKinematic);

            // What RestorePose does first: announce the end, then put the object back.
            typeof(ReleasedPartGravity).GetMethod("OnPreviewEnding", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(gravity, null);
            Assert.IsFalse(gravity.IsSettling(part), "preview drop aborted");
            Assert.IsTrue(part.GetComponent<Rigidbody>().isKinematic, "kinematic again before the pose is restored");
            Object.DestroyImmediate(playerHost);
        }
    }
}
