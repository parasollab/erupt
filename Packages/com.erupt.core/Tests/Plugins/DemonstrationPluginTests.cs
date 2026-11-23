using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// The Demonstration family's contract: a demos tab on registration (and no verb),
    /// samples only while a demonstration is being recorded, and no mode to
    /// enter first: recording starts and stops when the user says so.
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
        public void Register_ContributesTheDemosTab_AndNoVerb()
        {
            Assert.AreEqual(new[] { "demos-fake-demo" }, ui.Tabs);
            Assert.IsEmpty(ui.Bound, "The family binds no verb: a demonstration is started from its tab.");
            Assert.IsEmpty(ui.Registered);
            Assert.IsFalse(plugin.IsRecording);
        }

        // "Correct" on a plan is in the guidelines and not built: it is in the table, marked
        // pending, and a registered demonstration plugin does not put it on a plan's menu.
        [Test]
        public void Correct_IsNotOnAPlansMenu()
        {
            Verb correct = ui.Verbs.For(SelectionKind.Trajectory).First(v => v.Id == "correct");
            Assert.IsFalse(correct.IsAvailable);
            Assert.IsFalse(ui.Bound.ContainsKey("correct"));
        }

        [Test]
        public void Unregister_RemovesTheDemosTab()
        {
            ((IEruptPlugin)plugin).OnUnregister(context);
            CollectionAssert.DoesNotContain(ui.Tabs, "demos-fake-demo", "Its tab goes with it.");
        }

        [Test]
        public void Samples_ReachThePlugin_OnlyWhileRecording()
        {
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples, "not recording");

            plugin.StartDemonstration();
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

        // A recording runs until it is stopped; nothing else ends it.
        [Test]
        public void ARecording_RunsUntilItIsStopped()
        {
            plugin.StartDemonstration();
            Assert.IsTrue(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(1, plugin.Samples);
            Assert.AreEqual(0, plugin.StopCalls);

            plugin.StopDemonstration();

            Assert.AreEqual(1, plugin.StopCalls);
            Assert.IsFalse(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(1, plugin.Samples);
        }

        [Test]
        public void Unregister_StopsTheSampleFeed()
        {
            plugin.StartDemonstration();

            ((IEruptPlugin)plugin).OnUnregister(context);

            Assert.IsFalse(plugin.IsRecording);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples);
        }

        [Test]
        public void Register_WithoutUiHost_SkipsTheTab_AndStillRecords()
        {
            var bare = new GameObject("bare").AddComponent<FakeDemonstrator>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No UI host"));
            ((IEruptPlugin)bare).OnRegister(new EruptContext { Undo = new UndoStack() });
            Assert.IsTrue(bare.IsRegistered);
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, bare.Samples, "Nothing is fed before a demonstration is started.");
            bare.StartDemonstration();
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(1, bare.Samples);
            ((IEruptPlugin)bare).OnUnregister(bare.Context);
            Object.DestroyImmediate(bare.gameObject);
        }
    }
}
