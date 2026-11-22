using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Sensor;
using RosMessageTypes.Shape;
using RosMessageTypes.EruptInterfaces;
using RosMessageTypes.Trajectory;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Environment;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using GetSolutionResponse = RosMessageTypes.EruptInterfaces.GetSolutionResponse;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// While a solution executes, the solution player shows the real motion: the robot follows
    /// the live joint states, and the solution's attach/detach steps are mirrored from the
    /// server's step feedback, so the carried object leaves the hand when the robot lets go,
    /// not when the planning-scene watcher eventually says so.
    /// </summary>
    public class MtcExecutionStepTests
    {
        const string k_Task = "introspection_host_42_0xbeef:3";
        const string k_TopicTask = "introspection_host_42_0xbeef";
        const string k_Statistics = "/pick_place/statistics";
        const string SceneTopic = "/collision_objects_ros";
        const string AttachTopic = "/attached_collision_objects_ros";
        const string JointStates = "/joint_states";

        FakeRosBus bus;
        GameObject host, robotGo;
        Transform hand;
        FakeRobotModel robot;
        CollisionObjectsListenerSimple scene;
        PlanningSceneFanout fanout;
        AttachedCollisionObjectListener attachListener;
        MtcSolutionPlayer player;
        PickPlaceClient pickPlace;
        RobotRegistry robots;
        RobotProfile profile;
        FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);

            robotGo = new GameObject("robot");
            hand = new GameObject("fr3_hand").transform;
            hand.SetParent(robotGo.transform, false);
            hand.localPosition = new Vector3(0f, 0.5f, 0f);
            robot = new FakeRobotModel("fr3_joint1", "fr3_joint2") { Root = robotGo.transform, EndEffector = hand }
                .WithGripper("fr3_finger_joint1");

            host = new GameObject("mtc execution test");
            host.AddComponent<EnvironmentRegistry>();                // before the listener, which finds it in Awake
            pickPlace = host.AddComponent<PickPlaceClient>();
            pickPlace.Initialise(bus.Root);
            scene = host.AddComponent<CollisionObjectsListenerSimple>();
            fanout = new PlanningSceneFanout(scene);
            fanout.AddEndpoint(bus.Root);
            scene.requestInitialPlanningScene = false;
            // The ROS robot names its joints and links panda_*, the Unity robot fr3_*.
            profile = FakeRobots.ProfileWithPrefixes("fr3_", "panda_");
            robots = FakeRobots.Single(bus, robot, "", profile);
            attachListener = host.AddComponent<AttachedCollisionObjectListener>();
            attachListener.SetRobot(robots.Active);
            attachListener.Initialise(bus.Root);
            SetPrivate(attachListener, "sceneListener", scene);
            // The fixtures put the cube wherever; the listener's sanity limit on the attach jump
            // (a real hand closes on the object where it is) is not what these tests are about.
            SetPrivate(attachListener, "maxAttachJump", 10f);
            player = host.AddComponent<MtcSolutionPlayer>();
            player.SetRobot(robots.Active);
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
            if (robotGo != null) Object.DestroyImmediate(robotGo);
            FakeRobots.DestroyAll(robots);
            if (profile != null) Object.DestroyImmediate(profile);
            fanout.Dispose();
            RosBus.Reset();
        }

        // --- attach/detach from the step feedback -----------------------------------------

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
            robotGo.transform.position = new Vector3(0f, 1f, 0f);
            Vector3 released = go.transform.position;
            execute.Feedback(new ExecuteSolutionFeedback(3, 5, 13)); // "open hand" + "detach object"
            Assert.AreSame(originalParent, go.transform.parent, "The detach step releases the object at once.");
            AssertClose(released, go.transform.position);
            Assert.IsFalse(scene.attachedIds.Contains("cube"));
            Assert.AreEqual(3, player.ExecutionStepApplied);

            // The watcher catches up only now, while the arm retreats: no recapture, and its
            // re-ADD lands the exact place pose.
            robotGo.transform.position = new Vector3(0f, 2f, 0f);
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(originalParent, go.transform.parent, "A stale watcher attach must not lift the placed object.");
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.REMOVE, 0, 0, 0));
            bus.Inbound(SceneTopic, Box("cube", new Vector3(0.5f, 0f, 0f)));
            yield return null;
            AssertClose(new Vector3(0.5f, 0f, 0f), go.transform.position);

            yield return Finish(accepted, run);
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

            yield return Finish(accepted, run);
        }

        // The watcher may never report a hand-off that was applied from the feedback. The next
        // execution must not treat its first genuine attach notice as a leftover of that one.
        [UnityTest]
        public IEnumerator ANewExecution_ForgetsTheExpectedDetachesOfTheLastOne()
        {
            yield return PlanWithSolutions(4);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", PickPlaceSolution("cube")));
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));
            scene.TryGetObject("cube", out var go);

            Task<RosActionResult<ExecuteSolutionResult>> first = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;
            execute.Feedback(new ExecuteSolutionFeedback(4, 5, 14)); // every step, the detach included
            Assert.AreNotSame(hand, go.transform.parent);
            yield return Finish(accepted, first);

            Task<RosActionResult<ExecuteSolutionResult>> second = pickPlace.ExecuteAsync(4);
            accepted = execute.Accept();
            yield return null;
            bus.Inbound(AttachTopic, Attached("cube", "panda_hand", CollisionObjectMsg.ADD, 0, 0, 0.1));
            Assert.AreSame(hand, go.transform.parent, "The watcher's attach during a new execution is a genuine pick.");

            yield return Finish(accepted, second);
        }

        // --- live joint states ------------------------------------------------------------

        [UnityTest]
        public IEnumerator WhileExecuting_TheRobotFollowsTheLiveJointStates_AndStopsAfterwards()
        {
            yield return PlanWithSolutions(4);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", PickPlaceSolution("cube")));

            // Not executing: joint states are not followed.
            bus.Inbound(JointStates, JointState(0.9, 0.9, 0.01));
            yield return null;
            Assert.IsFalse(player.IsMirroring);
            Assert.IsEmpty(robot.Applied);

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;
            Assert.IsTrue(player.IsMirroring);

            // Several states in one frame: only the newest is applied, with ROS names mapped
            // to the Unity robot and joints it does not have left out.
            bus.Inbound(JointStates, JointState(0.1, 0.2, 0.01));
            bus.Inbound(JointStates, JointState(0.3, 0.4, 0.02));
            yield return null;
            var applied = robot.Applied[^1];
            CollectionAssert.AreEqual(new[] { "fr3_joint1", "fr3_joint2", "fr3_finger_joint1" }, applied.names);
            CollectionAssert.AreEqual(new[] { 0.3, 0.4, 0.02 }, applied.positions);
            Assert.AreEqual(1, robot.Applied.Count, "The superseded state is never applied.");

            yield return Finish(accepted, run);
            Assert.IsFalse(player.IsMirroring);
            int count = robot.Applied.Count;
            bus.Inbound(JointStates, JointState(0.7, 0.7, 0.0));
            yield return null;
            Assert.AreEqual(count, robot.Applied.Count, "The robot stays at the last state of the execution.");
        }

        [UnityTest]
        public IEnumerator StartingAnExecution_StopsARunningPreview()
        {
            yield return PlanWithSolutions(4);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", PickPlaceSolution("cube")));
            robot.Positions[0] = 0.25f;

            player.PlaySolution(new SolutionMsg { sub_trajectory = new[] { Motion("panda_joint1", 1.0, 2.0, seconds: 5) } });
            yield return null;
            Assert.IsTrue(player.IsPlaying);

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;
            Assert.IsFalse(player.IsPlaying, "The live robot replaces the preview.");
            Assert.AreEqual(0.25f, robot.Positions[0], 1e-6f, "The preview's pose is undone before the live states take over.");

            yield return Finish(accepted, run);
        }

        // The step feedback and the joint states arrive in order, but the joint states are
        // applied once per frame: the attach must be placed against the newest arm pose.
        [UnityTest]
        public IEnumerator StepFeedback_AppliesTheNewestJointStateBeforePlacingTheAttach()
        {
            yield return PlanWithSolutions(4);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", PickPlaceSolution("cube")));
            bus.Inbound(SceneTopic, Box("cube", Vector3.zero));

            Task<RosActionResult<ExecuteSolutionResult>> run = pickPlace.ExecuteAsync(4);
            var accepted = execute.Accept();
            yield return null;

            bus.Inbound(JointStates, JointState(0.5, 0.6, 0.03));
            int before = robot.Applied.Count;
            execute.Feedback(new ExecuteSolutionFeedback(1, 5, 11)); // same frame as the joint state
            Assert.Greater(robot.Applied.Count, before, "The pending joint state is applied before the attach is placed.");
            CollectionAssert.AreEqual(new[] { 0.5, 0.6, 0.03 }, robot.Applied[^1].positions);

            yield return Finish(accepted, run);
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

        static IEnumerator Finish(FakeRosBus.FakeActionGoal<ExecuteSolutionResult> accepted, Task run)
        {
            accepted.Complete(RosActionGoalStatus.Succeeded,
                new ExecuteSolutionResult(true, "done", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            yield return WaitFor(run);
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

        static SubTrajectoryMsg Motion(string joint, double from, double to, int seconds) => new SubTrajectoryMsg
        {
            trajectory = new RobotTrajectoryMsg
            {
                joint_trajectory = new JointTrajectoryMsg
                {
                    joint_names = new[] { joint },
                    points = new[]
                    {
                        new JointTrajectoryPointMsg { positions = new[] { from } },
                        new JointTrajectoryPointMsg { positions = new[] { to }, time_from_start = new DurationMsg(seconds, 0) },
                    }
                }
            }
        };

        // A ROS-side joint state: the arm and the active finger under ROS names, plus a joint
        // the Unity robot does not have.
        static JointStateMsg JointState(double joint1, double joint2, double finger) => new JointStateMsg
        {
            name = new[] { "panda_joint1", "panda_joint2", "panda_finger_joint1", "camera_pan" },
            position = new[] { joint1, joint2, finger, 1.0 }
        };

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
                new RosMessageTypes.Std.HeaderMsg(new TimeMsg(), "world"),
                new PoseMsg(new PointMsg(x, y, z), new QuaternionMsg(0, 0, 0, 1)));

        static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}");

        static IEnumerator WaitFor(Task task)
        {
            while (!task.IsCompleted) yield return null;
        }
    }
}
