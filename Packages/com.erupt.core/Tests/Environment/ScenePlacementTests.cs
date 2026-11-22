using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Ui;
using Erupt.UiBindings;

namespace Erupt.Environment.Tests
{
    /// <summary>
    /// Where the Scene tab puts a new shape (on the surface the user looks at, not inside it and
    /// not inside a robot), and the snap verb saying so when there is nothing to snap to.
    /// </summary>
    public class ScenePlacementTests
    {
        private readonly List<GameObject> made = new();
        private static readonly Vector3 Origin = new Vector3(120f, 1.5f, 0f);

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in made) if (go != null) Object.DestroyImmediate(go);
            made.Clear();
        }

        private GameObject Box(string name, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            made.Add(go);
            return go;
        }

        [UnityTest]
        public IEnumerator ANewShape_RestsOnTheSurfaceTheUserLooksAt()
        {
            // A table top 1.2 m ahead and below the eyes.
            Box("table", Origin + new Vector3(0f, -0.8f, 1.2f), new Vector3(2f, 0.1f, 2f));
            yield return null;
            Physics.SyncTransforms();
            Vector3 gaze = new Vector3(0f, -0.75f, 1.2f).normalized;

            Vector3 at = ScenePlacementTab.PlacementAlong(Origin, gaze, 2f, 0.3f);

            float tableTop = Origin.y - 0.75f;
            Assert.AreEqual(tableTop + 0.15f, at.y, 0.005f, "Half the shape's size above the table top: resting on it.");
            Assert.Less(Vector3.Distance(Origin, at), 2f);
        }

        [UnityTest]
        public IEnumerator WithNothingInReach_ItGoesTheReachAhead_AndARobotIsNotASurface()
        {
            Vector3 ahead = Vector3.forward;
            Assert.AreEqual(Origin + ahead * 2f, ScenePlacementTab.PlacementAlong(Origin, ahead, 2f, 0.3f));

            // A robot link in the line of sight, a wall behind it.
            GameObject link = Box("link", Origin + ahead * 1f, Vector3.one * 0.2f);
            link.AddComponent<ArticulationBody>();
            Box("wall", Origin + ahead * 1.8f, new Vector3(3f, 3f, 0.1f));
            yield return null;
            Physics.SyncTransforms();

            Vector3 at = ScenePlacementTab.PlacementAlong(Origin, ahead, 2f, 0.3f);
            Assert.AreEqual(Origin.z + 1.75f - 0.15f, at.z, 0.005f, "Against the wall, past the robot.");

            // A surface beyond the reach does not pull the shape to it.
            Assert.AreEqual(Origin + ahead * 0.5f, ScenePlacementTab.PlacementAlong(Origin, ahead, 0.5f, 0.3f));
        }

        [UnityTest]
        public IEnumerator SnapToSurface_WithNoSurfaceBelow_IsARefusal()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            var routerGo = new GameObject("router");
            made.Add(routerGo);
            var router = routerGo.AddComponent<InteractionRouter>();
            var refusals = new List<InteractionRefusal>();
            router.Refused += refusals.Add;

            var erupt = new GameObject("ERUPT");
            made.Add(erupt);
            var selection = erupt.AddComponent<SelectionService>();
            var rig = erupt.AddComponent<TierUiRig>();
            erupt.AddComponent<ObstacleVerbBindings>();
            yield return null;   // the bindings bind on Start

            GameObject floating = Box("floating", Origin + Vector3.up * 30f, Vector3.one * 0.2f);
            var marker = floating.AddComponent<SelectableMarker>();
            marker.SetKind(SelectionKind.Obstacle);
            selection.Select(marker);
            yield return null;

            Vector3 before = floating.transform.position;
            Assert.IsFalse(rig.TierTwo.Model.Invoke("snap").IsRefused, "The verb itself ran.");
            Assert.AreEqual(before, floating.transform.position);
            Assert.AreEqual(1, refusals.Count, "Snap used to do nothing, silently.");
            StringAssert.Contains("no surface below", refusals[0].Reason);
            Assert.AreEqual(before, refusals[0].WorldPoint);
            Assert.IsFalse(rig.UndoStack.CanUndo, "Nothing happened, so there is nothing to undo.");
        }
    }
}
