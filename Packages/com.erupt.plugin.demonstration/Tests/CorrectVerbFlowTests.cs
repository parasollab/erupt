using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>
    /// "Correct" on a plan, through the real tier UI: a planner's plan is selected, the verb
    /// is on the plan's menu (there is no mode to switch to first) and pressing it records a
    /// demonstration on the plan's robot, which can be replayed and published. Without the
    /// demonstration plugin the verb is not on the menu.
    /// </summary>
    public class CorrectVerbFlowTests
    {
        private GameObject eruptGo, plannerGo, demoGo;
        private SelectionService selection;
        private TierUiRig rig;
        private PluginHost host;
        private FakePlanner planner;
        private DemonstrationCollectionPlugin demonstrations;
        private FakeRosBus bus;
        private RobotRegistry robots;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            robots = FakeRobots.Three(bus);

            eruptGo = new GameObject("ERUPT");
            eruptGo.SetActive(false);
            selection = eruptGo.AddComponent<SelectionService>();
            rig = eruptGo.AddComponent<TierUiRig>();
            host = eruptGo.AddComponent<PluginHost>();
            host.DiscoverInScene = false;
            host.Initialise(new EruptContext { Ros = bus, Robots = robots, Selection = selection, Ui = rig, Undo = host.Undo });
            eruptGo.SetActive(true);
            yield return null;

            plannerGo = new GameObject("planner");
            planner = plannerGo.AddComponent<FakePlanner>();
            demoGo = new GameObject("demonstrations");
            demoGo.AddComponent<JointTrajectoryPlayer>();
            demonstrations = demoGo.AddComponent<DemonstrationCollectionPlugin>();
            host.RegisterAll(new IEruptPlugin[] { planner, demonstrations });
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            InteractionSampleBus.Reset();
            Object.DestroyImmediate(eruptGo);
            Object.DestroyImmediate(plannerGo);
            Object.DestroyImmediate(demoGo);
            FakeRobots.DestroyAll(robots);
        }

        private string[] Menu() => rig.TierTwo.Model.Verbs.Select(v => v.Id).ToArray();

        [UnityTest]
        public IEnumerator Correct_OnAPlan_RecordsADemonstrationOnThePlansRobot()
        {
            // A plan for the robot "right", selected by its handle.
            RobotInstance right = robots.All[1];
            planner.RequestPlan(right, new PlanPreferences(), _ => { });
            TrajectorySelectable plan = planner.Results.Single();
            selection.Select(plan);
            yield return null;

            // The verb is on the menu from the start: nothing has to be switched first.
            CollectionAssert.AreEquivalent(new[] { "execute", "preview", "correct" }, Menu(), "A plan can be corrected whenever the demonstration plugin is there.");
            Verb correct = rig.TierTwo.Model.Verbs.First(v => v.Id == "correct");
            Assert.IsTrue(rig.TierTwo.Model.CanInvoke(correct), "The button can be pressed.");
            Assert.IsNotNull(rig.TierTwo.ButtonFor("correct"));
            Assert.IsTrue(rig.TierTwo.ButtonFor("correct").interactable);

            // Pressed: a demonstration is recorded on the plan's robot, and the demos tab is open.
            Assert.IsFalse(rig.TierTwo.Invoke(correct).IsRefused);
            Assert.IsTrue(demonstrations.IsRecording);
            Assert.AreSame(right, demonstrations.RecordingRobot, "On the robot the plan is for, not on the active one by accident.");
            Assert.IsTrue(rig.TierThree.IsOpen, "The Demonstrations tab opens: the recording shows there and is stopped there.");
            Assert.AreEqual(demonstrations.DemosTabId, rig.TierThree.ActiveTab.Id);

            // The user moves the robot; the recorder samples it.
            var model = (FakeRobotModel)right.Model;
            DemonstrationRecorder recorder = demonstrations.SessionFor(right).Recorder;
            model.Positions[0] = 0.3f;
            recorder.Tick(Time.timeAsDouble + 1.0);
            model.Positions[0] = 0.6f;
            recorder.Tick(Time.timeAsDouble + 2.0);
            demonstrations.StopDemonstration();

            JointTrajectoryMsg demo = recorder.LastDemonstration;
            Assert.IsNotNull(demo, "The demonstration is there.");
            Assert.GreaterOrEqual(demo.points.Length, 2);
            Assert.AreEqual(0.6, demo.points.Last().positions[0], 1e-6);
            demonstrations.Publish(right);
            Assert.AreEqual(1, bus.CountOn("/right/joint_trajectory"), "And it is published in the robot's namespace.");

            // The verb stays on the menu after the recording: the plan can be corrected again.
            yield return null;
            CollectionAssert.AreEquivalent(new[] { "execute", "preview", "correct" }, Menu());

            // Without the demonstration plugin the plan's menu has no Correct, and the panel no Demonstrations tab.
            host.Unregister(demonstrations);
            yield return null;
            CollectionAssert.AreEquivalent(new[] { "execute", "preview" }, Menu());
            Assert.IsNull(rig.TierTwo.ButtonFor("correct"));
            CollectionAssert.DoesNotContain(rig.TierThree.Tabs.Select(t => t.Id).ToArray(), demonstrations.DemosTabId);
        }
    }
}
