using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Ros;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using Erupt.Robot.Tests;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// A carried object must stay visible under the gripper link, stay silent on
    /// /collision_object, be locked against physics and grabs, and come back untouched
    /// (no respawn) when it is put down.
    /// </summary>
    public class AttachedCollisionObjectTests
    {
        private const string SceneTopic = "/collision_objects_ros";
        private const string AttachTopic = "/attached_collision_objects_ros";

        private FakeRosBus bus;
        private GameObject host;
        private GameObject robot;
        private GameObject selectionHost;
        private Transform hand;
        private EnvironmentRegistry registry;
        private CollisionObjectsListenerSimple scene;
        private AttachedCollisionObjectListener attachListener;
        private RobotRegistry robots;
        private RobotProfile profile;
        // One planning scene, in the root namespace: what a scene with a single robot has.
        private PlanningSceneFanout fanout;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            fanout = new PlanningSceneFanout();
            fanout.AddEndpoint(bus.Root);
        }

        [TearDown]
        public void TearDown()
        {
            // Collected first: destroying the host clears the listener's registry.
            var spawned = scene != null
                ? new System.Collections.Generic.List<GameObject>(scene.objectsById.Values)
                : new System.Collections.Generic.List<GameObject>();
            if (host != null) Object.DestroyImmediate(host);
            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            if (robot != null) Object.DestroyImmediate(robot);
            if (selectionHost != null) Object.DestroyImmediate(selectionHost);
            FakeRobots.DestroyAll(robots);
            robots = null;
            if (profile != null) Object.DestroyImmediate(profile);
            fanout.Dispose();
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
        public IEnumerator Attach_RecordsTheLinkInTheEnvironmentRegistry_AndDetachClearsIt()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            Assert.IsTrue(registry.TryGetObject("cube", out var env));
            Assert.IsNull(env.AttachedTo);

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, env.AttachedTo);

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.IsNull(env.AttachedTo);
            yield return null;
        }

        // Attach notices for an object that already rides the link (each executed step's scene
        // diff, the watcher's own echoes) restate the carry with link-relative poses that differ
        // by centimetres between sources. The first placement is kept.
        [UnityTest]
        public IEnumerator RepeatedAttach_OnTheSameLink_KeepsTheFirstPlacement()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", new Vector3(1f, 0f, 0f)));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.1, 0.2, 0.3));
            scene.TryGetObject("cube", out var go);
            Vector3 first = go.transform.localPosition;

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.13, 0.2, 0.3));
            Assert.AreSame(hand, go.transform.parent);
            AssertClose(first, go.transform.localPosition);

            // A detach and a fresh attach is a new grasp: the new pose applies.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.13, 0.2, 0.3));
            AssertClose(new Vector3(-0.2f, 0.3f, 0.13f), go.transform.localPosition);
            yield return null;
        }

        // The hand closes on the object where it is. A notice whose link-relative pose would
        // fling the object far away does not describe where the Unity hand is: the object stays
        // where it stands until a notice consistent with its position arrives.
        [UnityTest]
        public IEnumerator FarAttachNotice_KeepsTheObjectWhereItStands_UntilAConsistentOne()
        {
            yield return Build();
            SetPrivate(attachListener, "maxAttachJump", 0.25f);
            // The cube sits 0.1 m above the hand, i.e. link-local (0, 0.1, 0).
            Vector3 cubeWorld = hand.position + new Vector3(0f, 0.1f, 0f);
            bus.Inbound(SceneTopic, Box("cube", cubeWorld));
            scene.TryGetObject("cube", out var go);

            // 1 m forward in ROS: a metre from where the cube stands -> not applied; the cube stays
            // put and unparented (still locked and counted as attached) until a consistent notice.
            Transform parentBefore = go.transform.parent;
            LogAssert.Expect(LogType.Warning, new Regex("would move it"));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 1.0, 0.0, 0.0));
            Assert.AreSame(parentBefore, go.transform.parent);
            AssertClose(cubeWorld, go.transform.position);
            Assert.IsTrue(scene.attachedIds.Contains("cube"));

            // ROS (0, 0, 0.1) = Unity link-local (0, 0.1, 0): consistent with where it stands -> applied.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.0, 0.0, 0.1));
            AssertClose(new Vector3(0f, 0.1f, 0f), go.transform.localPosition);

            // And from then on restatements change nothing.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.02, 0.0, 0.1));
            AssertClose(new Vector3(0f, 0.1f, 0f), go.transform.localPosition);
            yield return null;
        }

        // A notice that arrives before the Unity hand has reached the object is re-checked each
        // frame, so the object starts riding as soon as the hand gets there.
        [UnityTest]
        public IEnumerator FarAttachNotice_IsAppliedOnceTheHandReachesTheObject()
        {
            yield return Build();
            SetPrivate(attachListener, "maxAttachJump", 0.25f);
            Vector3 cubeWorld = new Vector3(1f, 0.6f, 0f);
            bus.Inbound(SceneTopic, Box("cube", cubeWorld));
            scene.TryGetObject("cube", out var go);

            // The notice puts the cube 0.1 m above the hand, but the hand is still a metre away.
            LogAssert.Expect(LogType.Warning, new Regex("would move it"));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0.0, 0.0, 0.1));
            Assert.AreNotSame(hand, go.transform.parent);

            robot.transform.position = new Vector3(1f, 0f, 0f); // the hand arrives under the cube
            yield return null;
            yield return null;

            Assert.AreSame(hand, go.transform.parent);
            AssertClose(new Vector3(0f, 0.1f, 0f), go.transform.localPosition);
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

            LogAssert.Expect(LogType.Warning, new Regex("No link transform"));
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

        // MoveIt takes an attached object out of its world; that REMOVE must not delete an
        // object Unity itself owns and the robot is carrying.
        [UnityTest]
        public IEnumerator WhileAttached_ARemoveDoesNotDeleteAUnityOwnedObject_AndThePlacePoseIsApplied()
        {
            yield return Build();
            var owned = GameObject.CreatePrimitive(PrimitiveType.Cube);
            owned.transform.SetParent(host.transform, false);
            var publisher = owned.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = "owned";
            scene.RegisterUnityOwnedObject("owned", owned);

            bus.Inbound(AttachTopic, Attached("owned", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, owned.transform.parent);
            bus.Inbound(SceneTopic, new CollisionObjectMsg { id = "owned", operation = CollisionObjectMsg.REMOVE });
            yield return null;
            Assert.IsTrue(owned != null);

            // Put down: the re-ADD's pose is taken even though Unity owns the object.
            bus.Inbound(AttachTopic, Attached("owned", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.IsTrue(publisher.attachedToRobot, "Silent until the place pose arrives.");
            bus.Inbound(SceneTopic, Box("owned", new Vector3(0.5f, 0f, 0f)));
            AssertClose(new Vector3(0.5f, 0f, 0f), owned.transform.position);
            Assert.IsFalse(publisher.attachedToRobot);
            Assert.AreEqual(0, owned.transform.childCount, "The Unity-owned geometry is kept, not rebuilt.");

            // Later echoes of the Unity-owned object are skipped as before.
            bus.Inbound(SceneTopic, Box("owned", new Vector3(3f, 0f, 0f)));
            AssertClose(new Vector3(0.5f, 0f, 0f), owned.transform.position);
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

            // Later user moves publish normally again.
            go.transform.position += new Vector3(0f, 0f, 0.3f);
            yield return new WaitForSeconds(0.05f);
            Assert.Greater(bus.CountOn("/collision_object"), 0);
        }

        [UnityTest]
        public IEnumerator WhileAttached_TheObjectCannotBeGrabbed_AndItsSelectionIsDropped()
        {
            selectionHost = new GameObject("selection");
            var selection = selectionHost.AddComponent<SelectionService>();
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            GameObject child = go.transform.GetChild(0).gameObject;
            var grab = child.GetComponent<XRGrabInteractable>();
            yield return null; // the grab controller starts
            yield return null; // and reads the initial selection

            var selectable = SelectableMarker.Ensure(child, SelectionKind.Obstacle);
            selection.Select(selectable);
            Assert.IsTrue(grab.enabled, "A selected object can be grabbed.");

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.IsFalse(grab.enabled, "A carried object cannot be grabbed.");
            Assert.IsNull(selection.Current, "The selection is dropped so its verbs cannot touch the carried object.");
            selection.Select(selectable);
            Assert.IsFalse(grab.enabled, "Selecting it again does not unlock it.");

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.IsTrue(grab.enabled, "Put down and selected: grabbable again.");
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
        public IEnumerator Detach_WithoutAReAdd_ResumesAfterTheTimeout_AndRaisesReleasedOnce()
        {
            yield return Build();
            SetPrivate(attachListener, "resumeTimeout", 0.05f);
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            int released = 0;
            GameObject releasedObject = null;
            attachListener.Released += o => { released++; releasedObject = o; };

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.AreEqual(0, released, "Not released until the place pose is in or the wait is over.");
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                Assert.IsTrue(pub.attachedToRobot, "Silent while the place pose is awaited.");

            LogAssert.Expect(LogType.Warning, new Regex("No re-ADD"));
            yield return new WaitForSeconds(0.2f);

            Assert.AreEqual(1, released);
            Assert.AreSame(go, releasedObject);
            Assert.IsFalse(scene.awaitingDetachPose.Contains("cube"));
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                Assert.IsFalse(pub.attachedToRobot, "The object publishes again after the timeout.");

            // A re-ADD that still arrives moves the object but is not a second release.
            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));
            Assert.AreEqual(1, released);
        }

        [UnityTest]
        public IEnumerator Released_IsRaisedWhenThePlacePoseArrives()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            Vector3 poseAtRelease = Vector3.zero;
            int released = 0;
            attachListener.Released += o => { released++; poseAtRelease = o.transform.position; };

            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));

            Assert.AreEqual(1, released);
            AssertClose(new Vector3(0.5f, 0f, 0f), poseAtRelease);
            yield return null;
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
        public IEnumerator ExpectedDetach_ReleasesAtOnce_AndTheWatchersLateNoticesDoNotRecapture()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            Transform originalParent = go.transform.parent;

            // The executing solution reports its attach step done before the watcher says anything.
            attachListener.ApplyExpected(Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, go.transform.parent);
            Assert.IsTrue(scene.attachedIds.Contains("cube"));
            // The watcher's own attach arrives later and changes nothing.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, go.transform.parent);

            robot.transform.position = new Vector3(0f, 1f, 0f);
            Vector3 released = go.transform.position;
            attachListener.ApplyExpected(Attached("cube", "panda_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.AreSame(originalParent, go.transform.parent, "An expected detach releases the object right away.");
            AssertClose(released, go.transform.position);
            Assert.IsFalse(scene.attachedIds.Contains("cube"));

            // The watcher is behind: its attach for a step the robot already finished must not
            // put the object back into the hand; its detach and re-ADD then end the hand-off.
            robot.transform.position = new Vector3(0f, 2f, 0f);
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(originalParent, go.transform.parent, "A stale watcher attach must be ignored.");
            AssertClose(released, go.transform.position);
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.AreSame(originalParent, go.transform.parent);
            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));
            yield return null;
            AssertClose(new Vector3(0.5f, 0f, 0f), go.transform.position);
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                Assert.IsFalse(pub.attachedToRobot, "The object publishes again once the place pose is in.");

            // A later genuine pick is not mistaken for a stale notice.
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, go.transform.parent);
        }

        [UnityTest]
        public IEnumerator ForgetExpected_LetsTheNextWatcherAttachThrough()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            attachListener.ApplyExpected(Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            attachListener.ApplyExpected(Attached("cube", "panda_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));

            // The watcher never reported that hand-off; a new execution starts.
            attachListener.ForgetExpected();
            bus.Inbound(AttachTopic, Attached("cube", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, go.transform.parent, "After ForgetExpected a watcher attach is a genuine pick.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReleaseAll_PutsCarriedObjectsBackAtOnce_WithLivePublishers()
        {
            yield return Build();
            bus.Inbound(SceneTopic, Box("a", Vector3.zero));
            bus.Inbound(SceneTopic, Box("b", new Vector3(0.2f, 0f, 0f)));
            scene.TryGetObject("a", out var a);
            scene.TryGetObject("b", out var b);
            Transform originalParent = a.transform.parent;
            bus.Inbound(AttachTopic, Attached("a", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            bus.Inbound(AttachTopic, Attached("b", "fr3_hand", CollisionObjectMsg.ADD, 0, 0, 0.2));
            robot.transform.position = new Vector3(0f, 1f, 0f);
            Vector3 whereA = a.transform.position;

            Assert.AreEqual(2, attachListener.ReleaseAll());

            Assert.AreSame(originalParent, a.transform.parent);
            Assert.AreSame(originalParent, b.transform.parent);
            AssertClose(whereA, a.transform.position);
            Assert.AreEqual(0, scene.attachedIds.Count);
            Assert.AreEqual(0, scene.awaitingDetachPose.Count, "No place pose is awaited after a release-all.");
            foreach (var go in new[] { a, b })
                foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    Assert.IsFalse(pub.attachedToRobot);
            Assert.IsTrue(registry.TryGetObject("a", out var env) && env.AttachedTo == null);

            // The watcher's detach notices arrive later and find nothing to do.
            bus.Inbound(AttachTopic, Attached("a", "fr3_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            Assert.AreEqual(0, scene.awaitingDetachPose.Count);
            Assert.AreEqual(0, attachListener.ReleaseAll());
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
            Assert.IsFalse(scene.TryGetObject("cube", out _));
            Assert.IsFalse(registry.TryGetObject("cube", out _), "The mirror entry goes with the object.");
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
            registry = host.AddComponent<EnvironmentRegistry>();   // before the listener, which finds it in Awake
            scene = host.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(scene);
            scene.requestInitialPlanningScene = false;

            var ik = host.AddComponent<DirectArticulationIKController>();
            ik.enabled = false;
            SetPrivate(ik, "robotRoot", robot.transform);

            // The ROS robot names its links panda_*, the Unity robot fr3_*.
            profile = FakeRobots.ProfileWithPrefixes("fr3_", "panda_");
            robots = FakeRobots.Single(bus, ik, "", profile);

            attachListener = host.AddComponent<AttachedCollisionObjectListener>();
            attachListener.SetRobot(robots.Active);
            attachListener.Initialise(bus.Root);
            SetPrivate(attachListener, "sceneListener", scene);
            SetPrivate(attachListener, "registry", registry);
            AllowAnyAttachJump();

            yield return null; // Start subscribes to the fake bus.
        }

        // The fixtures put cubes a metre from the hand; the listener's sanity limit on the attach
        // jump (a real hand closes on the object where it is) is lifted unless a test is about it.
        private void AllowAnyAttachJump() => SetPrivate(attachListener, "maxAttachJump", 10f);

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
