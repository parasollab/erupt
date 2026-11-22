using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RosMessageTypes.Std;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Ros;
using Erupt.Ros.Tests;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// The robot registry and the robots in it: ids, the active robot, events, who owns an
    /// object, and the namespaced bus each robot gets.
    /// </summary>
    public class RobotRegistryTests
    {
        private FakeRosBus bus;
        private RobotRegistry registry;
        private readonly List<UnityEngine.Object> created = new();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            registry = FakeRobots.Registry(bus);
        }

        [TearDown]
        public void TearDown()
        {
            FakeRobots.DestroyAll(registry);
            FakeRobots.DestroyAll(RobotRegistry.Default);
            foreach (UnityEngine.Object o in created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            created.Clear();
        }

        // ---------- registration, ids, active ----------

        [Test]
        public void Robots_AreListedInRegistrationOrder_AndFoundById()
        {
            RobotInstance left = FakeRobots.Add(registry, "left", "left", new FakeRobotModel());
            RobotInstance right = FakeRobots.Add(registry, "right", "right", new FakeRobotModel());

            CollectionAssert.AreEqual(new[] { left, right }, registry.All);
            Assert.IsTrue(registry.TryGet("right", out RobotInstance found));
            Assert.AreSame(right, found);
            Assert.IsFalse(registry.TryGet("middle", out _));
            Assert.AreSame(registry, left.Registry);
        }

        [Test]
        public void TheIdDefaultsToTheObjectName()
        {
            var go = Inactive("fr3");
            var robot = go.AddComponent<RobotInstance>();
            robot.UseRegistry(registry);
            go.SetActive(true);

            Assert.AreEqual("fr3", robot.Id);
            Assert.IsTrue(registry.TryGet("fr3", out _));

            go.name = "renamed";
            Assert.AreEqual("fr3", robot.Id, "The id is fixed while the robot is registered.");
        }

        [Test]
        public void TheFirstRobotIsActive_UntilAnotherIsChosen()
        {
            Assert.IsNull(registry.Active);
            RobotInstance left = FakeRobots.Add(registry, "left", "left", new FakeRobotModel());
            RobotInstance right = FakeRobots.Add(registry, "right", "right", new FakeRobotModel());
            Assert.AreSame(left, registry.Active);

            registry.SetActive(right);
            Assert.AreSame(right, registry.Active);
        }

        [Test]
        public void SetActive_RefusesARobotThatIsNotRegistered()
        {
            var other = FakeRobots.Registry(bus);
            RobotInstance stranger = FakeRobots.Add(other, "stranger", "", new FakeRobotModel());
            try
            {
                Assert.Throws<ArgumentException>(() => registry.SetActive(stranger));
                Assert.Throws<ArgumentNullException>(() => registry.SetActive(null));
            }
            finally
            {
                FakeRobots.DestroyAll(other);
            }
        }

        [Test]
        public void Events_ReportAddedRemovedAndActiveChanges()
        {
            var log = new List<string>();
            registry.Added += r => log.Add("added " + r.Id);
            registry.Removed += r => log.Add("removed " + r.Id);
            registry.ActiveChanged += r => log.Add("active " + (r != null ? r.Id : "none"));

            RobotInstance left = FakeRobots.Add(registry, "left", "left", new FakeRobotModel());
            RobotInstance right = FakeRobots.Add(registry, "right", "right", new FakeRobotModel());
            registry.SetActive(right);
            registry.SetActive(right);                       // already active: no event
            right.gameObject.SetActive(false);               // disabling unregisters
            left.gameObject.SetActive(false);

            CollectionAssert.AreEqual(new[]
            {
                "added left", "active left", "added right", "active right",
                "removed right", "active left", "removed left", "active none"
            }, log);
            CollectionAssert.IsEmpty(registry.All);
            Assert.IsNull(left.Registry);

            left.gameObject.SetActive(true);                 // enabling registers again
            CollectionAssert.AreEqual(new[] { left }, registry.All);
            Assert.AreSame(left, registry.Active);
        }

        [Test]
        public void ADuplicateId_IsAnErrorThatNamesBothObjects()
        {
            RobotInstance first = FakeRobots.Add(registry, "arm", "left", new FakeRobotModel());
            first.name = "Left Arm";

            var go = Inactive("Right Arm");
            var second = go.AddComponent<RobotInstance>();
            second.Configure("arm", "right", null, new FakeRobotModel());
            second.UseRegistry(registry);

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(second));
            StringAssert.Contains("'arm'", ex.Message);
            StringAssert.Contains("Left Arm", ex.Message);
            StringAssert.Contains("Right Arm", ex.Message);

            // A scene robot reports the same through the log and stays out of the registry.
            LogAssert.Expect(LogType.Error, new Regex("Robot id 'arm' is used by both 'Left Arm' and 'Right Arm'"));
            go.SetActive(true);
            CollectionAssert.AreEqual(new[] { first }, registry.All);
            Assert.IsNull(second.Registry);
            Assert.IsNull(second.Ros);
        }

        [Test]
        public void ARobot_JoinsTheDefaultRegistry_UnlessGivenAnother()
        {
            var go = new GameObject("scene robot");
            created.Add(go);
            var robot = go.AddComponent<RobotInstance>();

            CollectionAssert.Contains(RobotRegistry.Default.All, robot);
            CollectionAssert.DoesNotContain(registry.All, robot);

            UnityEngine.Object.DestroyImmediate(go);
            CollectionAssert.IsEmpty(RobotRegistry.Default.All, "Destroying the robot unregisters it.");
        }

        [Test]
        public void Configure_IsRefusedWhileRegistered()
        {
            RobotInstance robot = FakeRobots.Add(registry, "arm", "", new FakeRobotModel());
            Assert.Throws<InvalidOperationException>(() => robot.Configure("other", "", null));
            Assert.Throws<InvalidOperationException>(() => robot.UseRegistry(new RobotRegistry()));
        }

        // ---------- owner lookup ----------

        [Test]
        public void OwnerOf_FindsTheRobotAboveAnObject()
        {
            RobotInstance robot = FakeRobots.Add(registry, "arm", "", new FakeRobotModel());
            var link = new GameObject("link");
            link.transform.SetParent(robot.transform, false);
            var visual = new GameObject("visual");
            visual.transform.SetParent(link.transform, false);

            Assert.AreSame(robot, registry.OwnerOf(visual));
            Assert.AreSame(robot, registry.OwnerOf(robot.gameObject));
        }

        [Test]
        public void OwnerOf_FollowsTheMarker_ForObjectsOutsideTheRobot()
        {
            RobotInstance left = FakeRobots.Add(registry, "left", "left", new FakeRobotModel());
            RobotInstance right = FakeRobots.Add(registry, "right", "right", new FakeRobotModel());

            var handle = new GameObject("joint handle");
            created.Add(handle);
            var sphere = new GameObject("sphere");
            sphere.transform.SetParent(handle.transform, false);
            RobotOwned.Mark(handle, right);

            Assert.AreSame(right, registry.OwnerOf(handle));
            Assert.AreSame(right, registry.OwnerOf(sphere), "The marker covers the objects under it.");

            RobotOwned.Mark(handle, left);
            Assert.AreSame(left, registry.OwnerOf(handle), "Marking again replaces the owner.");
            Assert.AreEqual(1, handle.GetComponents<RobotOwned>().Length);
        }

        [Test]
        public void OwnerOf_IsNull_ForAnObjectThatBelongsToNoRobot()
        {
            FakeRobots.Add(registry, "arm", "", new FakeRobotModel());
            var table = new GameObject("table");
            created.Add(table);

            Assert.IsNull(registry.OwnerOf(table));
            Assert.IsNull(registry.OwnerOf(null));
        }

        // ---------- bus, names, frames ----------

        [Test]
        public void EachRobot_SeesTheBusFromItsOwnNamespace()
        {
            RobotInstance root = FakeRobots.Add(registry, "root", "", new FakeRobotModel());
            RobotInstance left = FakeRobots.Add(registry, "left", "left", new FakeRobotModel());

            root.Ros.Publish("joint_states", new StringMsg("a"));
            left.Ros.Publish("joint_states", new StringMsg("b"));
            left.Ros.Publish("/tf", new StringMsg("c"));

            Assert.AreEqual(1, bus.CountOn("/joint_states"), "No namespace: the name goes out as it does today.");
            Assert.AreEqual(1, bus.CountOn("/left/joint_states"));
            Assert.AreEqual(1, bus.CountOn("/tf"), "An absolute name is not namespaced.");
            Assert.AreSame(left.Ros, left.Ros, "Components handed the bus twice see the same object.");
            Assert.AreEqual("left", left.Namespace);
        }

        [Test]
        public void ARobot_HasNoBus_UntilTheRegistryIsGivenOne()
        {
            var busless = new RobotRegistry();
            RobotInstance robot = FakeRobots.Add(busless, "arm", "left", new FakeRobotModel());
            try
            {
                Assert.IsNull(robot.Ros);
                busless.SetBus(bus);
                robot.Ros.RegisterPublisher<StringMsg>("status");
                CollectionAssert.AreEqual(new[] { "/left/status" }, bus.RegisteredPublishers);
            }
            finally
            {
                FakeRobots.DestroyAll(busless);
            }
        }

        [Test]
        public void WithoutAProfile_TheRobotUsesTheDefaults()
        {
            RobotInstance robot = FakeRobots.Add(registry, "arm", "", new FakeRobotModel());

            Assert.IsNull(robot.Profile);
            Assert.AreSame(RobotNameMap.Identity, robot.Names);
            Assert.AreEqual("world", robot.PlanningFrameId);
            Assert.AreEqual("joint_states", robot.JointStatesTopic);
            Assert.AreEqual("", robot.TrajectoryCommandTopic);
            Assert.That(robot.Validate(), Has.Some.Contains("No robot profile"));
        }

        [Test]
        public void WithAProfile_TheRobotTakesItsNamesAndTopicsFromIt()
        {
            RobotProfile profile = RobotProfileTests.TwoFingerArm();
            created.Add(profile);
            profile.planningFrame = "map";
            profile.jointStatesTopic = "state/joints";
            profile.names = new RobotNameMap { localPrefix = "fr3_", rosPrefix = "panda_" };
            var model = new FakeRobotModel("j1", "j2");

            RobotInstance robot = FakeRobots.Add(registry, "arm", "", model, profile);

            Assert.AreSame(profile, robot.Profile);
            Assert.AreSame(model, robot.Model);
            Assert.AreEqual("panda_hand", robot.Names.ToRos("fr3_hand"));
            Assert.AreEqual("map", robot.PlanningFrameId);
            Assert.AreEqual("state/joints", robot.JointStatesTopic);
            Assert.AreEqual("arm_controller/joint_trajectory", robot.TrajectoryCommandTopic);
        }

        [Test]
        public void ThePlanningFrame_IsTheRobotsOwn_OrTheRegistrysDefault()
        {
            var origin = new GameObject("origin");
            var own = new GameObject("left base");
            created.Add(origin);
            created.Add(own);
            registry.DefaultPlanningFrame = origin.transform;
            RobotInstance robot = FakeRobots.Add(registry, "arm", "", new FakeRobotModel());

            Assert.AreSame(origin.transform, robot.PlanningFrame);
            robot.SetPlanningFrame(own.transform);
            Assert.AreSame(own.transform, robot.PlanningFrame);
        }

        [Test]
        public void Validate_ReportsJointsTheRobotDoesNotHave()
        {
            RobotProfile profile = RobotProfileTests.TwoFingerArm();
            created.Add(profile);
            RobotInstance robot = FakeRobots.Add(registry, "arm", "", new FakeRobotModel(), profile);
            foreach (string linkName in new[] { "base", "link1", "link2", "leftfinger", "hand" })
            {
                var link = new GameObject(linkName);
                link.transform.SetParent(robot.transform, false);
                if (linkName != "link2" && linkName != "base" && linkName != "hand") link.AddComponent<ArticulationBody>();
            }

            List<string> problems = robot.Validate();

            Assert.AreEqual(2, problems.Count, string.Join("\n", problems));
            Assert.That(problems, Has.Some.Contains("link 'link2' has no ArticulationBody"));
            Assert.That(problems, Has.Some.Contains("no link named 'rightfinger'"));
        }

        private GameObject Inactive(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            created.Add(go);
            return go;
        }
    }
}
