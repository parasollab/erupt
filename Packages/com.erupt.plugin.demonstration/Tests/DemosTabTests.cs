using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;
using Erupt.Robot;

namespace Erupt.Plugins.Demonstration.Tests
{
    /// <summary>The tier 3 tab built for real: element budget (Part 8) and the state it mirrors.</summary>
    public class DemosTabTests
    {
        private GameObject go, canvasGo;
        private DemonstrationCollectionPlugin plugin;
        private FakeRosBus ros;
        private EruptContext context;
        private RobotRegistry robots;
        private DemosTab tab;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("DemonstrationCollectionPlugin");
            go.AddComponent<JointTrajectoryPlayer>();
            plugin = go.AddComponent<DemonstrationCollectionPlugin>();
            ros = new FakeRosBus();
            robots = FakeRobots.Single(ros, new FakeRobotModel("j1"));
            context = new EruptContext { Ros = ros, Robots = robots, Ui = new FakeUiHost(), Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);

            var canvas = UiBuilder.CreateWorldCanvas("Canvas", null, new Vector2(600, 800));
            canvasGo = canvas.gameObject;
            var content = UiBuilder.CreatePanel("Content", canvas.transform, Color.clear);
            tab = new DemosTab(plugin, content);
        }

        [TearDown]
        public void TearDown()
        {
            tab.Dispose();
            ((IEruptPlugin)plugin).OnUnregister(context);
            InteractionSampleBus.Reset();
            FakeRobots.DestroyAll(robots);
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Tab_StaysWithinTheInteractiveBudget()
        {
            Assert.LessOrEqual(tab.InteractiveElementCount, 7);
            Assert.AreEqual(5, tab.InteractiveElementCount);
        }

        [Test]
        public void Record_IsAlwaysAvailable_AndToggles()
        {
            Assert.IsTrue(tab.RecordButton.interactable, "Nothing has to be switched before recording.");
            Assert.AreEqual("Record", tab.RecordButton.GetComponentInChildren<TMPro.TextMeshProUGUI>().text);
            Assert.AreEqual("Ready to record", tab.Root.Find("Demonstrations/Status").GetComponent<TMPro.TextMeshProUGUI>().text);

            tab.RecordButton.onClick.Invoke();
            Assert.IsTrue(plugin.IsRecording);
            Assert.AreEqual("Stop", tab.RecordButton.GetComponentInChildren<TMPro.TextMeshProUGUI>().text);
            Assert.IsTrue(tab.RecordButton.interactable, "A recording is stopped with the same button.");
            tab.RecordButton.onClick.Invoke();
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
        }    }
}
