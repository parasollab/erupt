using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros.Tests;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>The MoveIt transport against the FakeRosBus seam: what MoveIt would receive and what the robot gets back.</summary>
    public class MoveItPlanningClientTests
    {
        private FakeRosBus bus;
        private FakeRobot robot;
        private MoveItPlanningSettings settings;
        private MoveItPlanningClient client;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            robot = new FakeRobot(new[] { "fr3_joint1", "fr3_joint2" }, new[] { 0.1f, 0.2f });
            settings = new MoveItPlanningSettings
            {
                planningGroupName = "panda_arm",
                defaultPlannerId = "panda_arm",
                executeTrajectoryTopic = "/panda_arm_controller/joint_trajectory",
                rosJointNamePrefix = "panda_",
                unityJointNamePrefix = "fr3_"
            };
            client = new MoveItPlanningClient(bus, robot, settings);
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
        public void CaptureRobotState_RemapsJointNamesToRos()
        {
            RobotStateMsg state = client.CaptureRobotState();
            Assert.AreEqual(new[] { "panda_joint1", "panda_joint2" }, state.joint_state.name);
            Assert.AreEqual(0.1, state.joint_state.position[0], 1e-6);
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
            public IReadOnlyList<string> JointNames => names;
            public bool TryGetJointAngle(string jointName, out float positionRadians) { positionRadians = 0; return false; }
            public string[] GetJointStateNames() => names;
            public float[] GetJointStatePositions() => positions;
            public void ApplyJointState(string[] n, double[] p) => Applied.Add((n, p));
            public void ApplyJointState(IList<string> n, IList<float> p) => Applied.Add((new List<string>(n).ToArray(), System.Array.ConvertAll(new List<float>(p).ToArray(), x => (double)x)));
            public InteractionRefusal TrySolveToTarget(Vector3 targetPosition) => InteractionRefusal.None;
            public InteractionRefusal TryNudgeJoint(ArticulationBody joint, float deltaRadians) => InteractionRefusal.None;
            public Transform FindLinkTransform(string linkName) => null;
            public void BeginInteraction() { }
            public void EndInteraction() { }
        }
    }
}
