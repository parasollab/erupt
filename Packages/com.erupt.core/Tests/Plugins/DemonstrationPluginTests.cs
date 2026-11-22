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
    /// registration, samples only while a demonstration is being recorded, and no mode to
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
        public void Register_ContributesTheDemosTab_AndBindsCorrect()
        {
            Assert.AreEqual(new[] { "demos-fake-demo" }, ui.Tabs);
            Assert.Contains("correct", ui.Bound.Keys.ToList());
            Assert.IsEmpty(ui.Registered, "'correct' already exists in the verb table; it is bound, not added.");
            Assert.IsFalse(plugin.IsRecording);
        }

        // "Correct" on a plan starts a demonstration. It is on every plan's menu while the
        // plugin is registered, and gone from the menus once the plugin is.
        [Test]
        public void CorrectVerb_IsOnAPlansMenu_WhileThePluginIsRegistered()
        {
            var plan = new GameObject("plan").AddComponent<SelectableMarker>();
            plan.SetKind(SelectionKind.Trajectory);
            int refreshes = 0;
            ui.Verbs.Changed += () => refreshes++;
            try
            {
                Assert.IsTrue(ui.Verbs.For(SelectionKind.Trajectory).First(v => v.Id == "correct").IsAvailable, "No longer marked pending.");
                Assert.IsTrue(ui.Verbs.IsShownFor(SelectionKind.Trajectory, "correct", plan), "From registration on, with nothing to switch first.");
                CollectionAssert.Contains(ui.Verbs.For(SelectionKind.Trajectory, plan).Select(v => v.Id).ToArray(), "correct");

                ((IEruptPlugin)plugin).OnUnregister(context);
                Assert.IsFalse(ui.Verbs.IsShownFor(SelectionKind.Trajectory, "correct", plan), "Once the plugin is gone the verb is gone from the menus.");
                CollectionAssert.DoesNotContain(ui.Verbs.For(SelectionKind.Trajectory, plan).Select(v => v.Id).ToArray(), "correct");
                Assert.GreaterOrEqual(refreshes, 1, "An open menu is rebuilt when the plugin goes.");
                CollectionAssert.DoesNotContain(ui.Tabs, "demos-fake-demo", "Its tab goes with it.");
            }
            finally
            {
                Object.DestroyImmediate(plan.gameObject);
            }
        }

        [Test]
        public void CorrectVerb_StartsADemonstration_AndOpensTheDemosTab()
        {
            Assert.IsNull(ui.Summoned);

            ui.Bound["correct"](null);

            Assert.AreEqual(1, plugin.StartCalls);
            Assert.IsTrue(plugin.IsRecording);
            Assert.AreEqual("demos-fake-demo", ui.Summoned, "The recording shows, and is stopped, in the demos tab.");
        }

        // There is no mode in front of recording: the verb never answers "switch to ... first".
        [Test]
        public void CorrectVerb_IsNeverRefusedForAMode()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            var router = routerGo.AddComponent<InteractionRouter>();
            var refusals = new System.Collections.Generic.List<InteractionRefusal>();
            router.Refused += refusals.Add;
            context.Router = router;
            try
            {
                ui.Bound["correct"](null);
                Assert.AreEqual(1, plugin.StartCalls);
                Assert.IsEmpty(refusals);
                Assert.AreEqual("demos-fake-demo", ui.Summoned);
            }
            finally
            {
                context.Router = null;
                Object.DestroyImmediate(routerGo);
            }
        }

        [Test]
        public void Samples_ReachThePlugin_OnlyWhileRecording()
        {
            InteractionSampleBus.Publish(Sample());
            Assert.AreEqual(0, plugin.Samples, "not recording");

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
