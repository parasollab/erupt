using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// One robot in the root namespace (the FR3 of the scene, or the shipped robot
    /// <see cref="LiveRobots.RobotVariable"/> names, put in its place): the app is entered through its first scene and driven
    /// through the entry points the UI uses, against a real MoveIt / MTC stack.
    /// </summary>
    public class SingleArmLiveTests : LiveSceneFixture
    {
        protected override string Scenario => LiveRos.Single;
        protected override string EntryScene => "KitchenFR3";
        protected override string MainScene => "KitchenFR3";

        private static RobotInstance Arm => Robots[0];

        [UnityTest, Order(10)]
        public IEnumerator T01_ConnectionComesUp_WithoutErrors()
        {
            yield return Enter();
            yield return AssertConnected();
            Assert.AreEqual(1, Robots.Count, "The scene has one robot.");
            Assert.AreEqual(LiveRobots.SingleRobot ?? "fr3", Arm.Id, "The robot is known by its object's name.");
            Assert.AreEqual(LiveRobots.SingleRobot ?? "fr3", Arm.Profile.rosRobotName, "The robot is the one the ROS side was started with.");
            Assert.AreEqual("", Arm.Namespace, "The single robot is in the root namespace.");
            Assert.IsNotNull(MoveIt, "The MoveIt plugin is registered.");
            Assert.IsNotNull(Mtc, "The MTC plugin is registered.");
            Assert.IsNotNull(Demonstrations, "The demonstration plugin is registered.");
            AssertNoExceptions();
            AssertNoErrorsFromErupt();
        }

        [UnityTest, Order(20)]
        public IEnumerator T02_JointStates_AreMirrored_WithoutClamping()
        {
            yield return Enter();
            yield return AssertJointStatesMirrored(Arm);
        }

        [UnityTest, Order(30)]
        public IEnumerator T03_PlannerDiscovery_ReturnsThePlanners()
        {
            yield return Enter();
            yield return AssertPlannersDiscovered(Arm);
        }

        [UnityTest, Order(35)]
        public IEnumerator T03b_TheRobotStandsInItsPlanningFrame_WhereItsRosSideHasIt()
        {
            yield return Enter();
            yield return AssertBasePoseAgrees(Arm);
        }

        [UnityTest, Order(40)]
        public IEnumerator T04_SceneObstacles_AreInMoveIt_WithFramePoseAndSize()
        {
            yield return Enter();
            yield return AssertEveryPublisherIsInPlanningScene(Arm);
            // What the ROS side brought itself (demo_scene:=true) came in with the first snapshot.
            yield return AssertRosObjectsAreMirrored(Arm);
        }

        [UnityTest, Order(50)]
        public IEnumerator T05_AddMoveResizeDelete_AreReflectedInMoveIt()
        {
            yield return Enter();
            yield return AssertObstacleLifecycle(new[] { Arm });
        }

        [UnityTest, Order(55)]
        public IEnumerator T05b_RobotAtItsReadyPose_IsInsideUnitysLimits()
        {
            yield return Enter();
            // The profile's initial pose is the SRDF's "ready": bring the ROS robot there with the
            // app's own planning flow, then mirror it; nothing may be clamped.
            yield return MoveRosRobotTo(Arm, Arm.Profile.initialPose);
            yield return AssertJointStatesMirrored(Arm);
        }

        [UnityTest, Order(60)]
        public IEnumerator T06_AtRest_PlanningSceneTrafficIsQuiet()
        {
            yield return Enter();
            yield return AssertQuietAtRest(10f);
        }

        [UnityTest, Order(70)]
        public IEnumerator T07_ObjectAddedOnTheRosSide_AppearsMovesAndIsRemovedInUnity()
        {
            yield return Enter();
            yield return AssertRosObjectIsMirrored(Arm, new RobotInstance[0]);
        }

        [UnityTest, Order(80)]
        public IEnumerator T08_PlanToAReachableGoal_ThenExecute_RobotFollows()
        {
            yield return Enter();
            yield return AssertPlanAndExecute(Arm, new Vector3(0.10f, -0.12f, 0.12f));
        }

        [UnityTest, Order(85)]
        public IEnumerator T08b_TheSameFlow_WithMouseAndKeyboard_AsInTheEditorWithoutAHeadset()
        {
            yield return Enter();
            yield return AssertDesktopFlow(Arm);
        }

        [UnityTest, Order(100)]
        public IEnumerator T10_GripperVerb_ClosesAndOpensTheRosGripper()
        {
            yield return Enter();
            yield return AssertGripperReachesRos(Arm);
        }

        [UnityTest, Order(110)]
        public IEnumerator T11_PickAndPlace_RecordPlanPreviewExecute()
        {
            yield return Enter();
            yield return AssertPickAndPlace(Arm, new Vector3(0.45f, 0.0f, 0.25f), new Vector3(0.45f, 0.25f, 0.25f));
        }

        [UnityTest, Order(112)]
        public IEnumerator T11c_PickAndPlace_OfAnObjectThatRestsOnASurface()
        {
            yield return Enter();
            yield return AssertPickAndPlaceOnSurface(Arm, new Vector2(0.45f, -0.10f), new Vector2(0.45f, 0.20f));
        }

        [UnityTest, Order(120)]
        public IEnumerator T12_Demonstration_RecordReplayPublish()
        {
            yield return Enter();
            yield return AssertDemonstration(Arm);
        }

        [UnityTest, Order(130)]
        public IEnumerator T13_SceneReset_KeepsOneConnection_AndResetsThePlanningScene()
        {
            yield return Enter();
            yield return AssertSceneReloadKeepsOneConnection();
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

        [UnityTest, Order(90)]
        public IEnumerator T09_PlanWithAnObstacle_AvoidsItOrRefusesCleanly()
        {
            yield return Enter();
            yield return AssertPlanAroundObstacle(Arm);
        }
    }
}
