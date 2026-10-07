using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// A carried object must stay visible under the gripper link, stay silent on
    /// /collision_object, and come back untouched (no respawn) when it is put down.
    /// </summary>
    public class AttachedCollisionObjectTests
    {
        private const string SceneTopic = "/collision_objects_ros";
        private const string AttachTopic = "/attached_collision_objects_ros";

        private FakeRosBus bus;
        private GameObject host;
        private GameObject robot;
        private Transform hand;
        private CollisionObjectsListenerSimple scene;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
        }

        [TearDown]
        public void TearDown()
        {
            // Collected first: destroying the host clears the registry.
            var spawned = scene != null
                ? new System.Collections.Generic.List<GameObject>(scene.objectsById.Values)
                : new System.Collections.Generic.List<GameObject>();
            if (host != null) Object.DestroyImmediate(host);
            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            if (robot != null) Object.DestroyImmediate(robot);
            RosBus.Reset();
        }

        [UnityTest]
        public IEnumerator Attach_ReparentsTheSameObjectAtTheLinkRelativePose()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", new Vector3(1f, 0f, 0f)));
            Assert.IsTrue(scene.TryGetObject("cube", out var before));

            // ROS FLU (0.1 forward, 0.2 left, 0.3 up) -> Unity link-local (-0.2, 0.3, 0.1).
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.1, 0.2, 0.3));

            Assert.IsTrue(scene.TryGetObject("cube", out var after));
            Assert.AreSame(before, after, "Attach must not destroy or respawn the object.");
            Assert.AreSame(hand, after.transform.parent);
            AssertClose(new Vector3(-0.2f, 0.3f, 0.1f), after.transform.localPosition);
            Assert.IsTrue(scene.attachedIds.Contains("cube"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Attach_CompensatesNonUnitLinkScale()
        {
            yield return Build();
            robot.transform.localScale = Vector3.one * 2f;
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.0, 0.0, 0.5));

            scene.TryGetObject("cube", out var go);
            AssertClose(Vector3.one, go.transform.lossyScale);
            AssertClose(hand.position + hand.up * 0.5f, go.transform.position);
        }

        [UnityTest]
        public IEnumerator Attach_MapsRosLinkPrefixToTheUnityRobot()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0));

            scene.TryGetObject("cube", out var go);
            Assert.AreSame(hand, go.transform.parent);
        }

        [UnityTest]
        public IEnumerator Attach_UnknownLink_LeavesTheObjectVisibleInPlace()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", new Vector3(1f, 0f, 0f)));
            scene.TryGetObject("cube", out var go);
            Vector3 where = go.transform.position;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No link transform"));
            bus.Inbound(AttachTopic, Attached("cube", "no_such_link", CollisionObjectMsg.ADD, 0, 0, 0.5));

            Assert.IsTrue(go.activeInHierarchy);
            AssertClose(where, go.transform.position);
        }

        [UnityTest]
        public IEnumerator Attach_ForUnknownObject_BuildsItFromTheMessageGeometry()
        {
            yield return Build();
            bus.Inbound(AttachTopic, Attached("late", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));

            Assert.IsTrue(scene.TryGetObject("late", out var go), "Joining mid-carry must still show the object.");
            Assert.AreEqual(1, go.transform.childCount);
            Assert.AreSame(hand, go.transform.parent);
        }

        [UnityTest]
        public IEnumerator WhileAttached_SceneTopicMessagesAreIgnored()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            scene.TryGetObject("cube", out var go);
            Vector3 local = go.transform.localPosition;

            bus.Inbound(SceneTopic, new CollisionObjectMsg { id = "cube", operation = CollisionObjectMsg.REMOVE });
            bus.Inbound(SceneTopic, Box("cube", new Vector3(5f, 5f, 5f), CollisionObjectMsg.MOVE));
            bus.Inbound(SceneTopic, Box("cube", new Vector3(5f, 5f, 5f)));
            yield return null;

            Assert.IsTrue(go != null, "REMOVE while attached must not delete the carried object.");
            Assert.AreSame(hand, go.transform.parent);
            AssertClose(local, go.transform.localPosition);
        }

        [UnityTest]
        public IEnumerator WhileAttached_NothingIsPublishedForTheObject()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            yield return null; // child publisher starts
            scene.TryGetObject("cube", out var go);
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>())
                pub.publishRateHz = 100f;

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            bus.Clear();

            for (int i = 0; i < 5; i++)
            {
                robot.transform.position += new Vector3(0.2f, 0f, 0f); // the carry
                yield return new WaitForSeconds(0.02f);
            }
            Assert.AreEqual(0, bus.CountOn("/collision_object"), "A carried object must not stream MOVEs to MoveIt.");

            // Release: detach, then the watcher's re-ADD with the place pose.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(SceneTopic, Box("cube", new Vector3(2f, 0f, 0f)));
            yield return new WaitForSeconds(0.05f);
            Assert.AreEqual(0, bus.CountOn("/collision_object"), "The detach re-add must not be echoed back.");

            // Later participant moves publish normally again.
            go.transform.position += new Vector3(0f, 0f, 0.3f);
            yield return new WaitForSeconds(0.05f);
            Assert.Greater(bus.CountOn("/collision_object"), 0);
        }

        [UnityTest]
        public IEnumerator Detach_RestoresParentAndLocks_AndReAddDoesNotRespawn()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            Transform originalParent = go.transform.parent;
            GameObject child = go.transform.GetChild(0).gameObject;
            var rb = child.GetComponent<Rigidbody>();
            rb.isKinematic = false;

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.IsTrue(rb.isKinematic, "Physics must not move a carried object.");

            robot.transform.position = new Vector3(0f, 1f, 0f);
            Vector3 released = go.transform.position;
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));

            Assert.AreSame(originalParent, go.transform.parent);
            AssertClose(released, go.transform.position);
            Assert.IsFalse(rb.isKinematic, "Rigidbody settings must be restored on detach.");
            Assert.IsFalse(scene.attachedIds.Contains("cube"));

            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));
            yield return null;

            Assert.IsTrue(child != null, "The post-detach ADD must move the object, not rebuild it.");
            AssertClose(new Vector3(0.5f, 0f, 0f), go.transform.position);
        }

        [UnityTest]
        public IEnumerator RemoveAfterDetach_DeletesTheObject()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(SceneTopic, new CollisionObjectMsg { id = "cube", operation = CollisionObjectMsg.REMOVE });
            yield return null;

            Assert.IsTrue(go == null);
            Assert.IsFalse(scene.TryGetObject("cube", out _));
        }

        [UnityTest]
        public IEnumerator PickingTheSameObjectTwice_Works()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);

            for (int i = 0; i < 2; i++)
            {
                bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
                Assert.AreSame(hand, go.transform.parent);
                bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
                bus.Inbound(SceneTopic, Box("cube", new Vector3(i + 1f, 0f, 0f)));
                Assert.AreNotSame(hand, go.transform.parent);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ClearingTheRegistryMidCarry_LeavesNothingUnderTheLink()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));

            scene.ClearRegistry();
            yield return null;

            Assert.AreEqual(0, hand.childCount, "No orphan may stay parented to the robot link.");
            Assert.AreEqual(0, scene.attachedIds.Count);
        }

        // ---------- helpers ----------

        private IEnumerator Build()
        {
            robot = new GameObject("robot");
            var link0 = new GameObject("fr3_link0");
            link0.transform.SetParent(robot.transform, false);
            hand = new GameObject("fr3_hand").transform;
            hand.SetParent(link0.transform, false);
            hand.localPosition = new Vector3(0f, 0.5f, 0f);

            host = new GameObject("listeners");
            scene = host.AddComponent<CollisionObjectsListenerSimple>();
            scene.requestInitialPlanningScene = false;

            var ik = host.AddComponent<DirectArticulationIKController>();
            ik.enabled = false;
            SetPrivate(ik, "robotRoot", robot.transform);

            var attach = host.AddComponent<AttachedCollisionObjectListener>();
            SetPrivate(attach, "sceneListener", scene);
            SetPrivate(attach, "ikController", ik);

            yield return null; // Start subscribes to the fake bus.
        }

        private static void SetPrivate(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        // unityPosition is converted to the project's ROS world convention (x, z, y).
        private static CollisionObjectMsg Box(string id, Vector3 unityPosition, sbyte operation = CollisionObjectMsg.ADD)
        {
            return new CollisionObjectMsg
            {
                id = id,
                operation = operation,
                pose = new PoseMsg(
                    new PointMsg(unityPosition.x, unityPosition.z, unityPosition.y),
                    new QuaternionMsg(0, 0, 0, 1)),
                primitives = operation == CollisionObjectMsg.MOVE
                    ? new SolidPrimitiveMsg[0]
                    : new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.1, 0.1, 0.1 } } }
            };
        }

        private static AttachedCollisionObjectMsg Attached(string id, string link, sbyte operation, double x, double y, double z)
        {
            var obj = Box(id, Vector3.zero, operation == CollisionObjectMsg.REMOVE ? CollisionObjectMsg.MOVE : CollisionObjectMsg.ADD);
            obj.operation = operation;
            obj.header.frame_id = link;
            obj.pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1));
            return new AttachedCollisionObjectMsg { link_name = link, @object = obj };
        }

        private static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}");
    }
}
