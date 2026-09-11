using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;

namespace Erupt.Plugins.Tests
{
    public class PluginHostTests
    {
        private GameObject hostGo;
        private PluginHost host;
        private ModeManager modes;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            FakePlugin.RegisterOrder.Clear();
            hostGo = new GameObject("host");
            hostGo.SetActive(false);
            modes = hostGo.AddComponent<ModeManager>();
            host = hostGo.AddComponent<PluginHost>();
            context = new EruptContext { Modes = modes, Ui = new FakeUiHost(), Undo = new UndoStack() };
            host.Initialise(context);
            // Discovery would pick up FakePluginBehaviours from other tests; those tests opt in.
            host.DiscoverInScene = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (hostGo != null) Object.DestroyImmediate(hostGo);
        }

        [Test]
        public void RegisterAll_OrdersByDependency()
        {
            var a = new FakePlugin("a");
            var b = new FakePlugin("b", "a");
            var c = new FakePlugin("c", "b", "a");

            host.RegisterAll(new IEruptPlugin[] { c, b, a });

            Assert.AreEqual(new[] { "a", "b", "c" }, FakePlugin.RegisterOrder);
            Assert.AreSame(context, a.Registered, "Each plugin gets the host's context.");
        }

        [Test]
        public void RegisterAll_RefusesMissingDependency_AndRegistersNothing()
        {
            var b = new FakePlugin("b", "a");
            Assert.Throws<PluginDependencyException>(() => host.RegisterAll(new IEruptPlugin[] { b }));
            Assert.IsEmpty(host.Plugins);
            Assert.IsNull(b.Registered);
        }

        [Test]
        public void RegisterAll_RefusesCycle_AndRegistersNothing()
        {
            var a = new FakePlugin("a", "b");
            var b = new FakePlugin("b", "a");
            var ex = Assert.Throws<PluginDependencyException>(() => host.RegisterAll(new IEruptPlugin[] { a, b }));
            StringAssert.Contains("cycle", ex.Message);
            Assert.IsEmpty(host.Plugins);
        }

        [Test]
        public void Register_LaterPlugin_MaySatisfyDependencyFromEarlierRegistration()
        {
            host.Register(new FakePlugin("a"));
            Assert.DoesNotThrow(() => host.Register(new FakePlugin("b", "a")));
            Assert.AreEqual(2, host.Plugins.Count);
        }

        [Test]
        public void Register_RefusesDuplicateId()
        {
            host.Register(new FakePlugin("a"));
            Assert.Throws<PluginDependencyException>(() => host.Register(new FakePlugin("a")));
        }

        [UnityTest]
        public IEnumerator ModeChanges_FanOutToEveryPlugin_InOrder()
        {
            var a = new FakePlugin("a");
            var b = new FakePlugin("b", "a");
            hostGo.SetActive(true);
            yield return null;                       // Start subscribes to the ModeManager
            host.RegisterAll(new IEruptPlugin[] { b, a });

            modes.SetMode(AppMode.Plan);
            modes.SetMode(AppMode.Teach);

            Assert.AreEqual(new[] { AppMode.Plan, AppMode.Teach }, a.Modes);
            Assert.AreEqual(new[] { AppMode.Plan, AppMode.Teach }, b.Modes);
        }

        [UnityTest]
        public IEnumerator Destroy_UnregistersInReverseOrder()
        {
            var order = new System.Collections.Generic.List<string>();
            var a = new RecordingPlugin("a", order);
            var b = new RecordingPlugin("b", order, "a");
            hostGo.SetActive(true);
            yield return null;
            host.RegisterAll(new IEruptPlugin[] { a, b });

            Object.DestroyImmediate(hostGo);
            hostGo = null;

            Assert.AreEqual(new[] { "b", "a" }, order, "Unregister runs in reverse dependency order.");
        }

        [UnityTest]
        public IEnumerator Start_DiscoversSceneBehaviours_AndUnregistersOnDestroy()
        {
            host.DiscoverInScene = true;
            var pluginGo = new GameObject("scene plugin");
            var scenePlugin = pluginGo.AddComponent<FakePluginBehaviour>();
            scenePlugin.id = "scene";

            hostGo.SetActive(true);
            yield return null;

            Assert.AreEqual(1, scenePlugin.registerCalls, "Start must find and register scene plugins.");
            Assert.IsTrue(scenePlugin.IsRegistered);
            Assert.IsTrue(host.TryGet("scene", out _));

            Object.DestroyImmediate(hostGo);
            hostGo = null;
            Assert.AreEqual(1, scenePlugin.unregisterCalls);
            Assert.IsFalse(scenePlugin.IsRegistered);
            Object.DestroyImmediate(pluginGo);
        }

        private sealed class RecordingPlugin : IEruptPlugin
        {
            private readonly System.Collections.Generic.List<string> order;
            public RecordingPlugin(string id, System.Collections.Generic.List<string> order, params string[] deps) { Id = id; this.order = order; DependsOn = deps; }
            public string Id { get; }
            public string DisplayName => Id;
            public System.Collections.Generic.IReadOnlyList<string> DependsOn { get; }
            public void OnRegister(IEruptContext context) { }
            public void OnUnregister(IEruptContext context) => order.Add(Id);
            public void OnModeChanged(AppMode mode) { }
        }
    }
}
