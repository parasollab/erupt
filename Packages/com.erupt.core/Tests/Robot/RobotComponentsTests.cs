using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The per-robot helper components next to a <see cref="RobotInstance"/>: they find their
    /// robot by sitting under it, take the ghost from its profile, and mark what they spawn
    /// outside the robot so the registry can say whose it is.
    /// </summary>
    public class RobotComponentsTests
    {
        private RobotRegistry robots;
        private RobotProfile profile;
        private GameObject robotGo, ghostSource;
        private RobotInstance robot;

        [SetUp]
        public void SetUp()
        {
            // A stand-in for the ghost prefab: instantiating a scene object works the same way.
            ghostSource = new GameObject("ghost source");
            GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(ghostSource.transform, false);

            profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.ghostPrefab = ghostSource;

            robots = FakeRobots.Registry(new FakeRosBus());
            robotGo = new GameObject("arm");
            robot = FakeRobots.Attach(robots, robotGo, "arm", "left", profile, new FakeRobotModel("j1"));
            robotGo.transform.position = new Vector3(1f, 2f, 3f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string ghost in new[] { "StartGhost", "GoalGhost" })
            {
                GameObject left = GameObject.Find(ghost);
                if (left != null) Object.DestroyImmediate(left);
            }
            Object.DestroyImmediate(robotGo);
            Object.DestroyImmediate(ghostSource);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void TheRobot_FindsItsComponentsUnderItself()
        {
            var child = new GameObject("helpers");
            child.transform.SetParent(robotGo.transform, false);
            var ghosts = child.AddComponent<RobotGhosts>();
            var player = child.AddComponent<JointTrajectoryPlayer>();

            Assert.AreSame(ghosts, robot.Ghosts);
            Assert.AreSame(player, robot.TrajectoryPlayer);
            Assert.IsNull(robot.Controller, "This robot's model was supplied by code.");
            Assert.IsNull(robot.Interaction);
        }

        [Test]
        public void Ghosts_ComeFromTheProfile_StandAtTheRobot_AndBelongToIt()
        {
            var child = new GameObject("helpers");
            child.transform.SetParent(robotGo.transform, false);
            var ghosts = child.AddComponent<RobotGhosts>();

            ghosts.SpawnGoalGhost();

            Assert.IsNotNull(ghosts.GoalGhost);
            Assert.AreEqual("GoalGhost", ghosts.GoalGhost.name);
            Assert.IsNull(ghosts.GoalGhost.transform.parent, "The ghost is not parented under the robot.");
            Assert.AreEqual(robotGo.transform.position, ghosts.GoalGhost.transform.position);
            Assert.AreSame(robot, robots.OwnerOf(ghosts.GoalGhost));
            Assert.AreSame(robot, robots.OwnerOf(ghosts.GoalGhost.transform.GetChild(0).gameObject));
            Assert.IsNotNull(ghosts.GoalGhost.GetComponent<TranslucentOverride>());

            ghosts.ClearGhosts();
        }

        [Test]
        public void AnAssignedGhostPrefab_WinsOverTheProfiles()
        {
            var special = new GameObject("special ghost source");
            new GameObject("marker of the special source").transform.SetParent(special.transform, false);
            try
            {
                var ghosts = robotGo.AddComponent<RobotGhosts>();
                ghosts.robotPrefab = special;

                ghosts.SpawnStartGhost();

                Assert.IsNotNull(ghosts.StartGhost.transform.Find("marker of the special source"));
                ghosts.ClearGhosts();
            }
            finally
            {
                Object.DestroyImmediate(special);
            }
        }

        [Test]
        public void WithoutAGhostPrefab_NoGhostIsShown()
        {
            profile.ghostPrefab = null;
            var ghosts = robotGo.AddComponent<RobotGhosts>();
            LogAssert.Expect(LogType.Warning, new Regex("No ghost prefab"));

            ghosts.SpawnStartGhost();

            Assert.IsNull(ghosts.StartGhost);
            ghosts.UpdateStartGhost();
            ghosts.ClearGhosts();
        }

        [UnityTest]
        public IEnumerator ATrajectoryPlayerUnderARobot_PlaysOnThatRobot()
        {
            var model = (FakeRobotModel)robot.Model;
            var player = robotGo.AddComponent<JointTrajectoryPlayer>();
            var trajectory = new RosMessageTypes.Trajectory.JointTrajectoryMsg
            {
                joint_names = new[] { "j1" },
                points = new[]
                {
                    new RosMessageTypes.Trajectory.JointTrajectoryPointMsg { positions = new[] { 0.4 } },
                    new RosMessageTypes.Trajectory.JointTrajectoryPointMsg
                    {
                        positions = new[] { 0.8 },
                        time_from_start = new RosMessageTypes.BuiltinInterfaces.DurationMsg { sec = 10 }
                    },
                }
            };

            player.StartReplay(trajectory);
            yield return null;

            Assert.IsNotEmpty(model.Applied, "No robot was assigned: the player used the one it sits under.");
            Assert.AreEqual(0.4, model.Applied[0].positions[0], 1e-9);
            Assert.AreEqual(1, model.ControlTakenCount, "A playback announces that it drives the robot, so a joint-state mirror lets go.");
            player.StopReplay();
            yield return null;
        }
    }
}
