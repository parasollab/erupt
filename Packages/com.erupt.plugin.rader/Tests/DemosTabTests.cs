using NUnit.Framework;
using RosMessageTypes.Std;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;
using Erupt.Ui;

namespace Erupt.Plugins.Rader.Tests
{
    /// <summary>The tier 3 tab built for real: element budget (Part 8) and the state it mirrors.</summary>
    public class DemosTabTests
    {
        private GameObject go, canvasGo;
        private RaderPlugin plugin;
        private FakeRosBus ros;
        private EruptContext context;
        private DemosTab tab;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("RaderPlugin");
            go.AddComponent<JointTrajectoryPlayer>();
            plugin = go.AddComponent<RaderPlugin>();
            ros = new FakeRosBus();
            context = new EruptContext { Ros = ros, Robot = new FakeRobotModel("j1"), Ui = new FakeUiHost(), Undo = new UndoStack() };
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
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Tab_StaysWithinTheInteractiveBudget()
        {
            Assert.LessOrEqual(tab.InteractiveElementCount, 7);
            Assert.AreEqual(6, tab.InteractiveElementCount);
        }

        [Test]
        public void Record_IsDisabledOutsideTeach_AndTogglesInTeach()
        {
            Assert.IsFalse(tab.RecordButton.interactable);
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            Assert.IsTrue(tab.RecordButton.interactable);

            tab.RecordButton.onClick.Invoke();
            Assert.IsTrue(plugin.IsRecording);
            tab.RecordButton.onClick.Invoke();
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsNotNull(plugin.Recorder.LastDemonstration);
        }

        [Test]
        public void Resume_FollowsTheFerlHandshake()
        {
            Assert.IsFalse(tab.ResumeButton.interactable);
            ros.Inbound("/feedback_request", new BoolMsg(true));
            Assert.IsFalse(tab.ResumeButton.interactable, "requested but not satisfied");
            ros.Inbound("/req_satisfied", new BoolMsg(true));
            Assert.IsTrue(tab.ResumeButton.interactable);
            tab.ResumeButton.onClick.Invoke();
            Assert.AreEqual(1, ros.CountOn("/feedback_response"));
            Assert.IsFalse(tab.ResumeButton.interactable);
        }
    }
}
