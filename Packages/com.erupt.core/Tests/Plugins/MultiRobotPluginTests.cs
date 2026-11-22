using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// One plugin instance, three robots (left, right, third): the per-robot session helper, the
    /// family verbs acting on the owner of the selected object, the gripper verb, and the tabs
    /// following the active robot.
    /// </summary>
    public class MultiRobotPluginTests
    {
        private sealed class Session : System.IDisposable
        {
            public RobotInstance Robot;
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private FakeUiHost ui;
        private EruptContext context;
        private GameObject pluginGo, routerGo, hostGo;
        private InteractionRouter router;
        private readonly List<InteractionRefusal> refusals = new();
        private readonly List<GameObject> extras = new();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            robots = FakeRobots.Three(bus);
            left = robots.All[0];
            right = robots.All[1];
            third = robots.All[2];
            ui = new FakeUiHost();

            // A router without input sources says so; these tests only use its refusal channel.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;

            context = new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack(), Router = router };
            pluginGo = new GameObject("plugins");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in extras)
                if (go != null) Object.DestroyImmediate(go);
            extras.Clear();
            if (hostGo != null) Object.DestroyImmediate(hostGo);
            if (pluginGo != null) Object.DestroyImmediate(pluginGo);
            if (routerGo != null) Object.DestroyImmediate(routerGo);
            FakeRobots.DestroyAll(robots);
        }

        private T Register<T>() where T : EruptPluginBehaviour
        {
            T plugin = pluginGo.AddComponent<T>();
            ((IEruptPlugin)plugin).OnRegister(context);
            return plugin;
        }

        // ---------- RobotSessions ----------

        [Test]
        public void Sessions_OnePerRobot_CreatedForLateRobots_DisposedWhenTheyLeave()
        {
            var created = new List<string>();
            var sessions = new RobotSessions<Session>(robots, robot =>
            {
                created.Add(robot.Id);
                return new Session { Robot = robot };
            });

            CollectionAssert.AreEqual(FakeRobots.ThreeIds, created);
            Assert.AreEqual(3, sessions.Count);
            CollectionAssert.AreEqual(new[] { left, right, third }, sessions.Robots);
            Assert.AreSame(left, sessions.Active.Robot, "The first robot is active.");
            Assert.AreSame(right, sessions.For(right).Robot);
            CollectionAssert.AreEqual(new[] { left, right, third }, sessions.Select(s => s.Robot));

            RobotInstance fourth = FakeRobots.Add(robots, "fourth", "fourth", new FakeRobotModel());
            Assert.AreEqual(4, sessions.Count, "A robot that joins later gets a session.");
            Session fourthSession = sessions.For(fourth);

            Session rightSession = sessions.For(right);
            Object.DestroyImmediate(right.gameObject);
            Assert.IsTrue(rightSession.Disposed, "A robot that leaves has its session disposed.");
            Assert.AreEqual(3, sessions.Count);
            Assert.IsNull(sessions.For(right));

            robots.SetActive(third);
            Assert.AreSame(third, sessions.Active.Robot);

            sessions.Dispose();
            Assert.IsTrue(fourthSession.Disposed);
            Assert.AreEqual(0, sessions.Count);
            FakeRobots.Add(robots, "fifth", "fifth", new FakeRobotModel());
            Assert.AreEqual(0, sessions.Count, "A disposed helper no longer follows the registry.");
        }

        [Test]
        public void Sessions_ResolveTheOwnerOfASelection_AndFallBackToTheActiveRobot()
        {
            var sessions = new RobotSessions<Session>(robots, robot => new Session { Robot = robot });
            SelectableMarker thirdTool = FakeRobots.EndEffectorOf(third);
            var obstacle = new GameObject("obstacle").AddComponent<SelectableMarker>();
            extras.Add(obstacle.gameObject);

            Assert.AreSame(third, sessions.For(thirdTool).Robot);
            Assert.AreSame(third, sessions.RobotOf(thirdTool));
            Assert.AreSame(left, sessions.For(obstacle).Robot, "An object that belongs to no robot addresses the active one.");
            robots.SetActive(right);
            Assert.AreSame(right, sessions.For(obstacle).Robot);
            Assert.AreSame(right, sessions.For((ISelectable)null).Robot);
            sessions.Dispose();
        }

        [Test]
        public void Sessions_ARobotThePluginCannotServe_HasNoSession()
        {
            var sessions = new RobotSessions<Session>(robots, robot => robot == right ? null : new Session { Robot = robot });
            Assert.AreEqual(2, sessions.Count);
            Assert.IsNull(sessions.For(right));
            Assert.IsFalse(sessions.TryGet(right, out _));
            CollectionAssert.AreEqual(new[] { left, third }, sessions.Robots);
            sessions.Dispose();
        }

        [Test]
        public void Sessions_WithoutARegistry_AreEmpty()
        {
            var sessions = new RobotSessions<Session>(null, robot => new Session { Robot = robot });
            Assert.AreEqual(0, sessions.Count);
            Assert.IsNull(sessions.Active);
            Assert.IsNull(sessions.For(left));
            sessions.Dispose();
        }

        // ---------- planning verbs ----------

        [Test]
        public void PlanningVerbs_ActOnTheOwnerOfTheSelectedEndEffector()
        {
            FakePlanner planner = Register<FakePlanner>();
            SelectableMarker rightTool = FakeRobots.EndEffectorOf(right);
            SelectableMarker thirdTool = FakeRobots.EndEffectorOf(third);

            ui.Bound["set-goal"](thirdTool);
            ui.Bound["plan"](rightTool);
            ui.Bound["plan"](thirdTool);

            CollectionAssert.AreEqual(new[] { third }, planner.GoalRobots);
            CollectionAssert.AreEqual(new[] { right, third }, planner.PlanRobots);
            Assert.AreSame(left, robots.Active, "A verb does not change the active robot.");

            // Each plan remembers its robot, and its handle is owned by that robot.
            TrajectorySelectable rightPlan = planner.Results[0];
            TrajectorySelectable thirdPlan = planner.Results[1];
            Assert.AreEqual("right", rightPlan.RobotId);
            Assert.AreEqual("third", thirdPlan.RobotId);
            Assert.AreSame(right, robots.OwnerOf(rightPlan.gameObject));
            Assert.AreSame(third, planner.RobotOf(thirdPlan.Result));
            StringAssert.Contains("right", rightPlan.DisplayName, "With several robots a plan's label says whose it is.");

            ui.Bound["preview"](thirdPlan);
            ui.Bound["execute"](rightPlan);
            CollectionAssert.AreEqual(new[] { "third" }, planner.PreviewedRobotIds);
            CollectionAssert.AreEqual(new[] { "right" }, planner.ExecutedRobotIds);

            Assert.AreSame(thirdPlan.Result, planner.LastResultFor(third));
            Assert.AreSame(rightPlan.Result, planner.LastResultFor(right));
            Assert.IsNull(planner.LastResultFor(left));
        }

        [Test]
        public void PlanningVerbs_OnAnObjectOfNoRobot_AddressTheActiveRobot()
        {
            FakePlanner planner = Register<FakePlanner>();
            robots.SetActive(right);
            ui.Bound["plan"](null);
            planner.RequestPlan(new PlanPreferences(), _ => { });
            CollectionAssert.AreEqual(new[] { right, right }, planner.PlanRobots);
        }

        [Test]
        public void ClearingOneRobotsPlans_LeavesTheOthers()
        {
            FakePlanner planner = Register<FakePlanner>();
            planner.RequestPlan(left, new PlanPreferences(), _ => { });
            planner.RequestPlan(right, new PlanPreferences(), _ => { });
            planner.RequestPlan(third, new PlanPreferences(), _ => { });

            planner.ClearPlansOf(right);

            CollectionAssert.AreEqual(new[] { "left", "third" }, planner.Results.Select(r => r.RobotId));
            Assert.IsNull(planner.LastResultFor(right));
            Assert.IsNotNull(planner.LastResultFor(left));
        }

        [Test]
        public void ARobotNoPlannerServes_GetsARefusal_AndTheOthersStillPlan()
        {
            FakePlanner planner = Register<FakePlanner>();
            planner.Unserved.Add(right);

            ui.Bound["plan"](FakeRobots.EndEffectorOf(right));
            Assert.AreEqual(0, planner.PlanCalls);
            Assert.AreEqual(1, refusals.Count);
            Assert.AreEqual(RefusalCode.PlannerUnavailable, refusals[0].Code);
            StringAssert.Contains("right", refusals[0].Reason);

            ui.Bound["plan"](FakeRobots.EndEffectorOf(third));
            Assert.AreEqual(1, planner.PlanCalls);
            Assert.AreSame(planner, PlanningPlugin.PlannerFor(ui, third));
            Assert.IsNull(PlanningPlugin.PlannerFor(ui, right));
        }

        // ---------- tabs ----------

        [Test]
        public void TheTab_NamesTheActiveRobot_AndIsRebuiltWhenItChanges()
        {
            FakePlanner planner = Register<FakePlanner>();
            PanelTab tab = ui.TabsById["planner-fake"];
            Assert.AreEqual(new[] { "planner-fake" }, ui.Tabs, "One tab however many robots.");
            Assert.AreEqual("Fake Planner · left", tab.Label);
            Assert.AreEqual(1, planner.TabBuilds);

            robots.SetActive(third);
            Assert.AreEqual("Fake Planner · third", tab.Label);
            Assert.AreEqual(2, planner.TabBuilds, "The tab is rebuilt for the new active robot.");

            // Down to one robot: the title is the plugin's name alone.
            Object.DestroyImmediate(left.gameObject);
            Object.DestroyImmediate(right.gameObject);
            Assert.AreEqual("Fake Planner", tab.Label);
            Assert.AreSame(third, robots.Active);
        }

        [Test]
        public void WithOneRobot_TheTabTitleIsThePluginName()
        {
            var bus1 = new FakeRosBus();
            RobotRegistry one = FakeRobots.Single(bus1, new FakeRobotModel());
            var hostUi = new FakeUiHost();
            var go = new GameObject("single planner");
            extras.Add(go);
            var planner = go.AddComponent<FakePlanner>();
            try
            {
                ((IEruptPlugin)planner).OnRegister(new EruptContext { Ros = bus1, Robots = one, Ui = hostUi, Undo = new UndoStack() });
                Assert.AreEqual("Fake Planner", hostUi.TabsById["planner-fake"].Label);
                planner.RequestPlan(new PlanPreferences(), _ => { });
                Assert.AreEqual("Fake Planner plan 1", planner.Results[0].DisplayName);
                Assert.AreEqual("robot", planner.Results[0].RobotId);
            }
            finally
            {
                ((IEruptPlugin)planner).OnUnregister(planner.Context);
                FakeRobots.DestroyAll(one);
            }
        }

        [Test]
        public void TheDemosTab_FollowsTheActiveRobot_AndCorrectStartsOnTheOwner()
        {
            FakeDemonstrator demonstrator = Register<FakeDemonstrator>();

            PanelTab tab = ui.TabsById["demos-fake-demo"];
            Assert.AreEqual("Fake Demonstrator · left", tab.Label);
            robots.SetActive(right);
            Assert.AreEqual("Fake Demonstrator · right", tab.Label);
            Assert.AreEqual(2, demonstrator.TabBuilds);

            ui.Bound["correct"](FakeRobots.EndEffectorOf(third));
            CollectionAssert.AreEqual(new[] { third }, demonstrator.StartRobots);
            demonstrator.StartDemonstration();
            Assert.AreSame(right, demonstrator.StartRobots[1], "Without a target the active robot is meant.");
        }

        // ---------- gripper ----------

        [UnityTest]
        public System.Collections.IEnumerator TheGripperVerb_ActsOnTheOwner_AndARobotWithoutOneRefuses()
        {
            // The third robot has no gripper.
            Object.DestroyImmediate(third.gameObject);
            third = FakeRobots.Add(robots, "third", "third", new FakeRobotModel("third_joint1"));
            Assert.IsNull(third.Gripper);
            Assert.IsNotNull(left.Gripper);

            hostGo = new GameObject("host");
            hostGo.SetActive(false);
            var host = hostGo.AddComponent<PluginHost>();
            host.DiscoverInScene = false;
            host.Initialise(context);
            hostGo.SetActive(true);
            yield return null;   // Start binds the gripper verb

            Assert.IsTrue(ui.Bound.ContainsKey("gripper"), "Bound once, whatever the robots have.");
            var leftModel = (FakeRobotModel)left.Model;
            var rightModel = (FakeRobotModel)right.Model;

            ui.Bound["gripper"](FakeRobots.EndEffectorOf(right));
            Assert.AreEqual(1, rightModel.GripperToggles);
            Assert.AreEqual(0, leftModel.GripperToggles, "The active robot is not touched when another owns the selection.");

            ui.Bound["gripper"](FakeRobots.EndEffectorOf(third));
            Assert.AreEqual(1, refusals.Count, "A robot without a gripper answers with a refusal.");
            Assert.AreEqual("This robot has no gripper.", refusals[0].Reason);
            Assert.AreEqual(1, rightModel.GripperToggles);
            Assert.AreEqual(0, leftModel.GripperToggles);

            // The others still work afterwards; an unowned target means the active robot.
            ui.Bound["gripper"](null);
            Assert.AreEqual(1, leftModel.GripperToggles);
        }

        // ---------- a robot's base is moved ----------

        [Test]
        public void MovingARobotsBase_DropsThatRobotsPlans_StopsItsPreview_AndLeavesTheOthersAlone()
        {
            FakePlanner planner = Register<FakePlanner>();
            planner.RequestPlan(left, new PlanPreferences(), _ => { });
            planner.RequestPlan(right, new PlanPreferences(), _ => { });
            planner.RequestPlan(third, new PlanPreferences(), _ => { });
            planner.StoppedPreviews.Clear();

            Assert.IsTrue(right.PlaceBase(new Pose(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 90f, 0f))));

            CollectionAssert.AreEqual(new[] { right }, planner.StoppedPreviews);
            CollectionAssert.AreEqual(new[] { right }, planner.BaseMoves);
            Assert.IsNull(planner.LastResultFor(right), "A plan made for the old base is no plan for the new one.");
            Assert.IsNotNull(planner.LastResultFor(left));
            Assert.IsNotNull(planner.LastResultFor(third));
            CollectionAssert.AreEquivalent(new[] { "left", "third" }, planner.Results.Select(r => r.RobotId).ToList());
        }

        [Test]
        public void APlannerThatNeedsTheBaseWhereItIs_RefusesTheMove_ForThatRobotOnly()
        {
            FakePlanner planner = Register<FakePlanner>();
            planner.Busy.Add(right);

            Assert.IsFalse(right.PlaceBase(new Pose(Vector3.one, Quaternion.identity), out string reason));
            Assert.AreEqual("Robot 'right' is executing a trajectory.", reason);
            Assert.AreEqual(Vector3.zero, right.transform.position);
            Assert.IsEmpty(planner.BaseMoves);

            Assert.IsTrue(left.PlaceBase(new Pose(Vector3.one, Quaternion.identity)), "Another robot is free to move.");
        }

        [Test]
        public void ARobotThatJoinsLater_IsWatchedToo_AndOneWhosePlannerUnregistered_IsLetGo()
        {
            FakePlanner planner = Register<FakePlanner>();
            RobotInstance fourth = FakeRobots.Add(robots, "fourth", "fourth", new FakeRobotModel());
            planner.Busy.Add(fourth);
            Assert.IsFalse(fourth.PlaceBase(new Pose(Vector3.one, Quaternion.identity)));

            ((IEruptPlugin)planner).OnUnregister(context);

            Assert.IsTrue(fourth.PlaceBase(new Pose(Vector3.one, Quaternion.identity)), "An unregistered planner has no say.");
            Assert.IsEmpty(planner.BaseMoves, "And is not told.");
        }
    }
}
