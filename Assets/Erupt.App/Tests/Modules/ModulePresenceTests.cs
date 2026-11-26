#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Demonstration;
using Erupt.Plugins.MoveIt;
using Erupt.Plugins.Mtc;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;

namespace Erupt.App.Modules.Tests
{
    /// <summary>
    /// "The buttons of a module are there only when the module is": the ERUPT root prefab with
    /// a robot and, per test, some of the plugin prefabs next to it, exactly as a scene has
    /// them. The host finds the plugins by itself; the tier 2 menus (the end effector's, a
    /// plan's) and the tier 3 strip are then read as the user would see them. A module whose
    /// prefab is not in the scene (or whose package is not in the project, which comes to the
    /// same: nothing of it is there to register) leaves no button and no tab behind.
    /// </summary>
    public class ModulePresenceTests
    {
        private const string RootPrefab = "Packages/com.erupt.core/Prefabs/EruptCore.prefab";
        private const string MoveIt = "Packages/com.erupt.plugin.moveit/Prefabs/MoveItPlugin.prefab";
        private const string Mtc = "Packages/com.erupt.plugin.mtc/Prefabs/MtcPlugin.prefab";
        private const string Demonstration = "Packages/com.erupt.plugin.demonstration/Prefabs/DemonstrationCollectionPlugin.prefab";

        // What each module puts on the menus and on the strip.
        private static readonly string[] CoreEndEffector = { "Open/Close Gripper" };
        private static readonly string[] PlanningEndEffector = { "Set Start", "Set Goal", "Plan", "Open/Close Gripper" };
        private static readonly string[] PlanningPlan = { "Execute", "Preview" };

        private FakeRosBus bus;
        private GameObject root;
        private readonly List<GameObject> spawned = new();
        private RobotInstance robot;
        private PluginHost host;
        private TierUiRig rig;
        private SelectionService selection;
        private ISelectable endEffector, plan;

        private IEnumerator Build(params string[] pluginPrefabs)
        {
            var rootPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefab);
            Assume.That(rootPrefab != null, "The ERUPT root prefab is not in this project.");
            foreach (string path in pluginPrefabs)
                Assume.That(AssetDatabase.LoadAssetAtPath<GameObject>(path) != null, $"{path} is not in this project.");

            bus = new FakeRosBus();
            RosBus.Override(bus);
            UserSettings.Store = new MemoryUserSettingsStore();
            // A root without a camera, a rig or a ROS side says so in the log; that is not what is tested.
            LogAssert.ignoreFailingMessages = true;

            robot = FakeRobots.Add(RobotRegistry.Default, "robot", "", new FakeRobotModel("joint1", "joint2").WithGripper("finger"));
            endEffector = FakeRobots.EndEffectorOf(robot);
            var planGo = new GameObject("plan");
            planGo.transform.SetParent(robot.transform, false);
            plan = SelectableMarker.Ensure(planGo, SelectionKind.Trajectory, "Plan");

            // The plugin prefabs stand next to the root, as in a scene; the host finds them when it starts.
            foreach (string path in pluginPrefabs)
                spawned.Add(Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path)));
            root = Object.Instantiate(rootPrefab);
            yield return null;
            yield return null;

            host = root.GetComponent<PluginHost>();
            rig = root.GetComponent<TierUiRig>();
            selection = root.GetComponent<SelectionService>();
            Assert.IsNotNull(host);
            Assert.IsNotNull(rig);
            Assert.IsNotNull(selection);
            Assert.IsFalse(rig.ShowUnimplementedVerbs, "The root shows what can be pressed.");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            foreach (GameObject go in spawned) if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
            if (robot != null) Object.DestroyImmediate(robot.gameObject);
            RosBus.Reset();
            RobotRegistry.Default.SetBus(null);
            UserSettings.Store = null;
            LogAssert.ignoreFailingMessages = false;
        }

        // The buttons of the tier 2 menu for a selection, as texts, in the order they stand.
        private string[] MenuOf(ISelectable target)
        {
            selection.Select(target);
            string[] verbs = rig.TierTwo.Model.Verbs.Select(v => v.Label).ToArray();
            // What the model lists is what the view shows: one pressable button each.
            if (verbs.Length == 0) Assert.IsFalse(rig.TierTwo.Model.IsVisible, "A selection with nothing to offer has no menu.");
            foreach (Verb verb in rig.TierTwo.Model.Verbs)
                Assert.IsTrue(rig.TierTwo.Model.CanInvoke(verb), $"'{verb.Label}' is on the menu, so it can be pressed.");
            return verbs;
        }

        private string[] Strip() => rig.TierThree.Tabs.Select(t => t.Label).ToArray();

        private string[] Registered() => host.Plugins.Select(p => p.Id).ToArray();

        [UnityTest]
        public IEnumerator WithoutAnyModule_OnlyCoresOwnButtonsAreThere()
        {
            yield return Build();

            Assert.IsEmpty(Registered());
            CollectionAssert.AreEqual(CoreEndEffector, MenuOf(endEffector), "The gripper is the robot's own; nothing of a planner.");
            Assert.IsEmpty(MenuOf(plan), "No Execute or Preview without the module that offers them.");
            CollectionAssert.AreEqual(new[] { "Scene", "Settings" }, Strip(), "The Scene tab and the ROS address on Settings are core's; no module's tab.");
        }

        [UnityTest]
        public IEnumerator MoveItAlone_BringsThePlanningVerbsAndItsTab_AndNothingOfTheOthers()
        {
            yield return Build(MoveIt);

            CollectionAssert.AreEqual(new[] { "moveit" }, Registered());
            CollectionAssert.AreEqual(PlanningEndEffector, MenuOf(endEffector));
            CollectionAssert.AreEqual(PlanningPlan, MenuOf(plan));
            CollectionAssert.AreEqual(new[] { "Scene", "MoveIt", "Settings" }, Strip(), "No MTC tab and no Demonstrations tab.");
        }

        [UnityTest]
        public IEnumerator MoveItAndMtc_AddTheMtcTab()
        {
            yield return Build(MoveIt, Mtc);

            CollectionAssert.AreEquivalent(new[] { "moveit", "mtc" }, Registered());
            CollectionAssert.AreEqual(PlanningEndEffector, MenuOf(endEffector));
            CollectionAssert.AreEqual(PlanningPlan, MenuOf(plan));
            CollectionAssert.AreEqual(new[] { "Scene", "MoveIt", "MTC", "Settings" }, Strip());
        }

        [UnityTest]
        public IEnumerator DemonstrationAlone_BringsItsTab_AndNoVerb()
        {
            yield return Build(Demonstration);

            CollectionAssert.AreEqual(new[] { "demonstration" }, Registered());
            CollectionAssert.AreEqual(CoreEndEffector, MenuOf(endEffector), "No Set Start, Set Goal or Plan without a planner.");
            Assert.IsEmpty(MenuOf(plan), "The demonstration module puts nothing on a plan's menu, and there is no planner.");
            CollectionAssert.AreEqual(new[] { "Scene", "Demonstrations", "Settings" }, Strip());
        }

        // MTC needs the MoveIt plugin. Without it the host registers nothing and says why:
        // no half-working MTC tab, and no button of it.
        [UnityTest]
        public IEnumerator MtcWithoutMoveIt_IsNotRegistered_AndLeavesNoButton()
        {
            yield return Build(Mtc);

            Assert.IsEmpty(Registered());
            CollectionAssert.AreEqual(CoreEndEffector, MenuOf(endEffector));
            Assert.IsEmpty(MenuOf(plan));
            CollectionAssert.AreEqual(new[] { "Scene", "Settings" }, Strip());
        }

        [UnityTest]
        public IEnumerator AllThree_EveryButtonIsThere_AndAModuleTakenAwayTakesItsButtonsWithIt()
        {
            yield return Build(MoveIt, Mtc, Demonstration);

            CollectionAssert.AreEquivalent(new[] { "moveit", "mtc", "demonstration" }, Registered());
            CollectionAssert.AreEqual(PlanningEndEffector, MenuOf(endEffector));
            CollectionAssert.AreEqual(PlanningPlan, MenuOf(plan), "The demonstration module adds nothing to a plan's menu.");
            CollectionAssert.AreEqual(new[] { "Scene", "MoveIt", "MTC", "Demonstrations", "Settings" }, Strip());

            // The modules' own tabs hold no dead control for a module that is there.
            Assert.IsTrue(host.TryGet(out DemonstrationCollectionPlugin demonstrations));
            Assert.IsTrue(host.TryGet(out MtcPlugin mtc));
            Assert.IsTrue(host.TryGet(out MoveItPlugin moveIt));
            rig.SummonTab(demonstrations.DemosTabId);
            Button record = rig.TierThree.ActiveTab.Content.GetComponentsInChildren<Button>(true).First(b => b.name == "Record");
            Assert.IsTrue(record.interactable, "Record is available without switching anything first.");

            // Taken away while the app runs, a module takes its verbs and its tab with it.
            selection.Select(plan);
            host.Unregister(demonstrations);
            yield return null;
            CollectionAssert.AreEqual(PlanningPlan, rig.TierTwo.Model.Verbs.Select(v => v.Label).ToArray(), "The planners' verbs stay.");
            CollectionAssert.AreEqual(new[] { "Scene", "MoveIt", "MTC", "Settings" }, Strip());

            host.Unregister(mtc);
            host.Unregister(moveIt);
            yield return null;
            Assert.IsEmpty(Registered());
            CollectionAssert.AreEqual(CoreEndEffector, MenuOf(endEffector));
            Assert.IsEmpty(MenuOf(plan));
            CollectionAssert.AreEqual(new[] { "Scene", "Settings" }, Strip());
        }
    }
}
#endif
