using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// The Demonstration family's contract: a demos tab and the "correct" verb on
    /// registration, samples only while Teach mode and recording are both on, and
    /// leaving Teach ends the demonstration.
    /// </summary>
    public class DemonstrationPluginTests
    {
        private GameObject go;
        private FakeDemonstrator plugin;
        private FakeUiHost ui;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("demonstrator");
            plugin = go.AddComponent<FakeDemonstrator>();
            ui = new FakeUiHost();
            context = new EruptContext { Ui = ui, Undo = new UndoStack() };
            ((IEruptPlugin)plugin).OnRegister(context);
        }

        [TearDown]
        public void TearDown()
        {
            InteractionSampleBus.Reset();
            Object.DestroyImmediate(go);
        }

        private static InteractionSample Sample() =>
            new InteractionSample(Modality.Controller, 1f, 0.0, Pose.identity, "right");

        [Test]
        public void Register_ContributesTheDemosTab_AndBindsCorrect()
        {
            Assert.AreEqual(new[] { "demos-fake-demo" }, ui.Tabs);
            Assert.Contains("correct", ui.Bound.Keys.ToList());
            Assert.IsEmpty(ui.Registered, "'correct' already exists in the verb table; it is bound, not added.");
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsFalse(plugin.InTeachMode, "No ModeManager in the context means not in Teach.");
        }

        [Test]
        public void CorrectVerb_OutsideTeach_DoesNotStart()
        {
            ui.Bound["correct"](null);
            Assert.AreEqual(0, plugin.StartCalls);
            Assert.IsFalse(plugin.IsRecording);
        }

        [Test]
        public void Samples_ReachThePlugin_OnlyWhileTeachAndRecording()
        {
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples, "not recording");

            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples, "in Teach but not recording");

            ui.Bound["correct"](null);
            Assert.AreEqual(1, plugin.StartCalls);
            Assert.IsTrue(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(2, plugin.Samples);

            plugin.StopDemonstration();
            Assert.IsFalse(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(2, plugin.Samples, "stopped");
        }

        [Test]
        public void LeavingTeach_StopsTheDemonstration()
        {
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            plugin.StartDemonstration();
            Assert.IsTrue(plugin.IsRecording);

            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Plan);

            Assert.AreEqual(1, plugin.StopCalls);
            Assert.IsFalse(plugin.IsRecording);
            Assert.IsFalse(plugin.InTeachMode);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples);
        }

        [Test]
        public void Unregister_StopsTheSampleFeed()
        {
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            plugin.StartDemonstration();

            ((IEruptPlugin)plugin).OnUnregister(context);

            Assert.IsFalse(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples);
        }

        [Test]
        public void Register_WithoutUiHost_SkipsTheTab_AndStillGatesRecording()
        {
            var bare = new GameObject("bare").AddComponent<FakeDemonstrator>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No UI host"));
            ((IEruptPlugin)bare).OnRegister(new EruptContext { Undo = new UndoStack() });
            Assert.IsTrue(bare.IsRegistered);
            ((IEruptPlugin)bare).OnModeChanged(AppMode.Teach);
            bare.StartDemonstration();
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(1, bare.Samples);
            ((IEruptPlugin)bare).OnUnregister(bare.Context);
            Object.DestroyImmediate(bare.gameObject);
        }
    }
}
