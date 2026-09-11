using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Tests;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;
using RosMessageTypes.MoveitTaskConstructorMsgs;

namespace Erupt.Plugins.Mtc.Tests
{
    /// <summary>The tier 3 MTC tab: element budget with many solutions, ranking, Teach-mode gate on recording.</summary>
    public class SolutionsTabTests
    {
        private FakeRosBus bus;
        private GameObject pluginGo, canvasGo, modesGo;
        private MtcPlugin plugin;
        private MtcClient client;
        private ModeManager modes;
        private SolutionsTab tab;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            bus = new FakeRosBus();
            RosBus.Override(bus);
            modesGo = new GameObject("modes");
            modes = modesGo.AddComponent<ModeManager>();

            pluginGo = new GameObject("mtc");
            client = pluginGo.AddComponent<MtcClient>();
            pluginGo.AddComponent<PickPlaceTaskRecorder>();
            plugin = pluginGo.AddComponent<MtcPlugin>();
            yield return null;                                   // MtcClient.Start subscribes
            ((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = new FakeUiHost(), Modes = modes, Undo = new UndoStack() });

            var canvas = UiBuilder.CreateWorldCanvas("Tier3", null, new Vector2(800f, 900f));
            canvasGo = canvas.gameObject;
            tab = new SolutionsTab(plugin, UiBuilder.CreatePanel("Content", canvas.transform, Color.clear));
        }

        [TearDown]
        public void TearDown()
        {
            tab.Dispose();
            RosBus.Reset();
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(pluginGo);
            Object.DestroyImmediate(modesGo);
        }

        [UnityTest]
        public IEnumerator ManySolutions_ShowAtMostFiveRankedByCost_WithinSevenElements()
        {
            for (uint i = 1; i <= 8; i++) bus.Inbound("/solution", Solution(i, cost: 10f - i));
            yield return null;

            Assert.AreEqual(8, client.Solutions.Count);
            Assert.AreEqual(SolutionsTab.MaxSolutionButtons, tab.SolutionButtons.Count);
            Assert.LessOrEqual(tab.InteractiveElementCount, 7, "record + 5 solutions + cancel");
            var first = plugin.RankedSolutions.First().sol;
            Assert.AreEqual(8u, MtcClient.TopLevelId(first), "Lowest cost ranks first.");
        }

        [UnityTest]
        public IEnumerator FirstSolution_IsSelectedAndGetsAWorldHandle()
        {
            bus.Inbound("/solution", Solution(1, cost: 1f));
            yield return null;

            Assert.IsNotNull(plugin.SelectedSolution);
            var selectable = plugin.Results.Single();
            Assert.IsTrue(selectable.gameObject.activeSelf);
            Assert.IsNotNull(selectable.GetComponentInChildren<Collider>(), "A plan needs a collider to be ray-selectable.");
            Assert.AreEqual(SelectionKind.Trajectory, selectable.Kind);
        }

        [Test]
        public void Record_IsGatedOnTeachMode()
        {
            Assert.IsFalse(plugin.InTeachMode);
            var record = tab.Root.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b => b.name == "Record");
            Assert.IsFalse(record.interactable, "Recording is a Teach-mode activity (Part 5).");

            modes.SetMode(AppMode.Teach);
            ((IEruptPlugin)plugin).OnModeChanged(AppMode.Teach);
            Assert.IsTrue(plugin.InTeachMode);
            Assert.IsTrue(record.interactable);
        }

        private static SolutionMsg Solution(uint id, float cost) => new SolutionMsg
        {
            sub_solution = new[] { new SubSolutionMsg { info = new SolutionInfoMsg { id = id, cost = cost, stage_id = 1 } } },
            sub_trajectory = new[] { new SubTrajectoryMsg { info = new SolutionInfoMsg { id = id, cost = cost, stage_id = 1 } } }
        };
    }
}
