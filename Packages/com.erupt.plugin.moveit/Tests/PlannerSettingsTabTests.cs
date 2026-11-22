using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;
using RosMessageTypes.Moveit;

namespace Erupt.Plugins.MoveIt.Tests
{
    /// <summary>The tier 3 planner tab: element budget (Part 8) and that it drives the plugin's preferences.</summary>
    public class PlannerSettingsTabTests
    {
        private FakeRosBus bus;
        private GameObject pluginGo, canvasGo;
        private MoveItPlugin plugin;
        private PlannerSettingsTab tab;
        private RobotRegistry robots;

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            bus.SetServiceHandler("/query_planner_interface", _ => new QueryPlannerInterfacesResponse
            {
                planner_interfaces = new[]
                {
                    new PlannerInterfaceDescriptionMsg { pipeline_id = "ompl", planner_ids = new[] { "RRTConnect", "PRM" } },
                    new PlannerInterfaceDescriptionMsg { pipeline_id = "chomp", planner_ids = new[] { "chomp" } }
                }
            });
            pluginGo = new GameObject("moveit");
            pluginGo.SetActive(false);
            plugin = pluginGo.AddComponent<MoveItPlugin>();
            robots = FakeRobots.Single(bus, new FakeRobotModel("j1"));
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack() });

            var canvas = UiBuilder.CreateWorldCanvas("Tier3", null, new Vector2(800f, 900f));
            canvasGo = canvas.gameObject;
            var content = UiBuilder.CreatePanel("Content", canvas.transform, Color.clear);
            tab = new PlannerSettingsTab(plugin, content);
        }

        [TearDown]
        public void TearDown()
        {
            tab.Dispose();
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(pluginGo);
            FakeRobots.DestroyAll(robots);
        }

        [Test]
        public void Register_AddsSetStartAndPlanAsPluginVerbsOnTheEndEffector()
        {
            var ui = (FakeUiHost)plugin.Context.Ui;
            CollectionAssert.AreEquivalent(new[] { "set-start", "plan" },
                ui.Registered.Where(r => r.kind == SelectionKind.EndEffector).Select(r => r.id));
            // A plan's menu gets Stop Preview, shown while that plan's preview plays.
            CollectionAssert.AreEquivalent(new[] { PlanningPlugin.StopPreviewVerb },
                ui.Registered.Where(r => r.kind == SelectionKind.Trajectory).Select(r => r.id));
            Assert.IsTrue(ui.Registered.All(r => r.plugin == "moveit"));
            Assert.IsFalse(plugin.HasStart);
            ui.Bound["set-goal"](null);
            Assert.IsTrue(plugin.HasGoal);
            Assert.IsFalse(plugin.HasStart, "set-goal no longer captures the start implicitly.");
            ui.Bound["set-start"](null);
            Assert.IsTrue(plugin.HasStart);
        }

        [Test]
        public void Tab_StaysWithinTheSevenElementBudget()
        {
            Assert.LessOrEqual(tab.InteractiveElementCount, 7, "Part 8: ~7 interactive elements with a panel open.");
            Assert.GreaterOrEqual(tab.InteractiveElementCount, 6);
        }

        [Test]
        public void Preferences_DefaultToTheSettings_ThenFollowThePlannerQuery()
        {
            var before = tab.Preferences;
            Assert.AreEqual(plugin.Settings.planningPipelineId, before.PipelineId);
            Assert.AreEqual(plugin.Settings.defaultNumPlanningAttempts, before.Attempts);

            Assert.IsTrue(plugin.Client.QueryPlanners());

            var after = tab.Preferences;
            Assert.AreEqual("ompl", after.PipelineId, "The configured pipeline is chosen when MoveIt offers it.");
            Assert.AreEqual(plugin.Settings.defaultPlannerId, after.PlannerId,
                "No planner is named until one is picked: MoveIt then uses the one it configured for the group.");

            // The planner button walks the pipeline's planners and comes back to the default.
            var offered = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 20; i++)
            {
                PlannerButton().onClick.Invoke();
                if (tab.Preferences.PlannerId == plugin.Settings.defaultPlannerId) break;
                offered.Add(tab.Preferences.PlannerId);
            }
            CollectionAssert.AreEqual(plugin.Client.PlannersFor("ompl"), offered);
            CollectionAssert.Contains(offered, "RRTConnect");
        }

        private UnityEngine.UI.Button PlannerButton() =>
            tab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Planner");

        [Test]
        public void PlanVerb_UsesTheTabsPreferences()
        {
            plugin.Client.QueryPlanners();
            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/plan_kinematic_path", req => { sent = (GetMotionPlanRequest)req; return new GetMotionPlanResponse
            {
                motion_plan_response = new MotionPlanResponseMsg { error_code = new MoveItErrorCodesMsg { val = -1, message = "x" } }
            }; });
            // Start and goal come from the robot's current joint state.
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();

            // Pick a planner by name, as the user does with the planner button.
            for (int i = 0; i < 20 && tab.Preferences.PlannerId != "RRTConnect"; i++) PlannerButton().onClick.Invoke();
            plugin.RequestPlan(tab.Preferences, _ => { });

            Assert.IsNotNull(sent);
            Assert.AreEqual("ompl", sent.motion_plan_request.pipeline_id);
            Assert.AreEqual("RRTConnect", sent.motion_plan_request.planner_id);
            Assert.AreEqual(10, sent.motion_plan_request.num_planning_attempts);
        }
    }
}
