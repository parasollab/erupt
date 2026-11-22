using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    public class PlanningPluginTests
    {
        private GameObject go;
        private FakePlanner planner;
        private FakeUiHost ui;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("planner");
            planner = go.AddComponent<FakePlanner>();
            ui = new FakeUiHost();
            context = new EruptContext { Ui = ui, Undo = new UndoStack() };
            ((IEruptPlugin)planner).OnRegister(context);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(go);

        [Test]
        public void Register_ContributesTheGuidelineVerbsAndOneTab()
        {
            CollectionAssert.IsSubsetOf(new[] { "set-goal", "plan", "preview", "execute" }, ui.Bound.Keys);
            Assert.AreEqual(2, ui.Registered.Count, "Only 'plan' and 'stop-preview' are new; the rest already exist in the table and are bound, not added.");
            Assert.AreEqual((SelectionKind.EndEffector, "plan", "fake"), ui.Registered[0]);
            Assert.AreEqual((SelectionKind.Trajectory, PlanningPlugin.StopPreviewVerb, "fake"), ui.Registered[1]);
            // A planner whose preview ends by itself never shows it.
            Assert.IsFalse(planner.IsPreviewPlaying(null));
            Assert.AreEqual(new[] { "planner-fake" }, ui.Tabs);

            Verb plan = ui.Verbs.For(SelectionKind.EndEffector).First(v => v.Id == "plan");
            Assert.AreEqual(VerbOrigin.Plugin, plan.Origin);
            Assert.AreEqual("fake", plan.PluginId);
        }

        [Test]
        public void SetGoalVerb_ReachesThePlugin()
        {
            var target = new GameObject("ee").AddComponent<SelectableMarker>();
            target.SetKind(SelectionKind.EndEffector);
            ui.Bound["set-goal"](target);
            Assert.AreSame(target, planner.GoalTarget);
            Object.DestroyImmediate(target.gameObject);
        }

        // The plan's handle lives under the plugin, so a marker says which robot it is for.
        [Test]
        public void APlansHandle_BelongsToTheActiveRobot()
        {
            var robots = Erupt.Robot.Tests.FakeRobots.Registry(new Erupt.Ros.Tests.FakeRosBus());
            var robotGo = new GameObject("another planner");
            try
            {
                var left = Erupt.Robot.Tests.FakeRobots.Add(robots, "left", "left", new Erupt.Robot.Tests.FakeRobotModel("j1"));
                var right = Erupt.Robot.Tests.FakeRobots.Add(robots, "right", "right", new Erupt.Robot.Tests.FakeRobotModel("j1"));
                robots.SetActive(right);
                var other = robotGo.AddComponent<FakePlanner>();
                ((IEruptPlugin)other).OnRegister(new EruptContext { Ui = new FakeUiHost(), Undo = new UndoStack(), Robots = robots });

                other.RequestPlan(new PlanPreferences(), _ => { });

                Assert.AreSame(right, robots.OwnerOf(other.Results[0].gameObject));
                Assert.AreNotSame(left, robots.OwnerOf(other.Results[0].gameObject));
            }
            finally
            {
                Object.DestroyImmediate(robotGo);
                Erupt.Robot.Tests.FakeRobots.DestroyAll(robots);
            }

            // Without robots in the context the handle is simply unowned.
            planner.RequestPlan(new PlanPreferences(), _ => { });
            Assert.IsNull(planner.Results[0].GetComponent<Erupt.Robot.RobotOwned>());
        }

        [Test]
        public void Plan_SpawnsATrajectorySelectable_AndRaisesPlanProduced()
        {
            PlanResult seen = null;
            planner.PlanProduced += r => seen = r;

            ui.Bound["plan"](null);

            Assert.AreEqual(1, planner.PlanCalls);
            Assert.IsNotNull(seen);
            Assert.AreSame(seen, planner.LastResult);
            Assert.AreEqual("fake", seen.PlannerId, "PublishResult fills the planner id.");
            Assert.AreEqual(1, planner.Results.Count);
            var selectable = planner.Results[0];
            Assert.AreEqual(SelectionKind.Trajectory, selectable.Kind, "Trajectory verbs need a Trajectory selectable to attach to.");
            Assert.AreSame(seen, selectable.Result);
            Assert.AreSame(planner, selectable.Owner);
        }

        [Test]
        public void PreviewAndExecuteVerbs_UseTheSelectedTrajectory()
        {
            ui.Bound["plan"](null);
            var selectable = planner.Results[0];

            ui.Bound["preview"](selectable);
            ui.Bound["execute"](selectable);
            Assert.AreEqual(1, planner.PreviewCalls);
            Assert.AreEqual(1, planner.ExecuteCalls);

            // A non-trajectory selection does nothing rather than throwing.
            var obstacle = new GameObject("o").AddComponent<SelectableMarker>();
            ui.Bound["preview"](obstacle);
            Assert.AreEqual(1, planner.PreviewCalls);
            Object.DestroyImmediate(obstacle.gameObject);
        }

        [UnityTest]
        public IEnumerator Unregister_DestroysTheTrajectorySelectables()
        {
            ui.Bound["plan"](null);
            var selectableGo = planner.Results[0].gameObject;

            ((IEruptPlugin)planner).OnUnregister(context);
            yield return null;

            Assert.IsTrue(selectableGo == null, "Trajectory selectables belong to the registration.");
            Assert.IsEmpty(planner.Results);
            Assert.IsFalse(planner.IsRegistered);
        }
    }
}
