using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>
    /// Unity's one environment shared with three independent planning scenes (left, right,
    /// third), each in its own namespace and its own planning frame: what goes out, what comes
    /// back, who owns what, and what a robot carrying an object changes.
    /// </summary>
    public class PlanningSceneFanoutTests
    {
        private static readonly string[] Namespaces = FakeRobots.ThreeIds;

        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private GameObject host, leftFrame, rightFrame;
        private CollisionObjectsListenerSimple mirror;
        private PlanningSceneFanout fanout;
        private readonly List<GameObject> objects = new();
        private readonly Dictionary<RobotInstance, Transform> hands = new();
        private readonly List<AttachedCollisionObjectListener> attachListeners = new();
        private readonly List<string> warnings = new();

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Warning) warnings.Add(message);
        }

        private int Warnings(string pattern) => warnings.Count(w => Regex.IsMatch(w, pattern));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            robots = FakeRobots.Three(bus);
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];

            // Three planning frames: left's is shifted, right's is shifted and turned a quarter
            // turn about the vertical, third has none of its own (Unity world space).
            leftFrame = new GameObject("left planning frame");
            leftFrame.transform.position = new Vector3(1f, 0f, 0f);
            left.SetPlanningFrame(leftFrame.transform);
            rightFrame = new GameObject("right planning frame");
            rightFrame.transform.SetPositionAndRotation(new Vector3(0f, 0f, 2f), Quaternion.Euler(0f, 90f, 0f));
            right.SetPlanningFrame(rightFrame.transform);

            // Every robot has a link called "hand": an attach must land on the right one's own.
            hands.Clear();
            foreach (RobotInstance robot in robots.All)
            {
                var hand = new GameObject("hand").transform;
                hand.SetParent(robot.transform, false);
                ((FakeRobotModel)robot.Model).Links["hand"] = hand;
                hands[robot] = hand;
            }
            left.transform.position = new Vector3(-5f, 0f, 0f);
            right.transform.position = new Vector3(5f, 0f, 0f);
            third.transform.position = new Vector3(0f, 0f, 5f);

            host = new GameObject("planning scene");
            host.AddComponent<EnvironmentRegistry>();
            mirror = host.AddComponent<CollisionObjectsListenerSimple>();
            mirror.requestInitialPlanningScene = false;
            yield return null;   // the mirror starts

            fanout = new PlanningSceneFanout(mirror);
            attachListeners.Clear();
            foreach (RobotInstance robot in robots.All)
            {
                PlanningSceneEndpoint endpoint = fanout.AddRobot(robot);
                var listener = host.AddComponent<AttachedCollisionObjectListener>();
                listener.SetRobot(robot);
                listener.Initialise(robot.Ros);
                Set(listener, "sceneListener", mirror);
                Set(listener, "maxAttachJump", 100f);
                endpoint.Attached = listener;
                attachListeners.Add(listener);
            }
            yield return null;   // the attach mirrors start
            bus.Clear();
            warnings.Clear();
            Application.logMessageReceived += OnLog;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= OnLog;
            var spawned = new List<GameObject>(mirror != null ? mirror.objectsById.Values : new Dictionary<string, GameObject>().Values);
            fanout?.Dispose();
            foreach (GameObject go in objects.Concat(spawned))
                if (go != null) Object.DestroyImmediate(go);
            objects.Clear();
            if (host != null) Object.DestroyImmediate(host);
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(leftFrame);
            Object.DestroyImmediate(rightFrame);
        }

        // ---------- Unity → every planning scene ----------

        [UnityTest]
        public IEnumerator AnObstacle_IsAddedMovedResizedAndRemoved_InEveryPlanningScene_InItsOwnFrame()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            yield return WaitForPublish();

            // ADD, once per planning scene, with the pose in that robot's frame (ROS axes: x, z-up).
            foreach (string ns in Namespaces) Assert.AreEqual(1, bus.CountOn(Topic(ns)), ns);
            AssertPose(Last("left"), 1.0, 3.0, 0.5);     // one metre closer along x
            AssertPose(Last("right"), -1.0, 2.0, 0.5);   // seen from a frame at z = 2 that looks along +x
            AssertPose(Last("third"), 2.0, 3.0, 0.5);    // Unity world space
            foreach (string ns in Namespaces)
            {
                CollisionObjectMsg add = Last(ns);
                Assert.AreEqual(CollisionObjectMsg.ADD, add.operation);
                Assert.AreEqual("crate", add.id);
                Assert.AreEqual("world", add.header.frame_id);
                Assert.AreEqual(SolidPrimitiveMsg.BOX, add.primitives[0].type);
            }
            // The right frame is turned a quarter turn, so the crate is turned the other way in it.
            Assert.AreEqual(1.0, Mathf.Abs((float)Last("third").pose.orientation.w), 1e-4);
            Assert.AreEqual(Mathf.Sqrt(0.5f), Mathf.Abs((float)Last("right").pose.orientation.w), 1e-3);

            // MOVE
            bus.Clear();
            cube.transform.position = new Vector3(2f, 0.5f, 4f);
            yield return WaitForPublish();
            foreach (string ns in Namespaces)
            {
                Assert.AreEqual(1, bus.CountOn(Topic(ns)), ns);
                Assert.AreEqual(CollisionObjectMsg.MOVE, Last(ns).operation);
            }
            AssertPose(Last("left"), 1.0, 4.0, 0.5);
            AssertPose(Last("right"), -2.0, 2.0, 0.5);
            AssertPose(Last("third"), 2.0, 4.0, 0.5);

            // Resize: geometry again.
            bus.Clear();
            cube.transform.localScale = new Vector3(2f, 1f, 1f);
            yield return WaitForPublish();
            foreach (string ns in Namespaces)
            {
                Assert.AreEqual(CollisionObjectMsg.ADD, Last(ns).operation, ns);
                Assert.AreEqual(2.0, Last(ns).primitives[0].dimensions[0], 1e-6);
            }

            // Remove
            bus.Clear();
            Object.DestroyImmediate(cube);
            foreach (string ns in Namespaces)
            {
                Assert.AreEqual(1, bus.CountOn(Topic(ns)), ns);
                Assert.AreEqual(CollisionObjectMsg.REMOVE, Last(ns).operation);
                Assert.AreEqual("crate", Last(ns).id);
            }
        }

        [UnityTest]
        public IEnumerator Echoes_AreDropped_AndChangeNothing()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();
            Vector3 before = cube.transform.position;
            var sent = Namespaces.ToDictionary(ns => ns, Last);
            bus.Clear();

            // Every planning scene echoes what it was sent (MoveIt fills in the shape poses).
            foreach (string ns in Namespaces)
            {
                CollisionObjectMsg echo = Copy(sent[ns]);
                echo.primitive_poses = new[] { new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1)) };
                bus.Inbound(Scene(ns), echo);
            }
            yield return WaitForPublish();

            Assert.AreEqual(0, bus.Publishes.Count, "An echo is not answered, forwarded or applied.");
            Assert.AreEqual(before, cube.transform.position);
            Assert.AreEqual(0, cube.transform.childCount);
            Assert.AreEqual(0, Warnings("reports a change"), "An echo is not a conflict.");
        }

        [UnityTest]
        public IEnumerator AChangeToAUnityOwnedObject_FromAPlanningScene_IsIgnored_WithOneWarning()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();
            bus.Clear();

            bus.Inbound(Scene("right"), Box("crate", 9, 9, 9));
            bus.Inbound(Scene("right"), Box("crate", 8, 8, 8));
            bus.Inbound(Scene("right"), Box("crate", 7, 7, 7));

            Assert.AreEqual(new Vector3(2f, 0.5f, 3f), cube.transform.position);
            Assert.AreEqual(0, bus.Publishes.Count);
            Assert.AreEqual(1, Warnings("Planning scene 'right' reports a change to 'crate', but Unity owns it"),
                "Said once, not for every message.");
        }

        // ---------- a planning scene → Unity and the other planning scenes ----------

        [UnityTest]
        public IEnumerator AnObjectFromOnePlanningScene_IsAdoptedOnce_AndSentToTheOtherTwo()
        {
            // In left's frame (origin at Unity x = 1): ROS (1, 2, 0.5) is Unity (2, 0.5, 2).
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));

            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject pallet));
            AssertVector(new Vector3(2f, 0.5f, 2f), pallet.transform.position);
            Assert.AreEqual(1, pallet.transform.childCount);

            Assert.AreEqual(0, bus.CountOn(Topic("left")), "Nothing goes back to where it came from.");
            Assert.AreEqual(1, bus.CountOn(Topic("right")));
            Assert.AreEqual(1, bus.CountOn(Topic("third")));
            Assert.AreEqual(CollisionObjectMsg.ADD, Last("right").operation);
            Assert.AreEqual(1, Last("right").primitives.Length);
            AssertPose(Last("third"), 2.0, 2.0, 0.5);
            AssertPose(Last("right"), 0.0, 2.0, 0.5);

            // The others echo it; the object stays one object and nothing more is sent.
            bus.Inbound(Scene("right"), Copy(Last("right")));
            bus.Inbound(Scene("third"), Copy(Last("third")));
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            yield return WaitForPublish();

            Assert.AreEqual(2, bus.Publishes.Count);
            Assert.AreSame(pallet, mirror.objectsById["pallet"]);
            Assert.AreEqual(1, pallet.transform.childCount, "One Unity object per id.");
            Assert.AreEqual(0, Warnings("reports a change"));
        }

        [UnityTest]
        public IEnumerator AChangeFromTheOwner_IsAppliedAndForwarded_OneFromAnotherIsIgnoredOnce()
        {
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            mirror.TryGetObject("pallet", out GameObject pallet);
            yield return null;
            bus.Clear();

            // The owner moves it.
            bus.Inbound(Scene("left"), Box("pallet", 2, 2, 0.5, CollisionObjectMsg.MOVE));
            AssertVector(new Vector3(3f, 0.5f, 2f), pallet.transform.position);
            Assert.AreEqual(0, bus.CountOn(Topic("left")));
            Assert.AreEqual(CollisionObjectMsg.MOVE, Last("right").operation);
            AssertPose(Last("third"), 3.0, 2.0, 0.5);

            // A planning scene that does not own it tries to move it.
            bus.Clear();
            bus.Inbound(Scene("third"), Box("pallet", 9, 9, 9, CollisionObjectMsg.MOVE));
            bus.Inbound(Scene("third"), Box("pallet", 8, 8, 8, CollisionObjectMsg.MOVE));
            AssertVector(new Vector3(3f, 0.5f, 2f), pallet.transform.position);
            Assert.AreEqual(0, bus.Publishes.Count);
            Assert.AreEqual(1, Warnings("Planning scene 'third' reports a change to 'pallet', but planning scene 'left' owns it"));
        }

        [UnityTest]
        public IEnumerator MovingAnAdoptedObjectInUnity_TellsEveryPlanningScene_AndTheOwnersEchoIsDropped()
        {
            bus.Inbound(Scene("right"), Box("pallet", 0, 2, 0.5));
            mirror.TryGetObject("pallet", out GameObject pallet);
            GameObject child = pallet.transform.GetChild(0).gameObject;
            child.GetComponent<CollisionObjectPublisher>().publishRateHz = 100f;
            yield return null;
            bus.Clear();

            child.transform.position += new Vector3(0f, 0f, 1f);
            yield return WaitForPublish();

            foreach (string ns in Namespaces)
            {
                Assert.AreEqual(1, bus.CountOn(Topic(ns)), ns);
                Assert.AreEqual(CollisionObjectMsg.MOVE, Last(ns).operation);
            }
            Vector3 moved = child.transform.position;
            CollisionObjectMsg toOwner = Copy(Last("right"));
            bus.Clear();
            bus.Inbound(Scene("right"), toOwner);
            Assert.AreEqual(0, bus.Publishes.Count);
            AssertVector(moved, child.transform.position);
        }

        [UnityTest]
        public IEnumerator ARemoveFromOnePlanningScene_RemovesTheObject_AndIsNotSentBack()
        {
            // A Unity-owned object removed on the ROS side of one robot.
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            // And an object another planning scene owns.
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            mirror.TryGetObject("pallet", out GameObject pallet);
            yield return WaitForPublish();
            bus.Clear();

            bus.Inbound(Scene("third"), Remove("crate"));
            yield return null;
            Assert.IsTrue(cube == null, "The Unity object goes.");
            Assert.AreEqual(0, bus.CountOn(Topic("third")), "Not back to its origin.");
            Assert.AreEqual(CollisionObjectMsg.REMOVE, Last("left").operation);
            Assert.AreEqual(CollisionObjectMsg.REMOVE, Last("right").operation);
            Assert.AreEqual(2, bus.Publishes.Count);

            bus.Clear();
            bus.Inbound(Scene("left"), Remove("pallet"));
            yield return null;
            Assert.IsTrue(pallet == null);
            Assert.AreEqual(0, bus.CountOn(Topic("left")));
            Assert.AreEqual(1, bus.CountOn(Topic("right")));
            Assert.AreEqual(1, bus.CountOn(Topic("third")));

            // The echoes of those REMOVEs change nothing.
            bus.Clear();
            bus.Inbound(Scene("right"), Remove("pallet"));
            bus.Inbound(Scene("third"), Remove("pallet"));
            bus.Inbound(Scene("left"), Remove("crate"));
            Assert.AreEqual(0, bus.Publishes.Count);
        }

        [UnityTest]
        public IEnumerator DeletingAnObjectInUnity_RemovesItEverywhere_AndTheEchoesAreDropped()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();
            bus.Clear();

            mirror.UnregisterUnityOwnedObject("crate");
            Object.DestroyImmediate(cube);
            foreach (string ns in Namespaces) Assert.AreEqual(CollisionObjectMsg.REMOVE, Last(ns).operation, ns);

            bus.Clear();
            foreach (string ns in Namespaces) bus.Inbound(Scene(ns), Remove("crate"));
            Assert.AreEqual(0, bus.Publishes.Count);
            Assert.IsFalse(mirror.TryGetObject("crate", out _));
        }

        // ---------- attach ----------

        [UnityTest]
        public IEnumerator AnAttach_ParentsTheObjectUnderTheLinkOfTheRobotThatAttachedIt_AndTheOthersSeeItMove()
        {
            GameObject cube = MakeObstacle("part", new Vector3(5f, 0f, 0f));
            mirror.RegisterUnityOwnedObject("part", cube);
            yield return WaitForPublish();
            bus.Clear();

            bus.Inbound(Attach("right"), Attached("part", "hand", CollisionObjectMsg.ADD));

            Assert.AreSame(hands[right], cube.transform.parent, "The link is looked up on the robot whose MoveIt attached it.");
            Assert.IsTrue(mirror.attachedIds.Contains("part"));

            // The carry: right's hand moves. Its own planning scene hears nothing; the others do.
            right.transform.position += new Vector3(0f, 0f, 1f);
            yield return WaitForPublish();
            Assert.AreEqual(0, bus.CountOn(Topic("right")), "The carrying robot's planning scene has it as an attached body.");
            Assert.GreaterOrEqual(bus.CountOn(Topic("left")), 1);
            Assert.GreaterOrEqual(bus.CountOn(Topic("third")), 1);
            Assert.AreEqual(CollisionObjectMsg.MOVE, Last("left").operation);
            AssertPose(Last("third"), cube.transform.position.x, cube.transform.position.z, cube.transform.position.y);

            // While carried, nothing a planning scene says about it is applied.
            Vector3 carried = cube.transform.position;
            bus.Inbound(Scene("left"), Box("part", 9, 9, 9));
            bus.Inbound(Scene("right"), Remove("part"));
            AssertVector(carried, cube.transform.position);
            Assert.IsTrue(cube != null);

            // Put down: right's detach, then right's re-ADD with the place pose (in right's frame).
            CollisionObjectMsg lastToLeft = Last("left");
            bus.Clear();
            bus.Inbound(Attach("right"), Attached("part", "hand", CollisionObjectMsg.REMOVE));
            Assert.AreNotSame(hands[right], cube.transform.parent);
            // Left's echo of the carry arriving first is not the place pose.
            Vector3 putDown = cube.transform.position;
            bus.Inbound(Scene("left"), Copy(lastToLeft));
            Assert.IsTrue(mirror.awaitingDetachPose.Contains("part"));
            AssertVector(putDown, cube.transform.position);

            // Right's frame is at Unity z = 2 looking along +x: ROS (1, 2, 0.25) there is Unity (2, 0.25, 1).
            bus.Clear();
            bus.Inbound(Scene("right"), Box("part", 1, 2, 0.25));
            AssertVector(new Vector3(2f, 0.25f, 1f), cube.transform.position);
            Assert.AreEqual(0, bus.CountOn(Topic("right")), "The place pose is right's own; it is not sent back.");
            Assert.AreEqual(1, bus.CountOn(Topic("left")));
            Assert.AreEqual(1, bus.CountOn(Topic("third")));
            AssertPose(Last("third"), 2.0, 1.0, 0.25);
            AssertPose(Last("left"), 1.0, 1.0, 0.25);
            Assert.IsFalse(cube.GetComponent<CollisionObjectPublisher>().attachedToRobot);
        }

        [UnityTest]
        public IEnumerator AnAttachNamingALinkTheRobotLacks_DoesNotFallBackOnAnotherRobot()
        {
            GameObject cube = MakeObstacle("part", new Vector3(5f, 0f, 0f));
            mirror.RegisterUnityOwnedObject("part", cube);
            yield return WaitForPublish();
            // Only left has a link by this name.
            var gripper = new GameObject("gripper").transform;
            gripper.SetParent(left.transform, false);
            ((FakeRobotModel)left.Model).Links["gripper"] = gripper;
            Transform parentBefore = cube.transform.parent;

            LogAssert.Expect(LogType.Warning, new Regex("No link transform for 'gripper'"));
            bus.Inbound(Attach("third"), Attached("part", "gripper", CollisionObjectMsg.ADD));

            Assert.AreSame(parentBefore, cube.transform.parent, "Never under another robot's link.");
        }

        // ---------- robots coming and going ----------

        [UnityTest]
        public IEnumerator ARobotAddedAtRuntime_ReceivesTheEnvironment_AndARemovedOneGetsNoMoreTraffic()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            yield return WaitForPublish();
            bus.Clear();

            RobotInstance fourth = FakeRobots.Add(robots, "fourth", "fourth", new FakeRobotModel());
            fanout.AddRobot(fourth);
            yield return WaitForPublish();

            CollectionAssert.Contains(bus.RegisteredPublishers, "/fourth/collision_object");
            CollectionAssert.Contains(bus.Subscriptions, "/fourth/collision_objects_ros");
            var received = bus.PublishedOn(Topic("fourth")).Cast<CollisionObjectMsg>().ToList();
            CollectionAssert.AreEquivalent(new[] { "crate", "pallet" }, received.Select(m => m.id));
            Assert.IsTrue(received.All(m => m.operation == CollisionObjectMsg.ADD && m.primitives.Length == 1),
                "A planning scene that joins gets every object with its geometry.");
            foreach (string ns in Namespaces) Assert.AreEqual(0, bus.CountOn(Topic(ns)), "The others are told nothing new.");

            // Right leaves: nothing more goes to its namespace, and the rest carry on.
            bus.Clear();
            fanout.RemoveRobot(right);
            cube.transform.position += new Vector3(0f, 0f, 1f);
            yield return WaitForPublish();
            Assert.AreEqual(0, bus.CountOn(Topic("right")));
            Assert.AreEqual(1, bus.CountOn(Topic("left")));
            Assert.AreEqual(1, bus.CountOn(Topic("third")));
            Assert.AreEqual(1, bus.CountOn(Topic("fourth")));

            bus.Clear();
            bus.Inbound(Scene("right"), Box("ghost", 0, 0, 0));
            Assert.IsFalse(mirror.TryGetObject("ghost", out _), "A planning scene that left is not listened to.");
        }

        // ---------- one robot, empty namespace: the wire is what it always was ----------

        [UnityTest]
        public IEnumerator OneRobot_InTheRootNamespace_SendsTheSameMessagesOnTheSameTopicAsASinglePlanningScene()
        {
            fanout.Dispose();
            var soloBus = new FakeRosBus();
            RobotRegistry solo = FakeRobots.Single(soloBus, new FakeRobotModel());
            fanout = new PlanningSceneFanout(mirror);
            PlanningSceneEndpoint endpoint = fanout.AddRobot(solo.Active);
            try
            {
                CollectionAssert.AreEqual(new[] { "/collision_object" }, soloBus.RegisteredPublishers);
                CollectionAssert.AreEqual(new[] { "/collision_objects_ros" }, soloBus.Subscriptions);

                GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
                yield return WaitForPublish();
                cube.transform.position = new Vector3(2f, 0.5f, 4f);
                yield return WaitForPublish();
                cube.transform.localScale = new Vector3(2f, 1f, 1f);
                yield return WaitForPublish();
                soloBus.Inbound("/collision_objects_ros", Box("pallet", 1, 2, 0.5));
                Object.DestroyImmediate(cube);

                var sent = soloBus.Publishes.Select(p => (p.Topic, ((CollisionObjectMsg)p.Message).operation, ((CollisionObjectMsg)p.Message).id)).ToList();
                CollectionAssert.AreEqual(new[]
                {
                    ("/collision_object", CollisionObjectMsg.ADD, "crate"),
                    ("/collision_object", CollisionObjectMsg.MOVE, "crate"),
                    ("/collision_object", CollisionObjectMsg.ADD, "crate"),
                    ("/collision_object", CollisionObjectMsg.REMOVE, "crate")
                }, sent, "ADD, MOVE, ADD (resize), REMOVE on /collision_object; the mirrored object is not sent back.");

                var first = (CollisionObjectMsg)soloBus.Publishes[0].Message;
                Assert.AreEqual("world", first.header.frame_id);
                AssertPose(first, 2.0, 3.0, 0.5);
                Assert.IsTrue(first.primitive_poses == null || first.primitive_poses.Length == 0,
                    "A single primitive carries no shape pose, as before.");
                Assert.AreEqual("", endpoint.Namespace);
            }
            finally
            {
                fanout.Dispose();
                fanout = null;
                FakeRobots.DestroyAll(solo);
            }
        }

        // ---------- helpers ----------

        // ---------- found against a real ROS stack (Assets/Erupt.App/Tests/Live) ----------

        // MoveIt keeps and reports its world in its own planning frame, the root link of the robot
        // model, whatever frame the objects were sent in. A robot whose base is not at the origin
        // of the frame Unity addresses it in ("world") therefore gets its poses back shifted.
        private Transform GiveBaseLink(RobotInstance robot, Vector3 position, float rosYawDegrees)
        {
            var link = new GameObject("base").transform;
            link.SetParent(robot.transform, false);
            // An imported robot looks along its local z: a ROS yaw of psi is a Unity yaw of 90 - psi.
            link.SetPositionAndRotation(position, Quaternion.Euler(0f, 90f - rosYawDegrees, 0f));
            ((FakeRobotModel)robot.Model).Links["base"] = link;
            return link;
        }

        // A Unity world pose as a ROS pose in the frame of a link (the scene's world convention).
        private static PoseMsg InLink(Transform link, Vector3 position, Quaternion rotation)
        {
            Quaternion frame = AttachedObjectPlacement.WorldConventionRotation(link);
            Vector3 p = Quaternion.Inverse(frame) * (position - link.position);
            Quaternion q = Quaternion.Inverse(frame) * rotation;
            return new PoseMsg(new PointMsg(p.x, p.z, p.y), new QuaternionMsg(-q.x, -q.z, -q.y, q.w));
        }

        [UnityTest]
        public IEnumerator TheEchoOfAUnityObject_ReportedInTheRobotsBaseFrame_IsStillAnEcho()
        {
            Transform baseLink = GiveBaseLink(left, new Vector3(0.4f, 0f, 1.6f), rosYawDegrees: 180f);
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            cube.transform.rotation = Quaternion.Euler(10f, 40f, -25f);
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();
            CollisionObjectMsg sent = Last("left");
            Assert.AreEqual("world", sent.header.frame_id);
            bus.Clear();

            // What left's MoveIt says back: the same object, in its base frame.
            CollisionObjectMsg echo = Copy(sent);
            echo.header = new HeaderMsg { frame_id = "base" };
            echo.pose = InLink(baseLink, cube.transform.position, cube.transform.rotation);
            Assert.Greater(Vector3.Distance(
                new Vector3((float)echo.pose.position.x, (float)echo.pose.position.y, (float)echo.pose.position.z),
                new Vector3((float)sent.pose.position.x, (float)sent.pose.position.y, (float)sent.pose.position.z)), 0.5f,
                "The two frames differ: the numbers alone would not match.");
            bus.Inbound(Scene("left"), echo);
            yield return null;

            Assert.AreEqual(0, Warnings("reports a change"), "The echo is recognised once its frame is taken into account.");
            Assert.AreEqual(0, bus.Publishes.Count, "An echo is answered with nothing.");
            AssertVector(new Vector3(2f, 0.5f, 3f), cube.transform.position);

            // The same numbers claimed to be in the planning frame are a different pose: a conflict.
            echo.header = new HeaderMsg { frame_id = "world" };
            bus.Inbound(Scene("left"), echo);
            Assert.AreEqual(1, Warnings("reports a change to 'crate'"));
        }

        [UnityTest]
        public IEnumerator AnObjectOfTheRosSide_ReportedInTheRobotsBaseFrame_LandsWhereItIs_AndIsForwardedInTheOthersFrames()
        {
            Transform baseLink = GiveBaseLink(left, new Vector3(0.4f, 0f, 1.6f), rosYawDegrees: 90f);
            var where = new Vector3(3f, 0.25f, -1f);
            Quaternion turned = Quaternion.Euler(0f, 30f, 0f);

            CollisionObjectMsg pallet = Box("pallet", 0, 0, 0);
            pallet.header = new HeaderMsg { frame_id = "base" };
            pallet.pose = InLink(baseLink, where, turned);
            bus.Inbound(Scene("left"), pallet);
            yield return WaitForPublish();

            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject go));
            AssertVector(where, go.transform.position);
            Assert.Less(Quaternion.Angle(turned, go.transform.rotation), 0.05f);

            // third plans in Unity world space: ROS (x, y, z) = Unity (x, z, y).
            AssertPose(Last("third"), where.x, where.z, where.y);
            Assert.AreEqual("world", Last("third").header.frame_id);
            Assert.AreEqual(0, bus.CountOn(Topic("left")), "Not back to where it came from.");
            Assert.AreEqual(0, Warnings("reports poses in frame"));
        }

        [UnityTest]
        public IEnumerator APoseInAFrameThatIsNeitherThePlanningFrameNorALink_IsReadInThePlanningFrame_AndSaidOnce()
        {
            CollisionObjectMsg pallet = Box("pallet", 1, 2, 0.5);
            pallet.header = new HeaderMsg { frame_id = "map" };
            bus.Inbound(Scene("third"), pallet);
            pallet = Box("pallet", 1, 2.5, 0.5);
            pallet.header = new HeaderMsg { frame_id = "map" };
            bus.Inbound(Scene("third"), pallet);
            yield return null;

            Assert.AreEqual(1, Warnings("reports poses in frame 'map'"));
            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject go));
            AssertVector(new Vector3(1f, 0.5f, 2.5f), go.transform.position);
        }

        [UnityTest]
        public IEnumerator WhenItsSceneIsUnloaded_AUnityObjectLeavesEveryPlanningSceneOnce_AndItsEchoDoesNotDeleteItsSuccessor()
        {
            // The scene that is about to be reset holds the crate.
            UnityEngine.SceneManagement.Scene outgoing = UnityEngine.SceneManagement.SceneManager.CreateScene("outgoing " + System.Guid.NewGuid().ToString("N"));
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cube, outgoing);
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();
            bus.Clear();

            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(outgoing);
            objects.Remove(cube);
            foreach (string ns in Namespaces)
            {
                Assert.AreEqual(1, bus.CountOn(Topic(ns)), $"One message to '{ns}'.");
                Assert.AreEqual(CollisionObjectMsg.REMOVE, Last(ns).operation);
            }
            mirror.UnregisterUnityOwnedObject("crate");

            // The next scene publishes its own crate under the same id, with a fan-out that never
            // sent a REMOVE; the old one's echo arrives now.
            fanout.Dispose();
            fanout = new PlanningSceneFanout(mirror);
            foreach (RobotInstance robot in robots.All) fanout.AddRobot(robot);
            GameObject successor = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", successor);
            yield return WaitForPublish();
            bus.Clear();

            bus.Inbound(Scene("left"), Remove("crate"));
            yield return null;
            Assert.IsTrue(successor != null, "The echo of the outgoing scene's REMOVE is not a removal made on the ROS side.");
            Assert.AreEqual(0, bus.Publishes.Count);

            // A second REMOVE from the same planning scene is not an echo of anything.
            bus.Inbound(Scene("left"), Remove("crate"));
            yield return null;
            Assert.IsTrue(successor == null, "A REMOVE made on the ROS side still removes the object.");
        }

        [UnityTest]
        public IEnumerator LeavingAPlanningScene_TakesUnitysObjectsOutOfIt_AndLeavesTheRosSidesOwn()
        {
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            bus.Inbound(Scene("right"), Box("pallet", 1, 2, 0.5));   // right's own object, forwarded to left and third
            yield return WaitForPublish();
            bus.Clear();

            fanout.Leave(fanout.EndpointFor(left));
            CollisionObjectMsg[] toLeft = bus.PublishedOn(Topic("left")).Cast<CollisionObjectMsg>().ToArray();
            Assert.AreEqual(1, toLeft.Length, "Only what Unity put there is taken out.");
            Assert.AreEqual("crate", toLeft[0].id);
            Assert.AreEqual(CollisionObjectMsg.REMOVE, toLeft[0].operation);
            Assert.IsNull(fanout.EndpointFor(left), "The planning scene is no longer shared with.");
            Assert.AreEqual(1, bus.Publishes.Count, "The other planning scenes are not touched.");
        }

        [UnityTest]
        public IEnumerator Resync_SendsEveryUnityObjectAgainInFull_AndTakesThePlanningScenesWordForItsOwnObjects()
        {
            mirror.requestInitialPlanningScene = true;
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            bus.Inbound(Scene("left"), Box("barrel", 3, 2, 0.5));
            yield return WaitForPublish();
            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject pallet));
            Assert.IsTrue(mirror.TryGetObject("barrel", out GameObject barrel));

            // ROS was away. When it is back, left's MoveIt still has the pallet; the barrel is gone.
            foreach (string ns in Namespaces)
            {
                string scene = ns;
                bus.SetServiceHandler($"/{ns}/get_planning_scene", _ => new GetPlanningSceneResponse
                {
                    scene = new PlanningSceneMsg
                    {
                        robot_model_name = "fake",
                        world = new PlanningSceneWorldMsg
                        {
                            collision_objects = scene == "left" ? new[] { Box("pallet", 1, 2, 0.5) } : new CollisionObjectMsg[0]
                        }
                    }
                });
            }
            bus.Clear();
            fanout.Resync();
            yield return null;

            foreach (string ns in Namespaces)
            {
                CollisionObjectMsg crate = bus.PublishedOn(Topic(ns)).Cast<CollisionObjectMsg>().LastOrDefault(m => m.id == "crate");
                Assert.IsNotNull(crate, $"'{ns}' is told about the crate again.");
                Assert.AreEqual(CollisionObjectMsg.ADD, crate.operation);
                Assert.AreEqual(1, crate.primitives.Length, "With its geometry: a restarted MoveIt has forgotten it.");
            }
            Assert.IsTrue(pallet != null, "What its planning scene still holds stays.");
            Assert.IsTrue(barrel == null, "What its planning scene no longer holds goes from Unity.");
            Assert.IsTrue(bus.PublishedOn(Topic("right")).Cast<CollisionObjectMsg>().Any(m => m.id == "barrel" && m.operation == CollisionObjectMsg.REMOVE),
                "...and from the planning scenes it had been forwarded to.");
            Assert.IsTrue(cube != null);
        }

        // The answer to "what do you hold?" takes a moment. An obstacle the user deletes in that
        // moment is still on the list, and used to come back as an object of the ROS side that
        // the ROS side no longer had.
        [UnityTest]
        public IEnumerator Resync_DoesNotBringBack_AnObjectDeletedWhileThePlanningSceneWasAnswering()
        {
            mirror.requestInitialPlanningScene = true;
            GameObject cube = MakeObstacle("crate", new Vector3(2f, 0.5f, 3f));
            mirror.RegisterUnityOwnedObject("crate", cube);
            yield return WaitForPublish();

            foreach (string ns in Namespaces)
            {
                string scene = ns;
                bus.SetServiceHandler($"/{ns}/get_planning_scene", _ =>
                {
                    // The last planning scene has been asked and has not answered yet: the user
                    // deletes the crate now. Its answer still lists it.
                    if (scene == Namespaces[Namespaces.Length - 1] && cube != null) Object.DestroyImmediate(cube);
                    return new GetPlanningSceneResponse
                    {
                        scene = new PlanningSceneMsg
                        {
                            robot_model_name = "fake",
                            world = new PlanningSceneWorldMsg { collision_objects = new[] { Box("crate", 2, 3, 0.5) } }
                        }
                    };
                });
            }
            bus.Clear();
            fanout.Resync();
            yield return null;

            Assert.IsTrue(cube == null);
            Assert.IsFalse(mirror.TryGetObject("crate", out GameObject back) && back != null, "The crate is gone and stays gone.");
            foreach (string ns in Namespaces)
            {
                CollisionObjectMsg last = bus.PublishedOn(Topic(ns)).Cast<CollisionObjectMsg>().LastOrDefault(m => m.id == "crate");
                Assert.IsNotNull(last, $"'{ns}' heard about the crate.");
                Assert.AreEqual(CollisionObjectMsg.REMOVE, last.operation, $"The last word to '{ns}' is that the crate is gone.");
            }
        }

        [UnityTest]
        public IEnumerator AnEmptyAnswer_ToARequestTheEndpointCouldNotDeliver_IsNotAnEmptyPlanningScene()
        {
            mirror.requestInitialPlanningScene = true;
            bus.Inbound(Scene("left"), Box("pallet", 1, 2, 0.5));
            yield return WaitForPublish();
            Assert.IsTrue(mirror.TryGetObject("pallet", out GameObject pallet));

            // The connector hands an undelivered request back as a default message: no objects,
            // and no robot model name either, which MoveIt would never leave out.
            foreach (string ns in Namespaces)
                bus.SetServiceHandler($"/{ns}/get_planning_scene", _ => new GetPlanningSceneResponse());
            fanout.Resync();
            yield return null;

            Assert.IsTrue(pallet != null, "Nothing is removed on the strength of an answer that never came.");
            Assert.AreEqual(3, Warnings("did not answer"));
        }

        private static string Topic(string ns) => $"/{ns}/collision_object";
        private static string Scene(string ns) => $"/{ns}/collision_objects_ros";
        private static string Attach(string ns) => $"/{ns}/attached_collision_objects_ros";

        private CollisionObjectMsg Last(string ns)
        {
            CollisionObjectMsg last = null;
            foreach (var message in bus.PublishedOn(Topic(ns))) last = (CollisionObjectMsg)message;
            return last;
        }

        private static IEnumerator WaitForPublish()
        {
            yield return new WaitForSeconds(0.05f);
        }

        private GameObject MakeObstacle(string id, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.position = position;
            var publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = id;
            publisher.publishRateHz = 100f;
            objects.Add(go);
            return go;
        }

        // A box at a pose given in ROS axes (x, y, z-up) of the planning frame it is reported in.
        private static CollisionObjectMsg Box(string id, double x, double y, double z, sbyte operation = CollisionObjectMsg.ADD) =>
            new CollisionObjectMsg
            {
                id = id,
                operation = operation,
                pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)),
                primitives = operation == CollisionObjectMsg.MOVE
                    ? new SolidPrimitiveMsg[0]
                    : new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.2, 0.2, 0.2 } } }
            };

        private static CollisionObjectMsg Remove(string id) => new CollisionObjectMsg { id = id, operation = CollisionObjectMsg.REMOVE };

        private static CollisionObjectMsg Copy(CollisionObjectMsg source) => new CollisionObjectMsg
        {
            id = source.id,
            header = source.header,
            operation = source.operation,
            pose = source.pose,
            primitives = source.primitives,
            primitive_poses = source.primitive_poses,
            meshes = source.meshes,
            mesh_poses = source.mesh_poses
        };

        private static AttachedCollisionObjectMsg Attached(string id, string link, sbyte operation)
        {
            var obj = Box(id, 0, 0, 0, operation == CollisionObjectMsg.REMOVE ? CollisionObjectMsg.MOVE : CollisionObjectMsg.ADD);
            obj.operation = operation;
            obj.header.frame_id = link;
            return new AttachedCollisionObjectMsg { link_name = link, @object = obj };
        }

        private static void AssertPose(CollisionObjectMsg msg, double x, double y, double z)
        {
            Assert.IsNotNull(msg, "No message.");
            Assert.AreEqual(x, msg.pose.position.x, 1e-4, "x");
            Assert.AreEqual(y, msg.pose.position.y, 1e-4, "y");
            Assert.AreEqual(z, msg.pose.position.z, 1e-4, "z");
        }

        private static void AssertVector(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-4f, $"expected {expected}, was {actual}");

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(target, value);
    }
}
