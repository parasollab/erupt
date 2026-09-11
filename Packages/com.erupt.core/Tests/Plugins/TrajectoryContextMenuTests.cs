using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;

namespace Erupt.Plugins.Tests
{
    /// <summary>
    /// End to end through the real rig: a planner registers, plans, its trajectory handle is
    /// selected, and tier 2 offers preview/execute that reach the plugin.
    /// </summary>
    public class TrajectoryContextMenuTests
    {
        private GameObject eruptGo, plannerGo;
        private SelectionService selection;
        private TierUiRig rig;
        private PluginHost host;
        private PlacingPlanner planner;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            eruptGo = new GameObject("ERUPT");
            eruptGo.SetActive(false);
            selection = eruptGo.AddComponent<SelectionService>();
            var modes = eruptGo.AddComponent<ModeManager>();
            rig = eruptGo.AddComponent<TierUiRig>();
            host = eruptGo.AddComponent<PluginHost>();
            host.DiscoverInScene = false;
            host.Initialise(new EruptContext { Selection = selection, Modes = modes, Ui = rig, Undo = host.Undo });
            eruptGo.SetActive(true);
            yield return null;

            plannerGo = new GameObject("planner");
            planner = plannerGo.AddComponent<PlacingPlanner>();
            host.Register(planner);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(plannerGo);
            Object.DestroyImmediate(eruptGo);
        }

        [UnityTest]
        public IEnumerator SelectingAPlansHandle_ShowsTrajectoryVerbs_ThatReachThePlugin()
        {
            planner.RequestPlan(new PlanPreferences(), _ => { });
            var handle = planner.Place(planner.LastResult, new Vector3(1f, 1f, 1f));
            yield return null;

            Assert.IsNotNull(handle.GetComponentInChildren<Collider>());
            ISelectable resolved = SelectionService.Resolve(handle.GetComponentInChildren<Collider>().gameObject);
            Assert.AreSame(handle, resolved, "A ray on the handle resolves to the trajectory.");

            selection.Select(resolved);
            var model = rig.TierTwo.Model;
            Assert.AreEqual(SelectionKind.Trajectory, model.Kind);
            CollectionAssert.IsSubsetOf(new[] { "preview", "execute" }, model.Verbs.Select(v => v.Id));
            Assert.IsTrue(model.CanInvoke(model.Verbs.First(v => v.Id == "preview")));

            Assert.IsFalse(model.Invoke("preview").IsRefused);
            Assert.IsFalse(model.Invoke("execute").IsRefused);
            Assert.AreEqual(1, planner.PreviewCalls);
            Assert.AreEqual(1, planner.ExecuteCalls);

            Assert.LessOrEqual(rig.TierTwo.InteractiveElementCount + rig.TierOne.Registry.TierOneControls.Count, 9,
                "Tier 1 (4) + trajectory verbs stay near the Part 8 budget.");
        }

        [Test]
        public void PlanVerb_OnTheEndEffector_IsAPluginVerb()
        {
            var plan = rig.Verbs.For(SelectionKind.EndEffector).FirstOrDefault(v => v.Id == "plan");
            Assert.AreEqual(VerbOrigin.Plugin, plan.Origin);
            Assert.AreEqual("placing", plan.PluginId);
        }

        /// <summary>FakePlanner with the protected handle placement exposed.</summary>
        private sealed class PlacingPlanner : PlanningPlugin
        {
            public int PreviewCalls, ExecuteCalls;
            public override string Id => "placing";
            public override string DisplayName => "Placing";
            public override InteractionRefusal SetGoal(ISelectable endEffector) => InteractionRefusal.None;
            public override void RequestPlan(PlanPreferences preferences, System.Action<PlanResult> done) =>
                done?.Invoke(PublishResult(new PlanResult()));
            public override void Preview(PlanResult plan) => PreviewCalls++;
            public override void StopPreview() { }
            public override void Execute(PlanResult plan, System.Action<ExecutionStatus> status) => ExecuteCalls++;
            public TrajectorySelectable Place(PlanResult result, Vector3 at) => PlaceHandle(result, at);
        }
    }
}
