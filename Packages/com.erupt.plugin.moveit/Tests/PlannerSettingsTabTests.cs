using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
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
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = new FakeUiHost(), Undo = new UndoStack() });

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
        }

        [Test]
        public void Register_AddsSetStartAndPlanAsPluginVerbsOnTheEndEffector()
        {
            var ui = (FakeUiHost)plugin.Context.Ui;
            CollectionAssert.AreEquivalent(new[] { "set-start", "plan" }, ui.Registered.Select(r => r.id));
            Assert.IsTrue(ui.Registered.All(r => r.kind == SelectionKind.EndEffector && r.plugin == "moveit"));
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
            Assert.AreEqual("RRTConnect", after.PlannerId, "First planner of the pipeline when the default is not offered.");
        }

        [Test]
        public void PlanVerb_UsesTheTabsPreferences()
        {
            plugin.Client.QueryPlanners();
            GetMotionPlanRequest sent = null;
            bus.SetServiceHandler("/plan_kinematic_path", req => { sent = (GetMotionPlanRequest)req; return new GetMotionPlanResponse
            {
                motion_plan_response = new MotionPlanResponseMsg { error_code = new MoveItErrorCodesMsg { val = -1, message = "x" } }
            }; });
            // Start and goal come from the robot; a null robot yields empty states, which is fine for the request.
            plugin.SetStartFromRobot();
            plugin.SetGoalFromRobot();

            plugin.RequestPlan(tab.Preferences, _ => { });

            Assert.IsNotNull(sent);
            Assert.AreEqual("ompl", sent.motion_plan_request.pipeline_id);
            Assert.AreEqual("RRTConnect", sent.motion_plan_request.planner_id);
            Assert.AreEqual(10, sent.motion_plan_request.num_planning_attempts);
        }
    }
}
