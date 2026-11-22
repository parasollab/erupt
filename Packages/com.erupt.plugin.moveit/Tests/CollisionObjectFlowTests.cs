using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using Erupt.Ros;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.MoveIt;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// Core-flow tests over the ROS seam. These are the checks that could previously only
    /// be made by hand in a headset with a live ROS stack.
    /// </summary>
    public class CollisionObjectFlowTests
    {
        // The listeners use relative names; on a robot in the root namespace they go out as these.
        private const string SceneTopic = "/collision_objects_ros";
        private const string AttachTopic = "/attached_collision_objects_ros";
        private const string PlanningSceneService = "/get_planning_scene";

        private FakeRosBus bus;
        private GameObject obstacle;
        private GameObject listenerHost;
        // One planning scene, in the root namespace: what a scene with a single robot has.
        private PlanningSceneFanout fanout;
        private PlanningSceneEndpoint planningScene;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            fanout = new PlanningSceneFanout();
            planningScene = fanout.AddEndpoint(bus.Root);
        }

        [TearDown]
        public void TearDown()
        {
            if (listenerHost != null) Object.DestroyImmediate(listenerHost);
            if (obstacle != null) Object.DestroyImmediate(obstacle);
            fanout.Dispose();
            RosBus.Reset();
        }

        [Test]
        public void Seam_DefaultsToLiveConnection()
        {
            RosBus.Reset();
            Assert.IsFalse(RosBus.IsOverridden,
                "Unmodified scenes must reach the real ROSConnection.");
        }

        // Creating an obstacle must ADD it on /collision_object, or it never reaches
        // MoveIt's planning scene.
        [UnityTest]
        public IEnumerator CreatingAnObstacle_PublishesAddOnCollisionObject()
        {
            obstacle = MakeObstacle("unity_cube_test");

            yield return WaitForPublish();

            Assert.Contains("/collision_object", bus.RegisteredPublishers);
            Assert.GreaterOrEqual(bus.CountOn("/collision_object"), 1,
                "No CollisionObject was published for a newly created obstacle.");

            var msg = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.ADD, msg.operation, "First publish must be ADD (0).");
            Assert.AreEqual("unity_cube_test", msg.id);
        }

        // The publisher watches its own transform and emits MOVE on change. Undo in
        // Phase 2 has to cooperate with this rather than fight it.
        [UnityTest]
        public IEnumerator MovingAnObstacle_PublishesMove()
        {
            obstacle = MakeObstacle("unity_cube_move");

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            obstacle.transform.position += new Vector3(0.5f, 0f, 0f);
            yield return WaitForPublish();

            Assert.Greater(bus.CountOn("/collision_object"), afterAdd,
                "Moving an obstacle must publish an update.");
        }

        [UnityTest]
        public IEnumerator ScalingAnObstacle_PublishesUpdatedGeometry()
        {
            obstacle = MakeObstacle("unity_cube_scale");

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            obstacle.transform.localScale = new Vector3(2f, 3f, 4f);
            yield return WaitForPublish();

            Assert.Greater(bus.CountOn("/collision_object"), afterAdd,
                "Scale-only edits must republish geometry to MoveIt.");
            var msg = (CollisionObjectMsg)LastOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.ADD, msg.operation,
                "A scale change must replace geometry, not publish a pose-only MOVE.");
        }

        // The static provider is overridden with the same fake in SetUp, so a listener that
        // read it would show up here as a subscription.
        [UnityTest]
        public IEnumerator Listeners_StayIdleUntilTheyAreGivenABus()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            var attachListener = listenerHost.AddComponent<AttachedCollisionObjectListener>();
            yield return null; // Start of both, with no bus.

            CollectionAssert.IsEmpty(bus.Subscriptions);
            CollectionAssert.IsEmpty(bus.RegisteredServices);

            fanout.SetMirror(listener);
            attachListener.Initialise(bus.Root);

            CollectionAssert.AreEquivalent(new[] { SceneTopic, AttachTopic }, bus.Subscriptions);
            CollectionAssert.Contains(bus.RegisteredServices, PlanningSceneService);
        }

        [UnityTest]
        public IEnumerator MoveItPlugin_HandsTheContextBusToTheSceneListeners()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            listener.requestInitialPlanningScene = false;
            listenerHost.AddComponent<AttachedCollisionObjectListener>();
            var pluginGo = new GameObject("moveit");
            pluginGo.SetActive(false);
            var plugin = pluginGo.AddComponent<MoveItPlugin>();
            yield return null;

            // No robot in the context: the planning scene is mirrored in the root namespace.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No robot is registered"));
            var contextBus = new FakeRosBus();
            var context = new EruptContext { Ros = contextBus, Undo = new UndoStack() };
            try
            {
                ((IEruptPlugin)plugin).OnRegister(context);

                CollectionAssert.Contains(contextBus.Subscriptions, SceneTopic);
                CollectionAssert.Contains(contextBus.Subscriptions, AttachTopic);
                CollectionAssert.IsEmpty(bus.Subscriptions, "The listeners must use the context's bus, not the static one.");
            }
            finally
            {
                ((IEruptPlugin)plugin).OnUnregister(context);
                Object.DestroyImmediate(pluginGo);
            }
        }

        // One robot, in the root namespace and in "left": everything the MoveIt plugin and the
        // scene listeners subscribe, register and call lands under the robot's namespace.
        [UnityTest]
        public IEnumerator MoveItPlugin_UsesTheRobotsNamespace([Values("", "left")] string rosNamespace)
        {
            string ns = rosNamespace.Length == 0 ? "" : "/" + rosNamespace;
            listenerHost = new GameObject("collision-listener");
            listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            listenerHost.AddComponent<AttachedCollisionObjectListener>();
            var pluginGo = new GameObject("moveit");
            pluginGo.SetActive(false);
            var plugin = pluginGo.AddComponent<MoveItPlugin>();
            yield return null;

            var contextBus = new FakeRosBus();
            var profile = ScriptableObject.CreateInstance<Erupt.Robot.RobotProfile>();
            profile.armGroup = "arm";
            profile.trajectoryCommandTopic = "arm_controller/joint_trajectory";
            var robots = Erupt.Robot.Tests.FakeRobots.Single(contextBus, new Erupt.Robot.Tests.FakeRobotModel("j1"), rosNamespace, profile);
            var context = new EruptContext { Ros = contextBus, Robots = robots, Undo = new UndoStack() };
            try
            {
                ((IEruptPlugin)plugin).OnRegister(context);

                CollectionAssert.AreEquivalent(
                    new[] { ns + "/joint_states", ns + "/collision_objects_ros", ns + "/attached_collision_objects_ros" },
                    contextBus.Subscriptions);
                CollectionAssert.AreEquivalent(
                    new[] { ns + "/plan_kinematic_path", ns + "/query_planner_interface", ns + "/get_planning_scene" },
                    contextBus.RegisteredServices);
                CollectionAssert.AreEquivalent(
                    new[] { ns + "/arm_controller/joint_trajectory", ns + "/collision_object" },
                    contextBus.RegisteredPublishers);
                CollectionAssert.IsEmpty(bus.Subscriptions, "Nothing goes through the static bus.");
            }
            finally
            {
                ((IEruptPlugin)plugin).OnUnregister(context);
                Object.DestroyImmediate(pluginGo);
                Erupt.Robot.Tests.FakeRobots.DestroyAll(robots);
                Object.DestroyImmediate(profile);
            }
        }

        [UnityTest]
        public IEnumerator RuntimeUnityOwnedObject_WatcherEchoDoesNotBuildCoincidentGeometry()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(listener);
            listener.requestInitialPlanningScene = false;
            yield return null; // Start subscribes to the fake bus.

            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = "unity_runtime_owned";
            listener.RegisterUnityOwnedObject(publisher.objectId, obstacle);

            bus.Inbound(SceneTopic, MakeBoxCollisionObject(publisher.objectId));

            Assert.AreSame(obstacle, listener.objectsById[publisher.objectId]);
            Assert.AreEqual(0, obstacle.transform.childCount,
                "The watcher echo must not add a second renderer on top of the Unity-owned shape.");
        }

        [UnityTest]
        public IEnumerator Startup_RequestsAndAppliesExistingPlanningSceneObjects()
        {
            bus.SetServiceHandler("/get_planning_scene", _ =>
                new GetPlanningSceneResponse(new PlanningSceneMsg
                {
                    world = new PlanningSceneWorldMsg
                    {
                        collision_objects = new[] { MakeBoxCollisionObject("moveit_existing_box") }
                    }
                }));

            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(listener);
            yield return null; // Start subscribes, registers the service, and requests the snapshot.

            Assert.Contains("/get_planning_scene", bus.RegisteredServices);
            Assert.IsTrue(listener.TryGetObject("moveit_existing_box", out GameObject spawned));
            Assert.AreEqual(1, spawned.transform.childCount,
                "The full planning-scene snapshot must be rendered even when no live diff arrives.");
        }

        // Guards the destroy -> REMOVE feedback loop that suppressRemoveOnDestroy exists
        // to prevent: an inbound REMOVE must not bounce a REMOVE back into MoveIt.
        [UnityTest]
        public IEnumerator SuppressRemoveOnDestroy_StopsTheRemoveEchoingBack()
        {
            obstacle = MakeObstacle("unity_cube_suppress");
            yield return WaitForPublish();

            var publisher = obstacle.GetComponent<CollisionObjectPublisher>();
            publisher.suppressRemoveOnDestroy = true;

            bus.Clear();
            Object.DestroyImmediate(obstacle);
            obstacle = null;
            yield return null;

            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "A ROS-commanded removal must not publish REMOVE back to MoveIt.");
        }

        [UnityTest]
        public IEnumerator DeletingAnObstacle_PublishesRemove()
        {
            obstacle = MakeObstacle("unity_cube_remove");
            yield return WaitForPublish();

            bus.Clear();
            Object.DestroyImmediate(obstacle);
            obstacle = null;
            yield return null;

            Assert.AreEqual(1, bus.CountOn("/collision_object"),
                "Deleting an obstacle must publish exactly one CollisionObject.");
            var msg = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.REMOVE, msg.operation, "Delete must publish REMOVE (1).");
        }

        // --- objects mirrored from the planning scene ---------------------------------------

        [UnityTest]
        public IEnumerator MirroredObjects_GetTheGrabStack_AndAPublisherThatDoesNotReAdd()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(listener);
            listener.requestInitialPlanningScene = false;
            yield return null;

            bus.Inbound(SceneTopic, MakeBoxCollisionObject("mirrored_box"));
            bus.Inbound(SceneTopic, MakeMeshCollisionObject("mirrored_mesh"));
            Assert.IsTrue(listener.TryGetObject("mirrored_box", out GameObject box));
            Assert.IsTrue(listener.TryGetObject("mirrored_mesh", out GameObject mesh));

            GameObject boxChild = box.transform.GetChild(0).gameObject;
            Assert.AreEqual("Selectable", boxChild.tag);
            var body = boxChild.GetComponent<Rigidbody>();
            Assert.IsTrue(body.isKinematic);
            Assert.IsFalse(body.useGravity);
            Assert.IsNotNull(boxChild.GetComponent<XRGrabInteractable>());
            Assert.IsNotNull(boxChild.GetComponent<SelectableGrabController>());
            Assert.IsNotNull(boxChild.GetComponent<XRTwoHandedScaleTransformer>(), "Primitives can be scaled.");
            var boxPublisher = boxChild.GetComponent<CollisionObjectPublisher>();
            Assert.AreEqual("mirrored_box", boxPublisher.objectId);
            Assert.IsFalse(boxPublisher.isMesh);
            Assert.IsTrue(boxPublisher.hasBeenPublished, "The planning scene already holds a mirrored object.");

            GameObject meshChild = mesh.transform.GetChild(0).gameObject;
            Assert.AreEqual("Selectable", meshChild.tag);
            Assert.IsNotNull(meshChild.GetComponent<MeshCollider>(), "A mesh needs a collider to be selectable.");
            Assert.IsNotNull(meshChild.GetComponent<XRGrabInteractable>());
            Assert.IsNotNull(meshChild.GetComponent<XRGeneralGrabTransformer>());
            Assert.IsNotNull(meshChild.GetComponent<SelectableGrabController>());
            Assert.IsNull(meshChild.GetComponent<XRTwoHandedScaleTransformer>(), "Meshes are moved, not scaled.");
            var meshPublisher = meshChild.GetComponent<CollisionObjectPublisher>();
            Assert.IsTrue(meshPublisher.isMesh);
            Assert.IsTrue(meshPublisher.hasBeenPublished);

            yield return WaitForPublish();
            Assert.AreEqual(0, bus.CountOn("/collision_object"), "Mirrored objects must not be published back.");
        }

        [UnityTest]
        public IEnumerator ReAdd_WithTheSameGeometry_MovesTheObject_WithNewGeometry_RebuildsIt_AndNeitherRemovesIt()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(listener);
            listener.requestInitialPlanningScene = false;
            yield return null;

            bus.Inbound(SceneTopic, MakeBoxCollisionObject("box"));
            Assert.IsTrue(listener.TryGetObject("box", out GameObject parent));
            GameObject child = parent.transform.GetChild(0).gameObject;
            yield return WaitForPublish(); // the child's publisher is running

            bus.Inbound(SceneTopic, MakeBoxCollisionObject("box"));
            yield return null;
            Assert.IsTrue(child != null, "Unchanged geometry must not respawn the object.");

            var bigger = MakeBoxCollisionObject("box");
            bigger.primitives[0].dimensions = new[] { 2.0, 1.0, 1.0 };
            bus.Inbound(SceneTopic, bigger);
            yield return WaitForPublish();

            Assert.IsTrue(child == null, "Changed geometry is rebuilt.");
            Assert.AreEqual(1, parent.transform.childCount);
            Assert.AreEqual(0, bus.CountOn("/collision_object"),
                "Rebuilding a mirrored object must not publish a REMOVE for an id MoveIt still holds.");
        }

        [UnityTest]
        public IEnumerator ClaimedIdPrefix_AnObjectNoUnityObjectOwns_IsRemovedFromThePlanningSceneInsteadOfMirrored()
        {
            listenerHost = new GameObject("collision-listener");
            var listener = listenerHost.AddComponent<CollisionObjectsListenerSimple>();
            fanout.SetMirror(listener);
            listener.requestInitialPlanningScene = false;
            yield return null;
            listener.ClaimIdPrefix("part_");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("claimed id prefix"));
            bus.Inbound(SceneTopic, MakeBoxCollisionObject("part_7"));

            Assert.IsFalse(listener.TryGetObject("part_7", out _), "A leftover of an earlier session must not be mirrored.");
            Assert.AreEqual(1, bus.CountOn("/collision_object"));
            var remove = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.REMOVE, remove.operation);
            Assert.AreEqual("part_7", remove.id);

            // Ids outside the prefix are mirrored as usual.
            bus.Inbound(SceneTopic, MakeBoxCollisionObject("box_1"));
            Assert.IsTrue(listener.TryGetObject("box_1", out _));

            // A claimed id a live Unity object owns is left alone.
            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.objectId = "part_8";
            listener.RegisterUnityOwnedObject(publisher.objectId, obstacle);
            bus.Inbound(SceneTopic, MakeBoxCollisionObject("part_8"));
            Assert.AreEqual(1, bus.CountOn("/collision_object"), "No REMOVE for an owned object.");
            Assert.IsTrue(obstacle != null);
        }

        // --- planning-scene utilities ---------------------------------------------------------

        [Test]
        public void PublishRemove_SendsARemoveForTheId()
        {
            planningScene.PublishRemove("leftover");
            planningScene.PublishRemove("");

            Assert.Contains("/collision_object", bus.RegisteredPublishers);
            Assert.AreEqual(1, bus.CountOn("/collision_object"), "An empty id publishes nothing.");
            var msg = (CollisionObjectMsg)FirstOn("/collision_object");
            Assert.AreEqual(CollisionObjectMsg.REMOVE, msg.operation);
            Assert.AreEqual("leftover", msg.id);
            Assert.AreEqual("world", msg.header.frame_id);
        }

        [Test]
        public void DetachAllFromRobot_SendsOneRobotStateDiffThatDetachesEveryBody()
        {
            ApplyPlanningSceneRequest sent = null;
            bus.SetServiceHandler("/apply_planning_scene", request =>
            {
                sent = (ApplyPlanningSceneRequest)request;
                return new ApplyPlanningSceneResponse(true);
            });

            bool? result = null;
            planningScene.DetachAllFromRobot(ok => result = ok);

            Assert.Contains("/apply_planning_scene", bus.RegisteredServices);
            Assert.IsTrue(result);
            Assert.IsTrue(sent.scene.is_diff);
            Assert.IsTrue(sent.scene.robot_state.is_diff);
            Assert.AreEqual(1, sent.scene.robot_state.attached_collision_objects.Length);
            AttachedCollisionObjectMsg detach = sent.scene.robot_state.attached_collision_objects[0];
            Assert.AreEqual("", detach.link_name, "An empty link and an empty id address every attached body.");
            Assert.AreEqual("", detach.@object.id);
            Assert.AreEqual(CollisionObjectMsg.REMOVE, detach.@object.operation);
            Assert.IsEmpty(sent.scene.world.collision_objects, "World objects are not touched.");
            Assert.AreEqual(0, bus.CountOn("/collision_object"));
        }

        [Test]
        public void DetachAllFromRobot_ReportsFailure_WhenTheServiceRefuses_OrRosIsNotConnected()
        {
            bus.SetServiceHandler("/apply_planning_scene", _ => new ApplyPlanningSceneResponse(false));
            bool? result = null;
            planningScene.DetachAllFromRobot(ok => result = ok);
            Assert.IsFalse(result);

            bus.HasConnectionError = true;
            bus.Clear();
            result = null;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("ROS not connected"));
            planningScene.DetachAllFromRobot(ok => result = ok);
            Assert.IsFalse(result);
            Assert.IsEmpty(bus.RegisteredServices, "Nothing is sent without a connection.");
        }

        // Obstacles publish at 1 Hz by default, so a moved obstacle can take up to a
        // second to reach MoveIt. Phase 2's undo has to account for that latency.
        [UnityTest]
        public IEnumerator PublishingIsRateLimited()
        {
            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var publisher = obstacle.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = false;
            publisher.objectId = "unity_cube_rate";
            publisher.publishRateHz = 1f;

            yield return WaitForPublish();
            int afterAdd = bus.CountOn("/collision_object");

            for (int i = 0; i < 5; i++)
            {
                obstacle.transform.position += new Vector3(0.1f, 0f, 0f);
                yield return null;
            }

            Assert.AreEqual(afterAdd, bus.CountOn("/collision_object"),
                "Moves within the rate-limit window must coalesce rather than flood /collision_object.");
        }

        private static IEnumerator WaitForPublish()
        {
            // Comfortably past the 100 Hz window used in these tests.
            yield return new WaitForSeconds(0.05f);
        }

        private static GameObject MakeObstacle(string id)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            var publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = false;
            publisher.objectId = id;

            // Production default is 1 Hz (CollisionObjectPublisher.publishRateHz), which
            // would make every test wait a full second. Raised here only to keep the
            // suite fast — the rate-limiting behavior itself is asserted below.
            publisher.publishRateHz = 100f;
            return go;
        }

        private static CollisionObjectMsg MakeBoxCollisionObject(string id)
        {
            return new CollisionObjectMsg
            {
                id = id,
                operation = CollisionObjectMsg.ADD,
                primitives = new[]
                {
                    new SolidPrimitiveMsg
                    {
                        type = SolidPrimitiveMsg.BOX,
                        dimensions = new[] { 1.0, 1.0, 1.0 }
                    }
                }
            };
        }

        private static CollisionObjectMsg MakeMeshCollisionObject(string id)
        {
            return new CollisionObjectMsg
            {
                id = id,
                operation = CollisionObjectMsg.ADD,
                meshes = new[]
                {
                    new MeshMsg
                    {
                        vertices = new[]
                        {
                            new RosMessageTypes.Geometry.PointMsg(0, 0, 0),
                            new RosMessageTypes.Geometry.PointMsg(1, 0, 0),
                            new RosMessageTypes.Geometry.PointMsg(0, 1, 0),
                            new RosMessageTypes.Geometry.PointMsg(0, 0, 1)
                        },
                        triangles = new[]
                        {
                            new MeshTriangleMsg { vertex_indices = new uint[] { 0, 1, 2 } },
                            new MeshTriangleMsg { vertex_indices = new uint[] { 0, 1, 3 } },
                            new MeshTriangleMsg { vertex_indices = new uint[] { 0, 2, 3 } },
                            new MeshTriangleMsg { vertex_indices = new uint[] { 1, 2, 3 } }
                        }
                    }
                }
            };
        }

        private Unity.Robotics.ROSTCPConnector.MessageGeneration.Message FirstOn(string topic)
        {
            foreach (var m in bus.PublishedOn(topic)) return m;
            return null;
        }

        private Unity.Robotics.ROSTCPConnector.MessageGeneration.Message LastOn(string topic)
        {
            Unity.Robotics.ROSTCPConnector.MessageGeneration.Message last = null;
            foreach (var message in bus.PublishedOn(topic)) last = message;
            return last;
        }
    }
}
