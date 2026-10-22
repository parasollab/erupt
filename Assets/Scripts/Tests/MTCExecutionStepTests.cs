using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Shape;
using RosMessageTypes.StudyInterfaces;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Ros;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// While a solution executes, the trajectory player mirrors its attach/detach steps from
    /// the server's step feedback, so the carried object leaves the hand when the robot lets
    /// go, not when the planning-scene watcher eventually says so.
    /// </summary>
    public class MTCExecutionStepTests
    {
        const string k_Task = "introspection_host_42_0xbeef:3";
        const string k_TopicTask = "introspection_host_42_0xbeef";
        const string k_Statistics = "/pick_place/statistics";
        const string SceneTopic = "/collision_objects_ros";
        const string AttachTopic = "/attached_collision_objects_ros";

        FakeRosBus bus;
        GameObject host, robot;
        Transform hand;
        CollisionObjectsListenerSimple scene;
        AttachedCollisionObjectListener attachListener;
        MTCTrajectoryPlayer player;
        PickPlaceClient pickPlace;
        FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);

            robot = new GameObject("robot");
            hand = new GameObject("fr3_hand").transform;
            hand.SetParent(robot.transform, false);
            hand.localPosition = new Vector3(0f, 0.5f, 0f);

            host = new GameObject("mtc execution test");
            pickPlace = host.AddComponent<PickPlaceClient>();
            scene = host.AddComponent<CollisionObjectsListenerSimple>();
            scene.requestInitialPlanningScene = false;
            var ik = host.AddComponent<DirectArticulationIKController>();
            ik.enabled = false;
            SetPrivate(ik, "robotRoot", robot.transform);
            attachListener = host.AddComponent<AttachedCollisionObjectListener>();
            SetPrivate(attachListener, "sceneListener", scene);
            SetPrivate(attachListener, "ikController", ik);
            // The fixtures put the cube wherever; the listener's sanity limit on the attach jump
            // (a real hand closes on the object where it is) is not what these tests are about.
            SetPrivate(attachListener, "maxAttachJump", 10f);
            player = host.AddComponent<MTCTrajectoryPlayer>();
            SetPrivate(player, "ikController", ik);
            SetPrivate(player, "sceneListener", scene);
            SetPrivate(player, "attachedListener", attachListener);
            SetPrivate(player, "pickPlaceClient", pickPlace);
            yield return null; // Start() of every component

            plan = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>("/pick_place");
            execute = bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>("/execute_solution");
        }

        [TearDown]
        public void TearDown()
        {
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
        public IEnumerator StepFeedback_AttachesAndDetachesTheObjectBeforeTheWatcherReports()
        {
            yield return PlanWithSolutions(4);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", PickPlaceSolution("cube")));
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);
            Transform originalParent = go.transform.parent;

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;
            Assert.AreEqual(-1, player.ExecutionStepApplied);

            // "close hand" done: nothing to mirror yet.
            execute.Feedback(new ExecuteSolutionFeedback(0, 5, 10));
            Assert.AreSame(originalParent, go.transform.parent);
            Assert.AreEqual(0, player.ExecutionStepApplied);

            // "attach object" done: the object rides the hand at the link-relative pose.
            execute.Feedback(new ExecuteSolutionFeedback(1, 5, 11));
            Assert.AreSame(hand, go.transform.parent);
            Assert.IsTrue(scene.attachedIds.Contains("cube"));

            // Steps may be reported in a burst; everything up to the last one is applied once.
            robot.transform.position = new Vector3(0f, 1f, 0f);
            Vector3 released = go.transform.position;
            execute.Feedback(new ExecuteSolutionFeedback(3, 5, 13)); // "open hand" + "detach object"
            Assert.AreSame(originalParent, go.transform.parent, "The detach step releases the object at once.");
            AssertClose(released, go.transform.position);
            Assert.IsFalse(scene.attachedIds.Contains("cube"));
            Assert.AreEqual(3, player.ExecutionStepApplied);

            // The watcher catches up only now, while the arm retreats: no recapture, and its
            // re-ADD lands the exact place pose.
            robot.transform.position = new Vector3(0f, 2f, 0f);
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(originalParent, go.transform.parent, "A stale watcher attach must not lift the placed object.");
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));
            yield return null;
            AssertClose(new Vector3(0.5f, 0f, 0f), go.transform.position);

            accepted.Complete(RosActionGoalStatus.Succeeded,
                new ExecuteSolutionResult(true, "done", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            yield return WaitFor(run);
            Assert.AreEqual(-1, player.ExecutionStepApplied, "Tracking ends with the execution.");
        }

        [UnityTest]
        public IEnumerator StepFeedback_ArrivingBeforeTheSolution_IsAppliedOnceItIsFetched()
        {
            yield return PlanWithSolutions(4);
            // The server cannot serve the solution at first (e.g. still busy): the fetch is
            // retried on the next feedback and the steps reported so far are applied then.
            bool available = false;
            int calls = 0;
            bus.SetServiceHandler("/get_solution", request =>
            {
                calls++;
                return available
                    ? new GetSolutionResponse(true, "", PickPlaceSolution("cube"))
                    : new GetSolutionResponse(false, "not yet", new SolutionMsg());
            });
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;
            Assert.AreEqual(1, calls);
            execute.Feedback(new ExecuteSolutionFeedback(1, 5, 11));
            Assert.AreEqual(2, calls, "A failed fetch is retried when feedback arrives.");
            Assert.AreNotSame(hand, go.transform.parent, "Nothing to apply without the solution.");

            available = true;
            execute.Feedback(new ExecuteSolutionFeedback(1, 5, 11));
            Assert.AreEqual(3, calls);
            Assert.AreSame(hand, go.transform.parent, "Steps reported before the solution arrived are applied once it is here.");

            accepted.Complete(RosActionGoalStatus.Succeeded,
                new ExecuteSolutionResult(true, "done", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            yield return WaitFor(run);
        }

        // ---------- helpers ----------

        IEnumerator PlanWithSolutions(params uint[] ids)
        {
            Task goal = pickPlace.PlanAsync("cube", Pose(0.6, -0.15, 0.0));
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound(k_Statistics, PickPlaceClientTests.Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0,
                new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            yield return WaitFor(goal);
        }

        // close hand | attach object | open hand | detach object | retreat
        static SolutionMsg PickPlaceSolution(string objectId) => new SolutionMsg
        {
            task_id = k_TopicTask,
            sub_trajectory = new[]
            {
                Step(10, null),
                Step(11, Attached(objectId, "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1)),
                Step(12, null),
                Step(13, Attached(objectId, "panda_hand", CollisionObjectMsg.REMOVE, 0, 0, 0)),
                Step(14, null),
            }
        };

        static SubTrajectoryMsg Step(uint stageId, AttachedCollisionObjectMsg aco)
        {
            var step = new SubTrajectoryMsg { info = new SolutionInfoMsg { stage_id = stageId } };
            if (aco != null) step.scene_diff.robot_state.attached_collision_objects = new[] { aco };
            return step;
        }

        static void SetPrivate(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        static CollisionObjectMsg Box(string id, Vector3 unityPosition) => new CollisionObjectMsg
        {
            id = id,
            operation = CollisionObjectMsg.ADD,
            pose = new PoseMsg(new PointMsg(unityPosition.x, unityPosition.z, unityPosition.y), new QuaternionMsg(0, 0, 0, 1)),
            primitives = new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new[] { 0.1, 0.1, 0.1 } } }
        };

        static AttachedCollisionObjectMsg Attached(string id, string link, sbyte operation, double x, double y, double z)
        {
            var obj = Box(id, Vector3.zero);
            obj.operation = operation;
            obj.header.frame_id = link;
            obj.pose = new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1));
            if (operation == CollisionObjectMsg.REMOVE) obj.primitives = new SolidPrimitiveMsg[0];
            return new AttachedCollisionObjectMsg { link_name = link, @object = obj };
        }

        static PoseStampedMsg Pose(double x, double y, double z) =>
            new PoseStampedMsg(
                new RosMessageTypes.Std.HeaderMsg(new RosMessageTypes.BuiltinInterfaces.TimeMsg(), "world"),
                new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)));

        static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}");

        static IEnumerator WaitFor(Task task)
        {
            while (!task.IsCompleted) yield return null;
        }
    }
}
