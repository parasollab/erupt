using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using RosMessageTypes.Moveit;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// Three robots (<c>left</c>, <c>right</c>, <c>third</c>; FR3s unless
    /// <see cref="LiveRobots.ArmsVariable"/> names others), each an independent ROS stack in the
    /// namespace of its id that believes its robot stands at the origin of its own world, sharing
    /// one Unity environment in which they stand in three places (one of them turned around).
    /// Everything the single-arm fixture checks, per arm, plus what only several arms can get
    /// wrong: traffic in the wrong namespace, an obstacle in the wrong place for a robot that does
    /// not stand at the scene's origin, an object bouncing between planning scenes.
    /// </summary>
    public class ThreeArmLiveTests : LiveSceneFixture
    {
        protected override string Scenario => LiveRos.Arms;
        protected override string EntryScene => "MultiArmFR3";
        protected override string MainScene => "MultiArmFR3";

        // The ROS side is started without poses: ARMS="left=fr3 right=fr3 third=fr3".
        private static readonly string[] Ids = { "left", "right", "third" };

        [UnityTest, Order(10)]
        public IEnumerator T01_ConnectionComesUp_ThreeRobotsEachInItsNamespace()
        {
            yield return Enter();
            yield return AssertConnected();
            CollectionAssert.AreEquivalent(Ids, Robots.Select(r => r.Id));
            foreach (string id in Ids) Assert.AreEqual(id, Robot(id).Namespace, $"Robot '{id}' is in the namespace of its id.");
            Assert.AreEqual(3, Robots.Select(r => r.PlanningFrame).Distinct().Count(), "Each robot has its own planning frame.");
            foreach (RobotInstance robot in Robots)
                Assert.IsTrue(robot.PlanningFrame.IsChildOf(robot.transform), $"The planning frame of '{robot.Id}' is its prefab's own anchor.");
            foreach (KeyValuePair<string, string> arm in LiveRobots.Arms)
                Assert.AreEqual(arm.Value, Robot(arm.Key).Profile.rosRobotName, $"'{arm.Key}' is the robot the ROS side was started with.");
            foreach (string id in Ids)
            {
                Assert.IsNotNull(MoveIt.SessionFor(Robot(id)), $"MoveIt serves '{id}'.");
                Assert.IsNotNull(Mtc.SessionFor(Robot(id)), $"MTC has a session for '{id}'.");
                Assert.IsNotNull(Demonstrations.SessionFor(Robot(id)), $"Demonstrations has a session for '{id}'.");
            }
            AssertNoExceptions();
        }

        [UnityTest, Order(20)]
        public IEnumerator T02_JointStates_AreMirrored_PerArm()
        {
            yield return Enter();
            foreach (string id in Ids) yield return AssertJointStatesMirrored(Robot(id));
        }

        [UnityTest, Order(30)]
        public IEnumerator T03_PlannerDiscovery_AnswersInEveryNamespace()
        {
            yield return Enter();
            foreach (string id in Ids) yield return AssertPlannersDiscovered(Robot(id));
        }

        [UnityTest, Order(35)]
        public IEnumerator T03b_EachRobotStandsInItsPlanningFrame_WhereItsRosSideHasIt()
        {
            yield return Enter();
            foreach (string id in Ids) yield return AssertBasePoseAgrees(Robot(id));
        }

        [UnityTest, Order(40)]
        public IEnumerator T04_SceneObstacles_AreInEveryPlanningScene_AndTheSameRelativeToEachBase()
        {
            yield return Enter();
            foreach (string id in Ids) yield return AssertEveryPublisherIsInPlanningScene(Robot(id));

            // One obstacle, seen from the root link of each robot's model: Unity's own geometry
            // against what that arm's MoveIt holds (MoveIt keeps its world relative to the root of
            // its robot model, having moved it there with that namespace's TF).
            CollisionObjectPublisher obstacle = CollisionObjectPublisher.Live.First(p => p != null && p.isActiveAndEnabled && !p.pausePublishing);
            foreach (string id in Ids)
            {
                RobotInstance robot = Robot(id);
                PlanningSceneMsg scene = null;
                yield return LiveRos.PlanningScene(robot, s => scene = s);
                CollisionObjectMsg co = LiveRos.Find(scene, obstacle.objectId);
                Assert.IsNotNull(co, $"'{obstacle.objectId}' is in the planning scene of '{id}'.");
                string modelFrame = LiveRos.ModelFrame(scene);
                LiveRos.PoseIn(scene, modelFrame, co, out Vector3 rosPosition, out Quaternion rosRotation);

                Assert.IsTrue(robot.Model.TryFindLinkTransform(modelFrame, out Transform rootLink), $"'{id}' has the link '{modelFrame}'.");
                Quaternion rootRotation = AttachedObjectPlacement.WorldConventionRotation(rootLink);
                Vector3 relative = Quaternion.Inverse(rootRotation) * (obstacle.transform.position - rootLink.position);
                Quaternion relativeRotation = Quaternion.Inverse(rootRotation) * obstacle.transform.rotation;
                var unityPosition = new Vector3(relative.x, relative.z, relative.y);
                var unityRotation = new Quaternion(-relativeRotation.x, -relativeRotation.z, -relativeRotation.y, relativeRotation.w);

                Assert.Less((rosPosition - unityPosition).magnitude, PositionTolerance,
                    $"'{obstacle.objectId}' relative to '{modelFrame}' of '{id}': MoveIt {rosPosition:F4}, Unity {unityPosition:F4}.");
                Assert.Less(Quaternion.Angle(rosRotation, unityRotation), RotationToleranceDegrees,
                    $"'{obstacle.objectId}' relative to '{modelFrame}' of '{id}': orientation differs.");
                Debug.Log($"[live] '{obstacle.objectId}' relative to '{modelFrame}' of '{id}': {rosPosition:F4} on both sides.");
            }
        }

        [UnityTest, Order(50)]
        public IEnumerator T05_AddMoveResizeDelete_AreReflectedInEveryPlanningScene()
        {
            yield return Enter();
            yield return AssertObstacleLifecycle(Ids.Select(Robot).ToList());
        }

        [UnityTest, Order(60)]
        public IEnumerator T06_AtRest_PlanningSceneTrafficIsQuiet_InEveryNamespace()
        {
            yield return Enter();
            yield return AssertQuietAtRest(10f);
        }

        [UnityTest, Order(70)]
        public IEnumerator T07_ObjectAddedOnOneRosSide_ReachesUnityAndTheOtherTwo_WithoutBouncing()
        {
            yield return Enter();
            // From two robots that stand in different places, one of them turned around.
            yield return AssertRosObjectIsMirrored(Robot("left"), new[] { Robot("right"), Robot("third") });
            yield return AssertRosObjectIsMirrored(Robot("third"), new[] { Robot("left"), Robot("right") });
            yield return AssertQuietAtRest(5f);
        }

        [UnityTest, Order(80)]
        public IEnumerator T08_PlanAndExecute_OnEveryArm_WithoutCrossTalk()
        {
            yield return Enter();
            foreach (string id in Ids)
            {
                RobotInstance robot = Robot(id);
                List<RobotInstance> others = Robots.Where(r => !ReferenceEquals(r, robot)).ToList();
                var before = new Dictionary<string, Dictionary<string, double>>();
                foreach (RobotInstance other in others) yield return RosJoints(other, j => before[other.Id] = j);
                var unityBefore = others.ToDictionary(o => o.Id, UnityJoints);

                // The same motion seen from each robot: 10 cm forward, 12 cm to its right, 12 cm up.
                Vector3 offset = robot.PlanningFrame.InverseTransformVector(robot.transform.TransformVector(new Vector3(0.12f, 0.12f, 0.10f)));
                yield return AssertPlanAndExecute(robot, new Vector3(offset.x, offset.z, offset.y));

                foreach (RobotInstance other in others)
                {
                    Dictionary<string, double> after = null;
                    yield return RosJoints(other, j => after = j);
                    foreach (KeyValuePair<string, double> joint in before[other.Id])
                        Assert.AreEqual(joint.Value, after[joint.Key], 1e-4, $"Executing on '{id}' moved the ROS robot '{other.Id}' ({joint.Key}).");
                    Dictionary<string, float> unityAfter = UnityJoints(other);
                    foreach (KeyValuePair<string, float> joint in unityBefore[other.Id])
                        Assert.AreEqual(joint.Value, unityAfter[joint.Key], 1e-4f, $"Executing on '{id}' moved the Unity robot '{other.Id}' ({joint.Key}).");
                    Assert.IsNull(MoveIt.LastResultFor(other) is { } stray && stray.RobotId == robot.Id ? stray : null);
                }
            }
        }

        [UnityTest, Order(90)]
        public IEnumerator T09_PlanWithAnObstacle_OnTheTurnedRobot()
        {
            yield return Enter();
            yield return AssertPlanAroundObstacle(Robot("third"));
        }

        [UnityTest, Order(100)]
        public IEnumerator T10_GripperVerb_ReachesOnlyTheRobotItWasPressedOn()
        {
            yield return Enter();
            RobotInstance right = Robot("right");
            var before = new Dictionary<string, Dictionary<string, double>>();
            foreach (RobotInstance other in Robots.Where(r => !ReferenceEquals(r, right)).ToList())
                yield return RosJoints(other, j => before[other.Id] = j);
            yield return AssertGripperReachesRos(right);
            foreach (RobotInstance other in Robots.Where(r => !ReferenceEquals(r, right)).ToList())
            {
                Dictionary<string, double> after = null;
                yield return RosJoints(other, j => after = j);
                foreach (KeyValuePair<string, double> joint in before[other.Id])
                    Assert.AreEqual(joint.Value, after[joint.Key], 1e-4, $"The gripper verb on 'right' moved '{other.Id}' ({joint.Key}).");
            }
        }

        [UnityTest, Order(110)]
        public IEnumerator T11_PickAndPlace_OnOneArm_WhileAnotherPlansAndExecutes()
        {
            yield return Enter();
            RobotInstance left = Robot("left"), right = Robot("right");
            Vector3 offset = right.PlanningFrame.InverseTransformVector(right.transform.TransformVector(new Vector3(-0.10f, 0.10f, 0.08f)));
            yield return AssertPickAndPlace(left, new Vector3(0.45f, 0.0f, 0.25f), new Vector3(0.45f, 0.25f, 0.25f),
                whilePlanning: () => AssertPlanAndExecute(right, new Vector3(offset.x, offset.z, offset.y)));
        }

        [UnityTest, Order(115)]
        public IEnumerator T11b_PickAndPlace_OnTheTurnedRobot()
        {
            yield return Enter();
            yield return AssertPickAndPlace(Robot("third"), new Vector3(0.45f, 0.0f, 0.25f), new Vector3(0.45f, -0.25f, 0.25f));
        }

        [UnityTest, Order(117)]
        public IEnumerator T11c_PickAndPlace_OfAnObjectThatRestsOnASurface()
        {
            yield return Enter();
            yield return AssertPickAndPlaceOnSurface(Robot("right"), new Vector2(0.45f, -0.10f), new Vector2(0.45f, 0.20f));
        }

        [UnityTest, Order(120)]
        public IEnumerator T12_Demonstration_OnOneArm_StaysInItsNamespace()
        {
            yield return Enter();
            yield return AssertDemonstration(Robot("third"));
        }

        [UnityTest, Order(130)]
        public IEnumerator T13_SceneReset_KeepsOneConnection_AndResetsEveryPlanningScene()
        {
            yield return Enter();
            yield return AssertSceneReloadKeepsOneConnection();
            foreach (string id in Ids) yield return AssertEveryPublisherIsInPlanningScene(Robot(id));
        }

        [UnityTest, Order(990)]
        public IEnumerator T99_AfterEverything_TheConnectionIsUp_AndNothingWentWrongQuietly()
        {
            yield return Enter();
            yield return AssertConnected();
            AssertNoExceptions();
            AssertNoErrorsFromErupt();
            AssertNoPlanningSceneComplaints();
        }
    }
}
