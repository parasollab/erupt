using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;

namespace Erupt.Plugins.Tests
{
    public class PluginHostTests
    {
        private GameObject hostGo;
        private PluginHost host;
        private EruptContext context;

        [SetUp]
        public void SetUp()
        {
            FakePlugin.RegisterOrder.Clear();
            hostGo = new GameObject("host");
            hostGo.SetActive(false);
            host = hostGo.AddComponent<PluginHost>();
            context = new EruptContext { Ui = new FakeUiHost(), Undo = new UndoStack() };
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

        // ---------- robots ----------

        [Test]
        public void TheContext_ExposesTheRegistry_AndTheActiveRobot()
        {
            var bus = new FakeRosBus();
            var model = new FakeRobotModel("j1");
            RobotRegistry robots = FakeRobots.Registry(bus);
            try
            {
                context.Robots = robots;
                Assert.IsNull(context.Robots.Active, "No robot registered yet.");
                RobotInstance left = FakeRobots.Add(robots, "left", "left", model);
                RobotInstance right = FakeRobots.Add(robots, "right", "right", new FakeRobotModel("k1"));
                Assert.AreSame(model, context.Robots.Active.Model, "The first robot is the active one.");
                robots.SetActive(right);
                Assert.AreSame(right.Model, context.Robots.Active.Model);
                Assert.AreSame(left, robots.All[0]);
            }
            finally
            {
                FakeRobots.DestroyAll(robots);
            }
        }

        // Without a supplied context the host builds one: the scene's robots are in the default
        // registry, which gets the bus, so each robot can resolve its names.
        [UnityTest]
        public IEnumerator Start_BuildsTheContextFromTheSceneRobots_AndGivesThemTheBus()
        {
            var bus = new FakeRosBus();
            RosBus.Override(bus);
            var sceneGo = new GameObject("scene services");
            var robotGo = new GameObject("fr3");
            try
            {
                sceneGo.SetActive(false);
                var environment = sceneGo.AddComponent<EnvironmentRegistry>();
                sceneGo.AddComponent<SelectionService>();
                var sceneHost = sceneGo.AddComponent<PluginHost>();
                sceneHost.DiscoverInScene = false;
                var robot = robotGo.AddComponent<RobotInstance>();
                Assert.IsNull(robot.Ros, "No bus before the host has started.");

                sceneGo.SetActive(true);
                yield return null;

                Assert.AreSame(RobotRegistry.Default, sceneHost.Context.Robots);
                Assert.AreSame(robot, sceneHost.Context.Robots.Active);
                Assert.AreEqual("fr3", robot.Id);
                Assert.AreSame(environment.WorldOrigin, robot.PlanningFrame, "The planning frame defaults to the scene's world origin.");

                robot.Ros.RegisterPublisher<RosMessageTypes.Std.StringMsg>("status");
                CollectionAssert.AreEqual(new[] { "/status" }, bus.RegisteredPublishers);
            }
            finally
            {
                Object.DestroyImmediate(sceneGo);
                Object.DestroyImmediate(robotGo);
                RosBus.Reset();
            }
        }

        // A scene unload destroys objects in no particular order: a plugin may go before the host.
        [Test]
        public void APluginDestroyedWhileRegistered_HasTheHostUnregisterAll_DependantsFirst()
        {
            var order = new System.Collections.Generic.List<string>();
            var baseGo = new GameObject("base plugin");
            var dependantGo = new GameObject("dependant plugin");
            var basePlugin = baseGo.AddComponent<OrderedPluginBehaviour>();
            basePlugin.Configure("base", order);
            var dependant = dependantGo.AddComponent<OrderedPluginBehaviour>();
            dependant.Configure("dependant", order, "base");
            host.RegisterAll(new IEruptPlugin[] { dependant, basePlugin });
            Assert.IsTrue(basePlugin.IsRegistered && dependant.IsRegistered);

            // The plugin the other depends on is destroyed first.
            Object.DestroyImmediate(baseGo);

            CollectionAssert.AreEqual(new[] { "dependant", "base" }, order,
                "Both are unregistered at once and in order; a destroyed plugin left registered was called back dead.");
            Assert.IsFalse(dependant.IsRegistered);
            Assert.IsEmpty(host.Plugins);

            Object.DestroyImmediate(dependantGo);
            Assert.AreEqual(2, order.Count, "Nobody is unregistered twice.");
        }

        private sealed class OrderedPluginBehaviour : EruptPluginBehaviour
        {
            private string id;
            private string[] deps = System.Array.Empty<string>();
            private System.Collections.Generic.List<string> order;
            public void Configure(string pluginId, System.Collections.Generic.List<string> unregistered, params string[] dependsOn)
            {
                id = pluginId;
                order = unregistered;
                deps = dependsOn;
            }
            public override string Id => id;
            public override string DisplayName => id;
            public override System.Collections.Generic.IReadOnlyList<string> DependsOn => deps;
            protected override void OnUnregister(IEruptContext context) => order.Add(id);
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
        }
    }
}
