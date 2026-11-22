using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// Moving a robot's base: <see cref="RobotInstance.PlaceBase(Pose, out string)"/> moves the
    /// articulation root (a Transform write alone does not survive a physics step), says so
    /// through <see cref="RobotInstance.BaseMoved"/>, is refused while a guard objects, and can
    /// be undone; the placement ghost shows where the base would go.
    /// </summary>
    public class RobotBaseTests
    {
        private RobotRegistry robots;
        private readonly List<Object> made = new();

        [SetUp]
        public void SetUp() => robots = FakeRobots.Registry(new FakeRosBus());

        [TearDown]
        public void TearDown()
        {
            GameObject ghost = GameObject.Find("PlacementGhost");
            if (ghost != null) Object.DestroyImmediate(ghost);
            FakeRobots.DestroyAll(robots);
            foreach (Object o in made)
                if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        // A root body with one revolute link 30 cm up, as an imported robot has.
        private RobotInstance Articulated(string id, out ArticulationBody link)
        {
            var root = new GameObject(id);
            root.AddComponent<ArticulationBody>().immovable = true;
            var child = new GameObject("link1");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(0f, 0.3f, 0f);
            link = child.AddComponent<ArticulationBody>();
            link.jointType = ArticulationJointType.RevoluteJoint;
            link.useGravity = false;
            return FakeRobots.Attach(robots, root, id, id, model: new FakeRobotModel("j1"));
        }

        private static readonly Pose Target = new Pose(new Vector3(2f, 0.5f, -1f), Quaternion.Euler(0f, 90f, 0f));

        [UnityTest]
        public IEnumerator PlaceBase_MovesTheArticulationRoot_AndItStaysThereThroughPhysics()
        {
            RobotInstance robot = Articulated("arm", out ArticulationBody link);
            yield return new WaitForFixedUpdate();

            Assert.IsTrue(robot.PlaceBase(Target, out string reason), reason);
            for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();

            AssertPose(Target, robot.BasePose);
            // The link went with the root: 30 cm above the base, turned with it.
            Assert.Less(Vector3.Distance(Target.position + new Vector3(0f, 0.3f, 0f), link.transform.position), 1e-3f);
            Assert.Less(Quaternion.Angle(Target.rotation, link.transform.rotation), 0.1f);
        }

        [UnityTest]
        public IEnumerator WithoutPlaceBase_ATransformWriteOnAnArticulationRoot_DoesNotLast()
        {
            // The reason PlaceBase exists: this is what a plain Transform write does.
            RobotInstance robot = Articulated("arm", out _);
            yield return new WaitForFixedUpdate();
            Pose before = robot.BasePose;

            robot.transform.SetPositionAndRotation(Target.position, Target.rotation);
            for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();

            AssertPose(before, robot.BasePose);
        }

        [Test]
        public void PlaceBase_RaisesBaseMoved_WithThePoseTheBaseHadBefore()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            robot.transform.position = new Vector3(1f, 0f, 0f);
            var heard = new List<(RobotInstance robot, Pose before)>();
            robot.BaseMoved += (r, before) => heard.Add((r, before));

            Assert.IsTrue(robot.PlaceBase(Target));

            Assert.AreEqual(1, heard.Count);
            Assert.AreSame(robot, heard[0].robot);
            AssertPose(new Pose(new Vector3(1f, 0f, 0f), Quaternion.identity), heard[0].before);
            AssertPose(Target, robot.BasePose);
        }

        [Test]
        public void TheRobotsOwnPlanningFrame_MovesWithTheBase()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            var frame = new GameObject("PlanningFrame");
            frame.transform.SetParent(robot.transform, false);
            robot.SetPlanningFrame(frame.transform);

            robot.PlaceBase(Target);

            AssertPose(Target, new Pose(robot.PlanningFrame.position, robot.PlanningFrame.rotation));
        }

        [Test]
        public void APlanningFrameThatIsNotPartOfTheRobot_StaysBehind_AndTheUserIsWarnedOnce()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            var shared = new GameObject("origin");
            made.Add(shared);
            robot.SetPlanningFrame(shared.transform);

            LogAssert.Expect(LogType.Warning, new Regex("planning frame 'origin' is not part of the robot"));
            robot.PlaceBase(Target);
            robot.PlaceBase(new Pose(Vector3.one, Quaternion.identity));   // said once

            Assert.AreEqual(Vector3.zero, shared.transform.position);
        }

        [Test]
        public void AGuard_RefusesTheMove_WithItsReason_AndNothingMoves()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            int moved = 0;
            robot.BaseMoved += (_, _) => moved++;
            string busy = "Robot 'arm' is executing a trajectory.";
            System.Func<string> guard = () => busy;
            robot.AddBaseGuard(guard);
            robot.AddBaseGuard(() => null);   // a guard with no objection changes nothing

            Assert.IsFalse(robot.CanPlaceBase(out string why));
            Assert.AreEqual(busy, why);
            Assert.IsFalse(robot.PlaceBase(Target, out string reason));
            Assert.AreEqual(busy, reason);
            Assert.AreEqual(Vector3.zero, robot.transform.position);
            Assert.AreEqual(0, moved);

            busy = null;   // the execution finished
            Assert.IsTrue(robot.PlaceBase(Target, out reason));
            Assert.IsNull(reason);

            busy = "again";
            robot.RemoveBaseGuard(guard);
            Assert.IsTrue(robot.CanPlaceBase(out _), "A guard that was removed is not asked.");
        }

        [Test]
        public void PlaceBaseCommand_UndoPutsTheBaseBack_AndRedoPlacesItAgain()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            var start = new Pose(new Vector3(0.2f, 0f, 0.4f), Quaternion.Euler(0f, 30f, 0f));
            robot.transform.SetPositionAndRotation(start.position, start.rotation);
            int moves = 0;
            robot.BaseMoved += (_, _) => moves++;
            var undo = new UndoStack();

            undo.Do(new PlaceBaseCommand(robot, Target));
            AssertPose(Target, robot.BasePose);
            Assert.AreEqual("Place arm", undo.NextUndoLabel);

            Assert.IsTrue(undo.Undo());
            AssertPose(start, robot.BasePose);

            Assert.IsTrue(undo.Redo());
            AssertPose(Target, robot.BasePose);
            Assert.AreEqual(3, moves, "Every move, undo and redo included, is announced.");
        }

        [Test]
        public void PlaceBaseCommand_ThatTheRobotRefuses_SaysWhy_AndUndoThenDoesNothing()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            robot.AddBaseGuard(() => "Robot 'arm' is carrying an object.");
            var command = new PlaceBaseCommand(robot, Target);
            string heard = null;
            command.Refused += why => heard = why;

            command.Execute();
            Assert.AreEqual("Robot 'arm' is carrying an object.", heard);
            Assert.AreEqual(heard, command.LastRefusal);
            Assert.AreEqual(Vector3.zero, robot.transform.position);

            heard = null;
            command.Undo();
            Assert.IsNull(heard, "Nothing was applied, so there is nothing to undo.");
        }

        [Test]
        public void ThePlacementGhost_StandsAtTheProposedBase_FollowsIt_AndIsTheRobots()
        {
            var ghostSource = new GameObject("ghost source");
            made.Add(ghostSource);
            GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(ghostSource.transform, false);
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            made.Add(profile);
            profile.ghostPrefab = ghostSource;
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"), profile);
            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();

            Assert.IsTrue(ghosts.ShowPlacementGhost(Target));
            GameObject ghost = ghosts.PlacementGhost;
            Assert.IsNotNull(ghost);
            AssertPose(Target, new Pose(ghost.transform.position, ghost.transform.rotation));
            Assert.AreSame(robot, robots.OwnerOf(ghost));
            Assert.IsNotNull(ghost.GetComponent<TranslucentOverride>());
            Assert.AreEqual(Vector3.zero, robot.transform.position, "Showing where the robot would go moves nothing.");
            Assert.IsNull(ghosts.StartGhost);
            Assert.IsNull(ghosts.GoalGhost);

            var elsewhere = new Pose(new Vector3(-1f, 0f, 3f), Quaternion.Euler(0f, -45f, 0f));
            Assert.IsTrue(ghosts.ShowPlacementGhost(elsewhere));
            Assert.AreSame(ghost, ghosts.PlacementGhost, "The same copy is moved.");
            AssertPose(elsewhere, new Pose(ghost.transform.position, ghost.transform.rotation));

            ghosts.ClearPlacementGhost();
            Assert.IsNull(ghosts.PlacementGhost);
        }

        [Test]
        public void ARobotWithoutAGhostPrefab_HasNoPlacementGhost()
        {
            RobotInstance robot = FakeRobots.Add(robots, "arm", "", new FakeRobotModel("j1"));
            RobotGhosts ghosts = robot.gameObject.AddComponent<RobotGhosts>();

            LogAssert.Expect(LogType.Warning, new Regex("No ghost prefab"));
            Assert.IsFalse(ghosts.ShowPlacementGhost(Target));
            Assert.IsNull(ghosts.PlacementGhost);
        }

        private static void AssertPose(Pose expected, Pose actual)
        {
            Assert.Less(Vector3.Distance(expected.position, actual.position), 1e-3f, $"position: expected {expected.position}, was {actual.position}");
            Assert.Less(Quaternion.Angle(expected.rotation, actual.rotation), 0.1f, "rotation");
        }
    }
}
