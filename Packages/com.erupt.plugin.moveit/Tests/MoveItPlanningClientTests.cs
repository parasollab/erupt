using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>
    /// The MoveIt transport against the FakeRosBus seam: what MoveIt would receive and what the
    /// robot gets back. The robot's profile supplies the group, the topics and a ROS name rule
    /// (the ROS robot says <c>panda_</c> where the Unity robot says <c>fr3_</c>).
    /// </summary>
    public class MoveItPlanningClientTests
    {
        private FakeRosBus bus;
        private FakeRobot robot;
        private RobotProfile profile;
        private RobotRegistry robots;
        private MoveItPlanningSettings settings;
        private MoveItPlanningClient client;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            robot = new FakeRobot(new[] { "fr3_joint1", "fr3_joint2" }, new[] { 0.1f, 0.2f });
            profile = FakeRobots.ProfileWithPrefixes("fr3_", "panda_");
            profile.armGroup = "panda_arm";
            profile.baseLink = "fr3_link0";
            profile.trajectoryCommandTopic = "panda_arm_controller/joint_trajectory";
            settings = new MoveItPlanningSettings();
            client = ClientFor(robot);
        }

        [TearDown]
        public void TearDown()
        {
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(profile);
        }

        // A client for one robot in the given ROS namespace, replacing the previous robot.
        private MoveItPlanningClient ClientFor(IRobotModel model, string rosNamespace = "")
        {
            FakeRobots.DestroyAll(robots);
            robots = FakeRobots.Single(bus, model, rosNamespace, profile);
            return new MoveItPlanningClient(robots.Active, settings);
        }

        [Test]
        public void Connect_RegistersPublisherServicesAndJointStateSubscription()
        {
            client.Connect();
            Assert.Contains("/panda_arm_controller/joint_trajectory", bus.RegisteredPublishers);
            Assert.Contains("/plan_kinematic_path", bus.RegisteredServices);
            Assert.Contains("/query_planner_interface", bus.RegisteredServices);
            Assert.Contains("/joint_states", bus.Subscriptions);
        }

        [Test]
        public void Connect_InANamespace_PutsEveryNameUnderIt()
        {
            client = ClientFor(robot, "left");
            client.Connect();
            Assert.Contains("/left/panda_arm_controller/joint_trajectory", bus.RegisteredPublishers);
            Assert.Contains("/left/plan_kinematic_path", bus.RegisteredServices);
            Assert.Contains("/left/query_planner_interface", bus.RegisteredServices);
            CollectionAssert.AreEqual(new[] { "/left/joint_states" }, bus.Subscriptions);

            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/left/plan_kinematic_path", req => { sent = (GetMotionPlanRequest)req; return Failure(); });
            bus.SetServiceHandler("/left/query_planner_interface", _ => new QueryPlannerInterfacesResponse());
            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, _ => { });
            Assert.IsNotNull(sent, "The plan request goes to the robot's own move_group.");
            Assert.IsTrue(client.QueryPlanners());

            client.SetMirroring(true);
            bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "panda_joint1" }, position = new[] { 0.5 } });
            Assert.AreEqual(0, robot.Applied.Count, "Another robot's joint states are not this robot's.");
            bus.Inbound("/left/joint_states", new JointStateMsg { name = new[] { "panda_joint1" }, position = new[] { 0.5 } });
            Assert.AreEqual(1, robot.Applied.Count);

            var plan = new PlanResult
            {
                Trajectory = new JointTrajectoryMsg { joint_names = new[] { "fr3_joint1" }, points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.3 } } } }
            };
            Assert.IsTrue(client.Execute(plan));
            Assert.AreEqual(1, bus.CountOn("/left/panda_arm_controller/joint_trajectory"));
        }

        [Test]
        public void CaptureRobotState_RemapsJointNamesToRos()
        {
            RobotStateMsg state = client.CaptureRobotState();
            Assert.AreEqual(new[] { "panda_joint1", "panda_joint2" }, state.joint_state.name);
            Assert.AreEqual(0.1, state.joint_state.position[0], 1e-6);
            Assert.AreEqual("world", state.joint_state.header.frame_id, "The frame is the robot's planning frame.");

            profile.planningFrame = "map";
            Assert.AreEqual("map", client.CaptureRobotState().joint_state.header.frame_id);
        }

        [Test]
        public void WithoutANameRule_NamesCrossTheWireUnchanged()
        {
            profile.names = new RobotNameMap();
            client = ClientFor(robot);
            Assert.AreEqual(new[] { "fr3_joint1", "fr3_joint2" }, client.CaptureRobotState().joint_state.name);
        }

        [Test]
        public void ARobotWithoutATrajectoryTopic_CannotExecute()
        {
            profile.trajectoryCommandTopic = "";
            client = ClientFor(robot);
            client.Connect();
            CollectionAssert.IsEmpty(bus.RegisteredPublishers);
            Assert.IsFalse(client.CanExecute);

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("names no trajectory command topic"));
            var plan = new PlanResult
            {
                Trajectory = new JointTrajectoryMsg { joint_names = new[] { "fr3_joint1" }, points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.3 } } } }
            };
            Assert.IsFalse(client.Execute(plan));
            CollectionAssert.IsEmpty(bus.Publishes);
        }

        [Test]
        public void Mirroring_AppliesRosJointStatesToTheRobot_OnlyWhenOn()
        {
            client.Connect();
            bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "panda_joint1" }, position = new[] { 0.5 } });
            Assert.AreEqual(0, robot.Applied.Count, "Off by default, as the legacy panel's toggle was.");

            client.SetMirroring(true);
            bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "panda_joint1" }, position = new[] { 0.5 } });
            Assert.AreEqual(1, robot.Applied.Count);
            Assert.AreEqual("fr3_joint1", robot.Applied[0].names[0], "ROS names are remapped to Unity names before applying.");
        }

        [Test]
        public void QueryPlanners_PopulatesListingsFromTheService()
        {
            bus.SetServiceHandler("/query_planner_interface", _ => new QueryPlannerInterfacesResponse
            {
                planner_interfaces = new[]
                {
                    new PlannerInterfaceDescriptionMsg { pipeline_id = "ompl", planner_ids = new[] { "RRTConnect", "PRM" } },
                    new PlannerInterfaceDescriptionMsg { pipeline_id = "chomp", planner_ids = new[] { "chomp" } }
                }
            });
            IReadOnlyList<PlannerListing> seen = null;
            client.PlannersUpdated += p => seen = p;

            Assert.IsTrue(client.QueryPlanners());

            Assert.IsNotNull(seen);
            Assert.AreEqual(2, client.Planners.Count);
            Assert.AreEqual(new[] { "RRTConnect", "PRM" }, client.PlannersFor("ompl"));
            Assert.IsEmpty(client.PlannersFor("missing"));
        }

        [Test]
        public void RequestPlan_SendsGroupPipelinePlannerAndJointGoalConstraints()
        {
            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/plan_kinematic_path", req => { sent = (GetMotionPlanRequest)req; return Failure(); });

            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(),
                new PlanPreferences { PipelineId = "ompl", PlannerId = "RRTConnect", Attempts = 3, AllowedTimeSeconds = 2f }, _ => { });

            Assert.IsNotNull(sent);
            var r = sent.motion_plan_request;
            Assert.AreEqual("panda_arm", r.group_name);
            Assert.AreEqual("ompl", r.pipeline_id);
            Assert.AreEqual("RRTConnect", r.planner_id);
            Assert.AreEqual(3, r.num_planning_attempts);
            Assert.AreEqual(2.0, r.allowed_planning_time, 1e-6);
            Assert.AreEqual(1, r.goal_constraints.Length);
            var jc = r.goal_constraints[0].joint_constraints;
            Assert.AreEqual(2, jc.Length);
            Assert.AreEqual("panda_joint2", jc[1].joint_name);
            Assert.AreEqual(0.2, jc[1].position, 1e-6);
            Assert.AreEqual(settings.goalTolerance, jc[1].tolerance_above, 1e-9);
        }

        [Test]
        public void RequestPlan_LeavesGripperJointsOutOfTheGoalConstraints()
        {
            robot = new FakeRobot(new[] { "fr3_joint1", "fr3_joint2", "fr3_finger_joint1" }, new[] { 0.1f, 0.2f, 0.04f })
            {
                ArmNames = new[] { "fr3_joint1", "fr3_joint2" }
            };
            client = ClientFor(robot);
            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/plan_kinematic_path", req => { sent = (GetMotionPlanRequest)req; return Failure(); });

            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, _ => { });

            var r = sent.motion_plan_request;
            Assert.AreEqual(new[] { "panda_joint1", "panda_joint2", "panda_finger_joint1" }, r.start_state.joint_state.name,
                "The start state still tells MoveIt where the fingers are.");
            Assert.AreEqual(new[] { "panda_joint1", "panda_joint2" },
                System.Array.ConvertAll(r.goal_constraints[0].joint_constraints, c => c.joint_name),
                "The arm group cannot move the fingers, so the goal must not constrain them.");
        }

        [Test]
        public void RequestPlan_Success_YieldsAUnityNamedTrajectory()
        {
            bus.SetServiceHandler("/plan_kinematic_path", _ => new GetMotionPlanResponse
            {
                motion_plan_response = new MotionPlanResponseMsg
                {
                    error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                    planning_time = 0.25,
                    trajectory = new RobotTrajectoryMsg
                    {
                        joint_trajectory = new JointTrajectoryMsg
                        {
                            joint_names = new[] { "panda_joint1", "panda_joint2" },
                            points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.0, 0.0 } }, new JointTrajectoryPointMsg { positions = new[] { 1.0, 1.0 } } }
                        }
                    }
                }
            });
            PlanResult result = null;
            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, r => result = r);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.HasTrajectory);
            Assert.AreEqual(new[] { "fr3_joint1", "fr3_joint2" }, result.Trajectory.joint_names);
            Assert.AreEqual(0.25f, result.PlanningTimeSeconds, 1e-6f);
            Assert.IsInstanceOf<MotionPlanResponseMsg>(result.PlannerPayload);
        }

        [Test]
        public void RequestPlan_Failure_ReportsTheMoveItErrorAndNoResult()
        {
            bus.SetServiceHandler("/plan_kinematic_path", _ => Failure());
            PlanResult result = new PlanResult();
            string error = null;
            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, r => result = r, e => error = e);

            Assert.IsNull(result);
            StringAssert.Contains("-1", error);
        }

        [Test]
        public void Execute_PublishesTheRosNamedTrajectoryOnTheControllerTopic()
        {
            var plan = new PlanResult
            {
                Trajectory = new JointTrajectoryMsg { joint_names = new[] { "fr3_joint1" }, points = new[] { new JointTrajectoryPointMsg { positions = new[] { 0.3 } } } }
            };
            Assert.IsTrue(client.Execute(plan));
            Assert.AreEqual(1, bus.CountOn("/panda_arm_controller/joint_trajectory"));
            var published = (JointTrajectoryMsg)System.Linq.Enumerable.First(bus.PublishedOn("/panda_arm_controller/joint_trajectory"));
            Assert.AreEqual("panda_joint1", published.joint_names[0], "Unity names are remapped back to ROS names on the way out.");

            Assert.IsFalse(client.Execute(new PlanResult()), "Nothing to execute without a trajectory.");
        }

        // ---- found against a real ROS stack (Assets/Erupt.App/Tests/Live) ----

        private static PlanResult PlanTo(double joint1) => new PlanResult
        {
            Trajectory = new JointTrajectoryMsg
            {
                joint_names = new[] { "fr3_joint1" },
                points = new[]
                {
                    new JointTrajectoryPointMsg { positions = new[] { 0.0 } },
                    new JointTrajectoryPointMsg { positions = new[] { joint1 }, time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg { sec = 1 } }
                }
            }
        };

        private void RosReports(double joint1) =>
            bus.Inbound("/joint_states", new JointStateMsg { name = new[] { "panda_joint1" }, position = new[] { joint1 } });

        [Test]
        public void Execute_FollowsTheRosRobotToTheEndOfTheTrajectory_ThenLetsGo()
        {
            client.ExecutionSettleSeconds = 0f;
            bool? reached = null;
            var changes = new List<bool>();
            client.MirroringChanged += changes.Add;

            Assert.IsTrue(client.Execute(PlanTo(0.3), (ok, _) => reached = ok));
            Assert.IsTrue(client.IsMirroring && client.IsFollowingExecution, "The Unity robot follows the real one while it executes.");

            RosReports(0.15);
            Assert.AreEqual(1, robot.Applied.Count, "A joint state on the way is shown.");
            Assert.IsNull(reached);

            RosReports(0.3);
            Assert.AreEqual(2, robot.Applied.Count, "The last joint state is shown too.");
            Assert.AreEqual(true, reached, "The execution is reported finished when the robot reports the trajectory's end.");
            Assert.IsFalse(client.IsMirroring, "After the execution the Unity robot is the user's again.");
            CollectionAssert.AreEqual(new[] { true, false }, changes);

            RosReports(0.31);
            Assert.AreEqual(2, robot.Applied.Count, "Later joint states no longer move the Unity robot.");
        }

        [Test]
        public void Execute_WithMirrorJointsOn_KeepsMirroringAfterwards()
        {
            client.ExecutionSettleSeconds = 0f;
            client.Connect();
            client.SetMirroring(true);
            Assert.IsTrue(client.Execute(PlanTo(0.3)));
            RosReports(0.3);
            Assert.IsFalse(client.IsFollowingExecution);
            Assert.IsTrue(client.IsMirroring, "Mirror joints was switched on by hand and stays on.");
        }

        [Test]
        public void JointStatesFromBeforeTheCommand_DoNotCountAsHavingArrived()
        {
            client.ExecutionSettleSeconds = 0f;
            client.Connect();
            RosReports(0.3);   // the robot happens to be reported there before anything was sent
            bool? reached = null;
            Assert.IsTrue(client.Execute(PlanTo(0.3), (ok, _) => reached = ok));
            Assert.IsNull(reached, "Only a joint state received after the command says where it led.");
            RosReports(0.3);
            Assert.AreEqual(true, reached);
        }

        [Test]
        public void TheMirror_LetsGo_WhenTheUserOrAPlaybackTakesTheRobot()
        {
            client.Connect();
            client.SetMirroring(true);
            bool? changedTo = null;
            client.MirroringChanged += on => changedTo = on;

            robot.BeginInteraction();   // a drag, a jog; previews and replays call TakeControl
            Assert.IsFalse(client.IsMirroring, "A mirror that kept writing the joints would undo the user's pose with every joint state.");
            Assert.AreEqual(false, changedTo);
            RosReports(0.5);
            Assert.AreEqual(0, robot.Applied.Count);

            Assert.IsTrue(client.Execute(PlanTo(0.3)));
            robot.TakeControl();
            Assert.IsFalse(client.IsFollowingExecution, "The same goes for an execution that is being followed.");
        }

        [Test]
        public void RequestPlan_WithoutAConnection_IsRefusedAtOnce_InsteadOfWaitingForEver()
        {
            bus.HasConnectionError = true;
            bool sent = false;
            bus.SetServiceHandler("/plan_kinematic_path", _ => { sent = true; return Failure(); });
            PlanResult result = new PlanResult();
            string error = null;
            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, r => result = r, e => error = e);

            Assert.IsNull(result);
            StringAssert.Contains("not connected", error);
            Assert.IsFalse(sent, "A request sent with nothing listening is never answered.");
        }

        [Test]
        public void APlanInFlight_WhenTheConnectionDrops_FailsWhenItIsBack()
        {
            // No handler: the request is never answered, as when the endpoint goes away mid-plan.
            int done = 0;
            PlanResult result = new PlanResult();
            string error = null;
            client.RequestPlan(client.CaptureRobotState(), client.CaptureRobotState(), null, r => { result = r; done++; }, e => error = e);
            Assert.AreEqual(0, done);

            client.ConnectionRestored();
            Assert.AreEqual(1, done);
            Assert.IsNull(result);
            StringAssert.Contains("connection to ROS was lost", error);
            client.ConnectionRestored();
            Assert.AreEqual(1, done, "Failed once.");
        }

        [Test]
        public void MoveItErrors_AreDescribedInWords()
        {
            StringAssert.Contains("could not plan", MoveItPlanningClient.DescribeError(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.FAILURE, message = "" }));
            StringAssert.Contains("99999", MoveItPlanningClient.DescribeError(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.FAILURE, message = "" }));
            string goal = MoveItPlanningClient.DescribeError(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.GOAL_IN_COLLISION, message = "fr3_hand - box" });
            StringAssert.Contains("the goal is in collision", goal);
            StringAssert.Contains("fr3_hand - box", goal);
            Assert.AreEqual("no response", MoveItPlanningClient.DescribeError(null));
        }

        [Test]
        public void PlannerListing_IsReducedToWhatTheArmGroupCanBeAskedFor()
        {
            // OMPL with planner configurations: every group's, plus the bare group names.
            string[] ompl = { "panda_arm", "panda_arm[RRTConnect]", "panda_arm[PRM]", "hand", "hand[RRTConnect]", "panda_arm_hand", "panda_arm_hand[RRT]" };
            CollectionAssert.AreEqual(new[] { "RRTConnect", "PRM" }, MoveItPlanningClient.PlannersForGroup(ompl, "panda_arm"));

            // OMPL without any (what a bare MoveIt configuration lists): group names are not planners.
            CollectionAssert.IsEmpty(MoveItPlanningClient.PlannersForGroup(new[] { "panda_arm", "panda_arm_hand", "hand" }, "panda_arm"));

            // A pipeline that lists plain planner names.
            CollectionAssert.AreEqual(new[] { "PTP", "LIN", "CIRC" }, MoveItPlanningClient.PlannersForGroup(new[] { "PTP", "LIN", "CIRC" }, "panda_arm"));

            bus.SetServiceHandler("/query_planner_interface", _ => new QueryPlannerInterfacesResponse
            {
                planner_interfaces = new[] { new PlannerInterfaceDescriptionMsg { pipeline_id = "ompl", planner_ids = ompl } }
            });
            Assert.IsTrue(client.QueryPlanners());
            CollectionAssert.AreEqual(new[] { "RRTConnect", "PRM" }, client.PlannersFor("ompl"));
        }

        [Test]
        public void TheGripperCommand_GoesToMoveItsTrajectoryExecution_ForTheDriverJoint()
        {
            profile.gripper = new GripperSpec { kind = GripperKind.Prismatic, driverJoint = "fr3_finger_joint1", open = 0.04f, closed = 0f, openWidthMetres = 0.08f };
            client = ClientFor(robot, "left");
            client.Connect();
            Assert.IsTrue(client.CanCommandGripper);
            var action = bus.ActionClient<ExecuteTrajectoryGoal, ExecuteTrajectoryResult, ExecuteTrajectoryFeedback>("/left/execute_trajectory");
            bus.Inbound("/left/joint_states", new JointStateMsg { name = new[] { "panda_finger_joint1" }, position = new[] { 0.035 } });

            (bool open, bool ok)? moved = null;
            var refusals = new List<InteractionRefusal>();
            client.GripperMoved += (open, ok) => moved = (open, ok);
            client.Refused += refusals.Add;

            client.CommandGripper(open: false);
            JointTrajectoryMsg sent = action.LastGoal.trajectory.joint_trajectory;
            CollectionAssert.AreEqual(new[] { "panda_finger_joint1" }, sent.joint_names, "The driver joint, under its ROS name.");
            Assert.AreEqual(2, sent.points.Length);
            Assert.AreEqual(0.035, sent.points[0].positions[0], 1e-9, "It starts where the ROS robot reports the joint (MoveIt refuses a trajectory that does not).");
            Assert.AreEqual(0.0, sent.points[1].positions[0], 1e-9, "It ends at the profile's closed position.");
            Assert.Greater(sent.points[1].time_from_start.sec + sent.points[1].time_from_start.nanosec * 1e-9, 0.0);

            action.Accept().Complete(Unity.Robotics.ROSTCPConnector.RosActionGoalStatus.Succeeded,
                new ExecuteTrajectoryResult(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS }));
            Assert.AreEqual((false, true), moved);
            CollectionAssert.IsEmpty(refusals);

            // A command MoveIt fails is something the user is told.
            client.CommandGripper(open: true);
            Assert.AreEqual(0.04, action.LastGoal.trajectory.joint_trajectory.points[1].positions[0], 1e-6);
            action.Accept().Complete(Unity.Robotics.ROSTCPConnector.RosActionGoalStatus.Aborted,
                new ExecuteTrajectoryResult(new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.CONTROL_FAILED }));
            Assert.AreEqual((true, false), moved);
            Assert.AreEqual(1, refusals.Count);
            StringAssert.Contains("did not open", refusals[0].Reason);
        }

        [Test]
        public void TheGripperCommand_WithoutAConnection_IsARefusal_NotAGoal()
        {
            profile.gripper = new GripperSpec { kind = GripperKind.Prismatic, driverJoint = "fr3_finger_joint1", open = 0.04f, closed = 0f };
            client = ClientFor(robot);
            client.Connect();
            var action = bus.ActionClient<ExecuteTrajectoryGoal, ExecuteTrajectoryResult, ExecuteTrajectoryFeedback>("/execute_trajectory");
            bus.HasConnectionError = true;
            var refusals = new List<InteractionRefusal>();
            client.Refused += refusals.Add;

            client.CommandGripper(open: false);
            Assert.IsNull(action.LastGoal);
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals[0].Code);
            StringAssert.Contains("not connected", refusals[0].Reason);
        }

        [Test]
        public void ARobotWithoutAGripperDescription_HasNoGripperCommand()
        {
            client.Connect();
            Assert.IsFalse(client.CanCommandGripper);
            client.CommandGripper(open: false);   // nothing to do, nothing thrown
            CollectionAssert.IsEmpty(bus.Publishes);
        }

        private static GetMotionPlanResponse Failure() => new GetMotionPlanResponse
        {
            motion_plan_response = new MotionPlanResponseMsg { error_code = new MoveItErrorCodesMsg { val = -1, message = "PLANNING_FAILED" } }
        };

        /// <summary>Minimal robot: remembers what was applied.</summary>
        private sealed class FakeRobot : IRobotModel
        {
            private readonly string[] names; private readonly float[] positions;
            public readonly List<(string[] names, double[] positions)> Applied = new();
            public FakeRobot(string[] names, float[] positions) { this.names = names; this.positions = positions; }
            public Transform Root => null;
            public Transform EndEffector => null;
            /// <summary>The IK chain when it is shorter than the joint state (a robot with a gripper).</summary>
            public string[] ArmNames;
            public IReadOnlyList<string> JointNames => ArmNames ?? names;
            public bool TryGetJointAngle(string jointName, out float positionRadians) { positionRadians = 0; return false; }
            public string[] GetJointStateNames() => names;
            public float[] GetJointStatePositions() => positions;
            public IJointLimits Limits => JointLimitModel.Unlimited;
            public bool TryGetJointLink(string jointName, out Transform link) { link = null; return false; }
            public JointWriteReport ApplyJointState(string[] n, double[] p) { Applied.Add((n, p)); return JointWriteReport.Clean; }
            public JointWriteReport ApplyJointState(IList<string> n, IList<float> p)
            {
                Applied.Add((new List<string>(n).ToArray(), System.Array.ConvertAll(new List<float>(p).ToArray(), x => (double)x)));
                return JointWriteReport.Clean;
            }
            public InteractionRefusal TrySolveToTarget(Vector3 targetPosition) => InteractionRefusal.None;
            public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians) => InteractionRefusal.None;
            public Transform FindLinkTransform(string linkName) => null;
            public bool TryFindLinkTransform(string linkName, out Transform link) { link = null; return false; }
            public event System.Action ControlTaken;
            public void TakeControl() => ControlTaken?.Invoke();
            public void BeginInteraction() => TakeControl();
            public void EndInteraction() { }
        }
    }
}
