using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.EruptInterfaces;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;
using GetSolutionRequest = RosMessageTypes.EruptInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.EruptInterfaces.GetSolutionResponse;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>
    /// MTC solutions against the robot's joint limits: every step of a solution is checked when
    /// the solution is fetched, a solution that leaves the limits previews clamped and is not
    /// sent to execute_solution unless the plugin allows it, and joint states mirrored during
    /// execution raise one mismatch refusal per joint.
    /// </summary>
    public class MtcJointLimitTests
    {
        const string k_Task = "introspection_host_42_0xbeef:1";
        const string k_TopicTask = "introspection_host_42_0xbeef";

        private FakeRosBus bus;
        private GameObject pluginGo, routerGo, linkGo;
        private MtcPlugin plugin;
        private PickPlaceClient client;
        private MtcSolutionPlayer player;
        private FakeRobotModel robot;
        private InteractionRouter router;
        private RobotRegistry robots;
        private readonly List<InteractionRefusal> refusals = new List<InteractionRefusal>();
        private FakeRosBus.FakeActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> plan;
        private FakeRosBus.FakeActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> execute;

        // The second step of every solution takes fr3_joint1 to this.
        private const double Farthest = 4.0;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            bus.SetServiceHandler("/get_solution", request =>
                new GetSolutionResponse(true, "", Solution(((GetSolutionRequest)request).solution_id)));

            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            router.RefusalCoalesceSeconds = 0f;
            refusals.Clear();
            router.Refused += refusals.Add;

            pluginGo = new GameObject("mtc");
            client = pluginGo.AddComponent<PickPlaceClient>();
            pluginGo.AddComponent<PickPlaceTaskRecorder>();
            player = pluginGo.AddComponent<MtcSolutionPlayer>();   // before the plugin, which finds its siblings in Awake
            plugin = pluginGo.AddComponent<MtcPlugin>();

            linkGo = new GameObject("fr3_link1");
            robot = new FakeRobotModel("fr3_joint1").WithLimits(new JointLimit("fr3_joint1", JointKind.Revolute, -2.5f, 2.5f));
            robot.JointLinks["fr3_joint1"] = linkGo.transform;
            yield return null;                                   // Start of every component
            robots = FakeRobots.Single(bus, robot);
            ((IEruptPlugin)plugin).OnRegister(new EruptContext
            {
                Ros = bus, Ui = new FakeUiHost(), Undo = new UndoStack(), Robots = robots, Router = router
            });
            plan = bus.ActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>("/pick_place");
            execute = bus.ActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback>("/execute_solution");
        }

        [TearDown]
        public void TearDown()
        {
            RosBus.Reset();
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(linkGo);
            FakeRobots.DestroyAll(robots);
        }

        [UnityTest]
        public IEnumerator ASolutionOutsideTheLimits_IsFlaggedWhenFetched_AcrossAllItsSteps()
        {
            yield return Plan(7);

            PlanResult result = plugin.Results.Single().Result;
            Assert.IsTrue(result.HasLimitViolations);
            JointLimitViolation violation = result.LimitViolations.Single();
            Assert.AreEqual("fr3_joint1", violation.Joint);
            Assert.AreEqual(1.5f, violation.Excess, 1e-5f, "The worst point of the whole solution.");
            Assert.AreEqual(1, violation.Segment, "It is in the second step, which the plan's own trajectory (the first step) does not show.");
            Assert.AreEqual(1, violation.Point);
            Assert.AreEqual(2, violation.Count, "Both points of the second step are outside.");

            Assert.AreEqual(1, refusals.Count(r => r.Code == RefusalCode.PlanOutsideLimits), "The user is told once, when the solution arrives.");
            InteractionRefusal told = refusals.First(r => r.Code == RefusalCode.PlanOutsideLimits);
            StringAssert.Contains("fr3_joint1", told.Reason);
            Assert.AreSame(linkGo.transform, told.Subject);
        }

        [UnityTest]
        public IEnumerator ASolutionInsideTheLimits_IsNotFlagged()
        {
            robot.Limits = new JointLimitModel(new[] { new JointLimit("fr3_joint1", JointKind.Revolute, -6.2f, 6.2f) });

            yield return Plan(7);

            Assert.IsFalse(plugin.Results.Single().Result.HasLimitViolations);
            Assert.IsEmpty(refusals);
        }

        [UnityTest]
        public IEnumerator ASolutionOutsideTheLimits_PreviewsClamped()
        {
            yield return Plan(7);
            PlanResult result = plugin.Results.Single().Result;

            plugin.Preview(result);
            Assert.IsTrue(player.IsPlaying, "A flagged solution is still previewed.");
            float highest = 0f;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (player.IsPlaying && Time.realtimeSinceStartup < deadline)
            {
                highest = Mathf.Max(highest, robot.Positions[0]);
                Assert.LessOrEqual(robot.Positions[0], 2.5f + 1e-6f, "The robot never shows a pose outside its limits.");
                yield return null;
            }

            Assert.AreEqual(2.5f, highest, 1e-6f, "The preview ran up to the limit.");
            Assert.IsTrue(robot.Applied.Any(a => a.positions.Length > 0 && a.positions[0] >= Farthest - 1e-9), "The solution itself asked for more.");
        }

        [UnityTest]
        public IEnumerator ExecutingASolutionOutsideTheLimits_IsRefused()
        {
            yield return Plan(7);
            PlanResult result = plugin.Results.Single().Result;
            refusals.Clear();

            var statuses = new List<ExecutionStatus>();
            plugin.Execute(result, statuses.Add);
            yield return null;

            Assert.IsNull(execute.LastGoal, "No goal reaches execute_solution.");
            Assert.AreEqual(1, statuses.Count);
            Assert.AreEqual(ExecutionPhase.Refused, statuses[0].Phase);
            StringAssert.Contains("fr3_joint1", statuses[0].Message);
            Assert.AreEqual(PickPlacePhase.Idle, client.Phase);
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlanOutsideLimits, refusals[0].Code);
            StringAssert.StartsWith("Not executed", refusals[0].Reason);
        }

        [UnityTest]
        public IEnumerator TheClientItself_DoesNotSendASolutionOutsideTheLimits()
        {
            yield return Plan(7);

            Task run = client.ExecuteAsync(7);
            yield return null;

            Assert.IsTrue(run.IsFaulted);
            StringAssert.Contains("outside the joint limits", run.Exception.InnerException.Message);
            Assert.IsNull(execute.LastGoal);
            Assert.AreEqual(PickPlaceOutcome.Rejected, client.LastOutcome);
            Assert.That(client.LastStatus, Does.StartWith("REFUSED"));
            Assert.IsFalse(client.Busy);
        }

        [UnityTest]
        public IEnumerator WithTheOptOut_ASolutionOutsideTheLimitsExecutes()
        {
            plugin.AllowExecutionOutsideLimits = true;
            yield return Plan(7);
            PlanResult result = plugin.Results.Single().Result;
            Assert.IsTrue(result.HasLimitViolations, "Still flagged.");

            plugin.Execute(result, _ => { });

            Assert.IsNotNull(execute.LastGoal);
            Assert.AreEqual(7u, execute.LastGoal.solution_id);
            yield return Finish(execute.Accept());
        }

        [UnityTest]
        public IEnumerator TheOptOut_IsOffByDefault()
        {
            Assert.IsFalse(plugin.AllowExecutionOutsideLimits);
            yield return null;
        }

        [UnityTest]
        public IEnumerator JointStatesMirroredDuringExecution_AreClamped_AndRaiseOneMismatch()
        {
            // A solution inside the limits executes; the real robot then reports a joint outside them.
            robot.Limits = new JointLimitModel(new[] { new JointLimit("fr3_joint1", JointKind.Revolute, -4.5f, 4.5f) });
            yield return Plan(7);
            plugin.Execute(plugin.Results.Single().Result, _ => { });
            var goal = execute.Accept();
            yield return null;
            Assert.IsTrue(player.IsMirroring);

            for (int frame = 0; frame < 12; frame++)
            {
                // Several messages per frame, as a real joint state topic delivers them.
                for (int message = 0; message < 5; message++)
                    bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "fr3_joint1" }, position = new[] { 5.2 } });
                yield return null;
                Assert.LessOrEqual(robot.Positions[0], 4.5f + 1e-6f, "The mirrored robot stays inside its limits.");
            }

            Assert.AreEqual(4.5f, robot.Positions[0], 1e-6f);
            Assert.AreEqual(1, refusals.Count(r => r.Code == RefusalCode.LimitMismatch), "Sixty joint states, one refusal.");
            InteractionRefusal mismatch = refusals.First(r => r.Code == RefusalCode.LimitMismatch);
            StringAssert.Contains("fr3_joint1", mismatch.Reason);
            Assert.AreSame(linkGo.transform, mismatch.Subject);

            yield return Finish(goal);
        }

        // ---------- helpers ----------

        private IEnumerator Finish(FakeRosBus.FakeActionGoal<ExecuteSolutionResult> goal)
        {
            goal.Complete(RosActionGoalStatus.Succeeded,
                new ExecuteSolutionResult(true, "done", new MoveItErrorCodesMsg(MoveItErrorCodesMsg.SUCCESS, "", "")));
            while (client.Busy) yield return null;
            yield return null;
        }

        private IEnumerator Plan(params uint[] ids)
        {
            Task goal = client.PlanAsync("object", new PoseStampedMsg());
            var accepted = plan.Accept();
            yield return null;
            plan.Feedback(new PickPlaceFeedback(k_Task, "planning", (uint)ids.Length, 1f));
            bus.Inbound("/pick_place/description", PickPlaceClientTests.Description(k_TopicTask));
            bus.Inbound("/pick_place/statistics", PickPlaceClientTests.Statistics(k_TopicTask, ids));
            accepted.Complete(RosActionGoalStatus.Succeeded, new PickPlaceResult(true, "", k_Task, 0, new MoveItErrorCodesMsg()));
            while (!goal.IsCompleted) yield return null;
            yield return null;
        }

        // Two steps: stage 2 moves fr3_joint1 from 1 to 2, stage 3 from 3 to 4.
        private static SolutionMsg Solution(uint id) => new SolutionMsg
        {
            sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, cost = 1f, stage_id = 1 } } },
            sub_trajectory = new[]
            {
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 10, cost = 0.5f, stage_id = 2 }, trajectory = Motion(1.0, 2.0) },
                new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id + 20, cost = 0.5f, stage_id = 3 }, trajectory = Motion(3.0, Farthest) },
            }
        };

        private static RobotTrajectoryMsg Motion(double from, double to) => new RobotTrajectoryMsg
        {
            joint_trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "fr3_joint1" },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { from } },
                    new JointTrajectoryPointMsg { positions = new[] { to }, time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg(0, 10_000_000) },
                }
            }
        };
    }
}
