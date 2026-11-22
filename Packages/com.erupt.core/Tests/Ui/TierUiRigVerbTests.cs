using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Ui.Tests
{
    /// <summary>
    /// The rig's tier 2 menus hold what can be pressed. The verbs the guidelines name and
    /// nothing implements yet are left out by default, behind one switch, and stay in the
    /// registry. Tier 3 opens where a backend says, when one says.
    /// </summary>
    public class TierUiRigVerbTests
    {
        private GameObject go, target;
        private SelectionService selection;
        private TierUiRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            go = new GameObject("ERUPT");
            selection = go.AddComponent<SelectionService>();
            rig = go.AddComponent<TierUiRig>();
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        private ISelectable Make(SelectionKind kind)
        {
            Object.DestroyImmediate(target);
            target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var marker = target.AddComponent<SelectableMarker>();
            marker.SetKind(kind);
            return marker;
        }

        private string[] Shown() => rig.TierTwo.Model.Verbs.Select(v => v.Id).ToArray();

        [UnityTest]
        public IEnumerator ByDefault_AMenuHoldsOnlyTheVerbsThatAreImplemented()
        {
            Assert.IsFalse(rig.ShowUnimplementedVerbs);
            rig.BindVerb("delete", _ => { });
            rig.BindVerb("resize", _ => { });

            selection.Select(Make(SelectionKind.Obstacle));
            yield return null;
            CollectionAssert.AreEqual(new[] { "resize", "delete" }, Shown(), "No Reshape, Paint Cost or Lock; no unbound Snap or Duplicate.");
            Assert.AreEqual(2, rig.TierTwo.InteractiveElementCount);

            // A robot link has nothing to offer yet: no menu, rather than one of dead buttons.
            selection.Select(Make(SelectionKind.RobotLink));
            yield return null;
            Assert.IsEmpty(Shown());
            Assert.IsFalse(rig.TierTwo.Canvas.gameObject.activeSelf);

            // The vocabulary is all still there.
            CollectionAssert.Contains(rig.Verbs.For(SelectionKind.Obstacle).Select(v => v.Id).ToArray(), "paint-cost");
            CollectionAssert.Contains(rig.Verbs.For(SelectionKind.RobotLink).Select(v => v.Id).ToArray(), "set-joint-angle");
        }

        [UnityTest]
        public IEnumerator TheSwitch_ShowsTheUnimplementedVerbsAgain_Disabled()
        {
            rig.BindVerb("delete", _ => { });
            selection.Select(Make(SelectionKind.Obstacle));
            yield return null;
            Assert.AreEqual(1, Shown().Length);

            rig.ShowUnimplementedVerbs = true;
            yield return null;
            Assert.AreEqual(7, Shown().Length);
            Assert.AreEqual(1, rig.TierTwo.InteractiveElementCount, "They are listed, greyed out.");
            Assert.IsFalse(rig.TierTwo.ButtonFor("paint-cost").interactable);

            rig.ShowUnimplementedVerbs = false;
            yield return null;
            CollectionAssert.AreEqual(new[] { "delete" }, Shown());
        }

        [UnityTest]
        public IEnumerator TierThree_OpensWhereABackendSays_AndInFrontOfTheHeadOtherwise()
        {
            var cameraGo = new GameObject("camera") { tag = "MainCamera" };
            cameraGo.AddComponent<Camera>();
            cameraGo.transform.SetPositionAndRotation(new Vector3(100f, 1.6f, 0f), Quaternion.identity);
            try
            {
                rig.AddTab("a", "A", null);
                var elsewhere = new Pose(new Vector3(100f, 5f, 2f), Quaternion.Euler(30f, 0f, 0f));
                rig.UseSummonPlacement(() => elsewhere);
                rig.ToggleTierThree();
                yield return null;
                Assert.IsTrue(rig.TierThree.IsOpen);
                Assert.AreEqual(elsewhere.position, rig.TierThree.transform.position);
                Assert.Less(Quaternion.Angle(elsewhere.rotation, rig.TierThree.transform.rotation), 0.01f);

                // A placement that has nothing to say, and no placement at all: the default.
                foreach (System.Func<Pose?> placement in new System.Func<Pose?>[] { () => null, null })
                {
                    rig.ToggleTierThree();
                    rig.UseSummonPlacement(placement);
                    rig.ToggleTierThree();
                    yield return null;
                    Assert.AreEqual(TierUiRig.PlacementPositionFor(cameraGo.transform.position, Vector3.forward, 1.1f, -0.1f),
                        rig.TierThree.transform.position);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
            }
        }
    }
}
