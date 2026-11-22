#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins.Tests;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;

namespace Erupt.Plugins.Ar.Tests
{
    /// <summary>
    /// The plugin prefab the AR sample scene uses (<c>Prefabs/ArPlugin.prefab</c>: the plugin
    /// and the tracker), instantiated in Play mode on a fake bus. There is no headset, so there
    /// is no passthrough camera and the tracker is inert; the plugin registers and serves the
    /// robots all the same.
    /// </summary>
    public class ArPluginPrefabTests
    {
        private const string PrefabPath = "Packages/com.erupt.plugin.ar/Prefabs/ArPlugin.prefab";

        private FakeRosBus bus;
        private RobotRegistry robots;
        private GameObject instance;
        private ArPlugin plugin;

        [TearDown]
        public void TearDown()
        {
            if (plugin != null && plugin.IsRegistered) ((IEruptPlugin)plugin).OnUnregister(plugin.Context);
            if (instance != null) Object.DestroyImmediate(instance);
            FakeRobots.DestroyAll(robots);
        }

        [UnityTest]
        public IEnumerator ThePrefab_RegistersOnAFakeBus_WithItsTrackerInert_AndPlacesARobotFromAnInjectedTag()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(prefab, PrefabPath);
            instance = Object.Instantiate(prefab);
            plugin = instance.GetComponent<ArPlugin>();
            var tracker = instance.GetComponent<AprilTagTracker>();
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(tracker);
            Assert.AreEqual(0, instance.GetComponentsInChildren<Component>(true).Count(c => c == null), "No missing script on the prefab.");

            bus = new FakeRosBus();
            robots = FakeRobots.Three(bus);
            RobotInstance third = robots.All[2];
            var anchorGo = new GameObject("MarkerAnchor");
            anchorGo.transform.SetParent(third.transform, false);
            anchorGo.AddComponent<MarkerAnchor>().Configure(7, 0.10f, new Vector3(0f, 0f, 0.5f), Vector3.zero);

            var ui = new FakeUiHost();
            var undo = new UndoStack();
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = undo });
            for (int i = 0; i < 5; i++) yield return null;   // the plugin and the tracker run their updates

            Assert.AreSame(tracker, plugin.Tracker, "The prefab's plugin reads the prefab's tracker.");
            Assert.AreEqual(3, plugin.Sessions.Count);
            Assert.IsFalse(tracker.IsReady, "No headset: no passthrough camera.");
            Assert.AreEqual("no passthrough camera", tracker.StatusText);
            Assert.IsTrue(tracker.TryGetTagSize(7, out float size), "The plugin had the tracker look for the anchor's tag.");
            Assert.AreEqual(0.10f, size);
            Assert.IsEmpty(tracker.Tags);
            Assert.IsEmpty(bus.Publishes);
            CollectionAssert.Contains(ui.Tabs, ArPlugin.TabId);
            LogAssert.NoUnexpectedReceived();

            // The same path a detection takes, without a camera.
            Assert.IsTrue(tracker.InjectObservation(7, MarkerMathTests.FlatTag(new Vector3(1f, 0f, 2f))));
            yield return null;
            Assert.IsTrue(plugin.SessionFor(third).HasProposal);
            Assert.AreEqual(Vector3.zero, third.transform.position);
            ui.Bound[ArPlugin.PlaceVerb](FakeRobots.EndEffectorOf(third));
            Assert.Less(Vector3.Distance(new Vector3(1f, 0f, 2.5f), third.transform.position), 1e-4f);
            Assert.AreEqual(Vector3.zero, robots.All[0].transform.position);
        }
    }
}
#endif
