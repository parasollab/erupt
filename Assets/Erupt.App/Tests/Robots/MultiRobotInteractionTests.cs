#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;

namespace Erupt.App.Robots.Tests
{
    /// <summary>
    /// Three FR3 robots (left, right, third) under one interaction router: an intent reaches
    /// only the robot its target belongs to, drags are per input source, the active robot
    /// follows the selection, and highlights create no materials. Skipped in a project that
    /// does not have the Franka robot package.
    /// </summary>
    public class MultiRobotInteractionTests
    {
        private const string PrefabPath = "Packages/com.erupt.robot.franka/Robots/FR3.prefab";
        private static readonly string[] ArmJoints =
            { "fr3_joint1", "fr3_joint2", "fr3_joint3", "fr3_joint4", "fr3_joint5", "fr3_joint6", "fr3_joint7" };

        private FakeRosBus bus;
        private RobotRegistry robots;
        private RobotInstance left, right, third;
        private GameObject routerGo, selectionGo, obstacle;
        private InteractionRouter router;
        private SelectionService selection;
        private RobotIntentDispatcher dispatcher;
        private readonly List<GameObject> spawned = new();
        private readonly List<InteractionRefusal> refusals = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assume.That(prefab != null, "The Franka robot package (com.erupt.robot.franka) is not in this project.");

            bus = new FakeRosBus();
            robots = FakeRobots.Registry(bus);

            // The router and the selection service exist before the robots, which look for them.
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            refusals.Clear();
            router.Refused += refusals.Add;
            selectionGo = new GameObject("selection");
            selection = selectionGo.AddComponent<SelectionService>();

            left = Spawn(prefab, "left", new Vector3(-1.5f, 0f, 0f));
            right = Spawn(prefab, "right", new Vector3(0f, 0f, 0f));
            third = Spawn(prefab, "third", new Vector3(1.5f, 0f, 0f));

            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = "obstacle";
            obstacle.transform.position = new Vector3(0f, 0.5f, 6f);
            obstacle.transform.localScale = Vector3.one * 0.2f;
            SelectableMarker.Ensure(obstacle, SelectionKind.Obstacle, "Obstacle");

            yield return new WaitForFixedUpdate();
            yield return null;
            yield return null;   // the joint handles exist after a LateUpdate
            Physics.SyncTransforms();
            dispatcher = RobotIntentDispatcher.For(router);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
            if (obstacle != null) Object.DestroyImmediate(obstacle);
            if (routerGo != null) Object.DestroyImmediate(routerGo);
            if (selectionGo != null) Object.DestroyImmediate(selectionGo);
        }

        private RobotInstance Spawn(GameObject prefab, string id, Vector3 position)
        {
            var holder = new GameObject("holder");
            holder.SetActive(false);
            GameObject go = Object.Instantiate(prefab, position, Quaternion.identity, holder.transform);
            go.name = id;
            var robot = go.GetComponent<RobotInstance>();
            robot.Configure(id, id, robot.Profile);
            robot.UseRegistry(robots);
            go.transform.SetParent(null, true);
            Object.DestroyImmediate(holder);
            spawned.Add(go);
            return robot;
        }

        // ---------- helpers: intents with real ray hits ----------

        // The handle of a robot's n-th arm joint (handles are named after the body the joint moves).
        private static Transform JointHandle(RobotInstance robot, int number) =>
            Object.FindObjectsByType<RobotOwned>(FindObjectsSortMode.None)
                .First(m => m.Owner == robot && Regex.IsMatch(m.name, $@"^Joint IK Handle \(fr3_(joint|link){number}\)$")).transform;

        // A ray onto a collider of this link's own body (not of the links nested under it).
        private static void AimAtLink(Transform link, out Ray ray, out RaycastHit hit)
        {
            Physics.SyncTransforms();
            Collider own = link.GetComponentsInChildren<Collider>()
                .First(c => c.GetComponentInParent<ArticulationBody>().transform == link);
            Vector3 origin = own.bounds.center + new Vector3(0.3f, 0.2f, -1f);
            ray = new Ray(origin, (own.bounds.center - origin).normalized);
            hit = default;
            foreach (RaycastHit candidate in Physics.RaycastAll(ray, 10f, ~0, QueryTriggerInteraction.Collide))
            {
                if (candidate.collider != own) continue;
                hit = candidate;
                return;
            }
            Assert.Fail($"The ray does not hit the collider of '{link.name}'.");
        }

        // A ray from the side that passes through the target, and the hit on the target itself.
        private static void Aim(Transform target, out Ray ray, out RaycastHit hit)
        {
            Physics.SyncTransforms();
            Vector3 origin = target.position + new Vector3(0f, 0.2f, -1f);
            ray = new Ray(origin, (target.position - origin).normalized);
            hit = default;
            foreach (RaycastHit candidate in Physics.RaycastAll(ray, 10f, ~0, QueryTriggerInteraction.Collide))
            {
                if (candidate.transform != target && !candidate.transform.IsChildOf(target)) continue;
                hit = candidate;
                return;
            }
            Assert.Fail($"No collider of '{target.name}' on the ray; the test cannot aim at it.");
        }

        // Where a ray meets one particular collider (whatever else it passes through).
        private static RaycastHit HitOn(Transform target, Ray ray)
        {
            foreach (RaycastHit candidate in Physics.RaycastAll(ray, 10f, ~0, QueryTriggerInteraction.Collide))
                if (candidate.transform == target) return candidate;
            Assert.Fail($"The ray does not meet '{target.name}'.");
            return default;
        }

        private static InteractionIntent Intent(IntentKind kind, string sourceId, Ray ray, RaycastHit hit, bool hasHit = true, Vector2 axis = default)
        {
            var sample = new InteractionSample(Modality.Controller, 1f, Time.realtimeSinceStartupAsDouble, new Pose(ray.origin, Quaternion.identity), sourceId);
            return new InteractionIntent(kind, sample, ray, hasHit, hit, axis);
        }

        private void Select(Transform target, string sourceId = "right-hand")
        {
            Aim(target, out Ray ray, out RaycastHit hit);
            dispatcher.OnSelect(Intent(IntentKind.Select, sourceId, ray, hit));
        }

        private static float[] Joints(RobotInstance robot)
        {
            var values = new float[ArmJoints.Length];
            for (int i = 0; i < ArmJoints.Length; i++)
                Assert.IsTrue(robot.Model.TryGetJointAngle(ArmJoints[i], out values[i]), $"{robot.Id} has no joint {ArmJoints[i]}");
            return values;
        }

        private static bool Moved(float[] before, float[] after) =>
            before.Zip(after, (a, b) => Mathf.Abs(a - b)).Max() > 1e-4f;

        // ---------- tests ----------

        [UnityTest]
        public IEnumerator EveryRobotIsServedByOneDispatcher()
        {
            Assert.AreEqual(3, dispatcher.Count);
            Assert.AreSame(dispatcher, routerGo.GetComponent<RobotIntentDispatcher>());
            Assert.AreEqual(1, Object.FindObjectsByType<RobotIntentDispatcher>(FindObjectsSortMode.None).Length);
            foreach (RobotInstance robot in new[] { left, right, third })
            {
                Assert.AreSame(robot.Interaction, dispatcher.ControllerFor(robot.Interaction.Handle.gameObject));
                Assert.AreSame(robot.Interaction, dispatcher.ControllerFor(JointHandle(robot, 4).gameObject));
            }
            Assert.IsNull(dispatcher.ControllerFor(obstacle));
            Assert.AreEqual(1, router.ProximitySources.Count, "The robots offer their tips to proximity feedback without setup.");
            yield break;
        }

        // On a robot without a gripper the handle at the tool flange is partly inside the last
        // link's body. Pointing at what shows of it picks the handle, not the link it is sunk
        // into; a link that merely passes in front of the handle, further away, is still a link.
        [UnityTest]
        public IEnumerator AHandleSunkIntoALink_IsStillPickedAndDragged()
        {
            Transform handle = right.Interaction.Handle;
            Transform tool = right.Model.EndEffector;
            Vector3 origin = handle.position + new Vector3(0f, 0.2f, -1f);
            Vector3 direction = (handle.position - origin).normalized;

            // A body of the robot's last link that covers the front of the handle.
            var flange = GameObject.CreatePrimitive(PrimitiveType.Cube);
            flange.name = "flange";
            flange.transform.SetParent(tool, true);
            flange.transform.localScale = Vector3.one;
            flange.transform.SetPositionAndRotation(handle.position - direction * 0.04f, Quaternion.LookRotation(direction));
            flange.transform.localScale = new Vector3(0.03f, 0.03f, 0.04f) / tool.lossyScale.x;
            yield return null;
            Physics.SyncTransforms();
            try
            {
                var ray = new Ray(origin, direction);
                RaycastHit hit = HitOn(flange.transform, ray);
                Assert.Less(hit.distance, Vector3.Distance(origin, handle.position) - 0.04f, "The link's body is met before the handle.");
                Assert.IsTrue(right.Interaction.PointsAtHandle(ray, hit));

                dispatcher.OnSelect(Intent(IntentKind.Select, "right-hand", ray, hit));
                Assert.AreEqual(SelectionKind.EndEffector, selection.Current.Kind, "Pointing through the link at the handle selects the end effector.");
                Assert.AreSame(right, robots.Active);

                // And a drag begun there drags the end effector.
                selection.ClearSelection();
                dispatcher.OnBeginDrag(Intent(IntentKind.BeginDrag, "right-hand", ray, hit));
                Assert.IsTrue(dispatcher.IsDragging("right-hand"));
                Assert.AreEqual(SelectionKind.EndEffector, selection.Current.Kind, "Dragging the end effector selects it: its menu is there when the drag ends.");
                dispatcher.OnEndDrag(Intent(IntentKind.EndDrag, "right-hand", ray, hit));
                Assert.AreEqual(SelectionKind.EndEffector, selection.Current.Kind, "And it stays selected.");

                // The same body well in front of the handle is something else to point at.
                flange.transform.position = handle.position - direction * 0.3f;
                Physics.SyncTransforms();
                hit = HitOn(flange.transform, ray);
                Assert.IsFalse(right.Interaction.PointsAtHandle(ray, hit));

                // A ray that misses the handle's sphere does not pick it either.
                var beside = new Ray(origin + Vector3.right * 0.1f, direction);
                flange.transform.position = handle.position + Vector3.right * 0.1f - direction * 0.04f;
                Physics.SyncTransforms();
                hit = HitOn(flange.transform, beside);
                Assert.IsFalse(right.Interaction.PointsAtHandle(beside, hit));
            }
            finally
            {
                Object.DestroyImmediate(flange);
            }
        }

        [UnityTest]
        public IEnumerator Selecting_ReachesOnlyTheOwner_AndMakesItTheActiveRobot()
        {
            Assert.AreSame(left, robots.Active, "The first robot is active until something is selected.");

            Select(JointHandle(right, 3));

            Assert.IsNotNull(right.Interaction.SelectedJoint);
            Assert.IsTrue(right.Interaction.SelectedJoint.transform.IsChildOf(right.transform));
            Assert.IsNull(left.Interaction.SelectedJoint);
            Assert.IsNull(third.Interaction.SelectedJoint);
            Assert.AreSame(right, robots.OwnerOf(selection.Current.GameObject));
            Assert.AreSame(right, robots.Active);
            StringAssert.EndsWith("· right", selection.Current.DisplayName, "With several robots the menu header names the robot.");

            // Another robot's end effector: the first robot lets go of its highlight.
            Select(third.Interaction.Handle);

            Assert.IsNull(right.Interaction.SelectedJoint, "A selection on another robot is not this robot's.");
            Assert.AreEqual(SelectionKind.EndEffector, selection.Current.Kind);
            Assert.AreEqual("End Effector · third", selection.Current.DisplayName);
            Assert.AreSame(third, robots.Active);

            // Something that is no robot's: the robots' selection goes, the active robot stays.
            Aim(obstacle.transform, out Ray ray, out RaycastHit hit);
            dispatcher.OnSelect(Intent(IntentKind.Select, "right-hand", ray, hit));
            Assert.IsNull(selection.Current);
            selection.Select(obstacle.GetComponent<SelectableMarker>());
            Assert.AreSame(third, robots.Active, "The active robot is sticky when something else is selected.");
            selection.ClearSelection();
            Assert.AreSame(third, robots.Active);

            // Only the active robot is shown as active.
            Assert.IsTrue(third.Interaction.ShowsActive);
            Assert.IsFalse(left.Interaction.ShowsActive || right.Interaction.ShowsActive);
            yield break;
        }

        [UnityTest]
        public IEnumerator Jogging_TurnsTheSelectedJointOfItsOwner_AndNoOtherRobot()
        {
            Select(JointHandle(left, 1));
            Select(JointHandle(third, 1));   // a stale joint on left must not be jogged
            yield return null;
            float[] leftBefore = Joints(left), rightBefore = Joints(right), thirdBefore = Joints(third);

            for (int i = 0; i < 5; i++)
            {
                dispatcher.OnAxis(Intent(IntentKind.Axis, "right-hand", default, default, false, new Vector2(0f, 1f)));
                yield return null;
            }

            Assert.IsTrue(Moved(thirdBefore, Joints(third)), "The owner of the current selection is jogged.");
            Assert.IsFalse(Moved(leftBefore, Joints(left)), "A robot whose joint was selected earlier is not.");
            Assert.IsFalse(Moved(rightBefore, Joints(right)));

            // With an obstacle selected nothing is jogged.
            selection.Select(obstacle.GetComponent<SelectableMarker>());
            float[] thirdNow = Joints(third);
            dispatcher.OnAxis(Intent(IntentKind.Axis, "right-hand", default, default, false, new Vector2(0f, 1f)));
            yield return null;
            Assert.IsFalse(Moved(thirdNow, Joints(third)));
        }

        [UnityTest]
        public IEnumerator TwoSources_DragTwoRobotsAtOnce_AndTheThirdStaysWhereItIs()
        {
            float[] leftBefore = Joints(left), rightBefore = Joints(right), thirdBefore = Joints(third);

            Aim(left.Interaction.Handle, out Ray leftRay, out RaycastHit leftHit);
            Aim(right.Interaction.Handle, out Ray rightRay, out RaycastHit rightHit);
            dispatcher.OnBeginDrag(Intent(IntentKind.BeginDrag, "left-hand", leftRay, leftHit));
            dispatcher.OnBeginDrag(Intent(IntentKind.BeginDrag, "right-hand", rightRay, rightHit));

            Assert.AreSame(left.Interaction, dispatcher.DraggedBy("left-hand"));
            Assert.AreSame(right.Interaction, dispatcher.DraggedBy("right-hand"));
            Assert.IsTrue(left.Interaction.IsDragging && right.Interaction.IsDragging);
            Assert.IsFalse(third.Interaction.IsDragging);

            // Each hand pulls its own handle a few centimetres, in different directions.
            for (int i = 1; i <= 10; i++)
            {
                var pulledLeft = new Ray(leftRay.origin + new Vector3(0f, -0.01f * i, 0f), leftRay.direction);
                var pulledRight = new Ray(rightRay.origin + new Vector3(0.01f * i, 0f, 0f), rightRay.direction);
                dispatcher.OnDrag(Intent(IntentKind.Drag, "left-hand", pulledLeft, default, false));
                dispatcher.OnDrag(Intent(IntentKind.Drag, "right-hand", pulledRight, default, false));
                yield return null;
            }

            Assert.IsTrue(Moved(leftBefore, Joints(left)));
            Assert.IsTrue(Moved(rightBefore, Joints(right)));
            Assert.IsFalse(Moved(thirdBefore, Joints(third)), "A robot nobody drags does not move.");

            dispatcher.OnEndDrag(Intent(IntentKind.EndDrag, "left-hand", leftRay, default, false));
            Assert.IsFalse(left.Interaction.IsDragging);
            Assert.IsTrue(right.Interaction.IsDragging, "Ending one hand's drag leaves the other's.");
            dispatcher.OnEndDrag(Intent(IntentKind.EndDrag, "right-hand", rightRay, default, false));
            Assert.IsFalse(dispatcher.IsDragging("right-hand"));
        }

        [UnityTest]
        public IEnumerator ARayThatMissesEveryRobot_ButPassesAHandle_DragsTheNearestHandle()
        {
            // Through third's handle onto the obstacle behind it: the hit is the obstacle.
            Transform handle = third.Interaction.Handle;
            Vector3 origin = handle.position + new Vector3(0f, 0f, -1f);
            obstacle.transform.position = handle.position + new Vector3(0f, 0f, 2f);
            Physics.SyncTransforms();
            var ray = new Ray(origin, Vector3.forward);
            RaycastHit obstacleHit = Physics.RaycastAll(ray, 10f, ~0, QueryTriggerInteraction.Collide)
                .First(h => h.transform == obstacle.transform);

            dispatcher.OnBeginDrag(Intent(IntentKind.BeginDrag, "right-hand", ray, obstacleHit));

            Assert.AreSame(third.Interaction, dispatcher.DraggedBy("right-hand"), "The handle the ray passes closest to.");
            Assert.IsFalse(left.Interaction.IsDragging || right.Interaction.IsDragging);
            dispatcher.OnEndDrag(Intent(IntentKind.EndDrag, "right-hand", ray, default, false));

            // Far from every handle: nothing is dragged.
            var away = new Ray(new Vector3(0f, 5f, -1f), Vector3.forward);
            obstacle.transform.position = new Vector3(0f, 5f, 2f);
            Physics.SyncTransforms();
            RaycastHit farHit = Physics.RaycastAll(away, 10f, ~0, QueryTriggerInteraction.Collide).First(h => h.transform == obstacle.transform);
            dispatcher.OnBeginDrag(Intent(IntentKind.BeginDrag, "right-hand", away, farHit));
            Assert.IsFalse(dispatcher.IsDragging("right-hand"));
            yield break;
        }

        [UnityTest]
        public IEnumerator Highlights_AreLayeredPropertyBlocks_NotMaterialInstances()
        {
            // Shared materials before anything is selected.
            Renderer[] renderers = right.GetComponentsInChildren<Renderer>();
            Material[] shared = renderers.Select(r => r.sharedMaterial).ToArray();
            Transform link3 = right.transform.GetComponentsInChildren<ArticulationBody>().First(b => b.name == "fr3_link3").transform;
            Transform link4 = right.transform.GetComponentsInChildren<ArticulationBody>().First(b => b.name == "fr3_link4").transform;
            Renderer onLink3 = link3.GetComponentsInChildren<Renderer>().First(r => r.GetComponentInParent<ArticulationBody>().transform == link3);
            Renderer onLink4 = link4.GetComponentsInChildren<Renderer>().First(r => r.GetComponentInParent<ArticulationBody>().transform == link4);
            Assert.IsFalse(onLink3.HasPropertyBlock());

            // Selecting a link of the right robot makes it active (tint on every link) and
            // highlights that link alone, not the links further down the arm.
            AimAtLink(link3, out Ray ray, out RaycastHit hit);
            dispatcher.OnSelect(Intent(IntentKind.Select, "right-hand", ray, hit));
            ArticulationBody selected = right.Interaction.SelectedJoint;
            Assert.IsNotNull(selected, "A ray on a link selects the joint that moves it.");
            Transform selectedLink = selected.transform;
            Renderer onSelected = selectedLink.GetComponentsInChildren<Renderer>()
                .First(r => r.GetComponentInParent<ArticulationBody>().transform == selectedLink);
            Renderer below = selectedLink.GetComponentsInChildren<Renderer>()
                .First(r => r.GetComponentInParent<ArticulationBody>().transform != selectedLink);

            Assert.IsTrue(RendererTint.Has(onSelected, RendererTint.Layer.Selected));
            Assert.IsFalse(RendererTint.Has(below, RendererTint.Layer.Selected), "The links below the selected one keep their colour.");
            Assert.IsTrue(RendererTint.Has(below, RendererTint.Layer.ActiveRobot));
            Assert.IsTrue(onLink4.HasPropertyBlock());

            // Another robot becomes active: every override on the right robot goes.
            Select(left.Interaction.Handle);
            Assert.IsFalse(RendererTint.Has(onSelected, RendererTint.Layer.Selected));
            Assert.IsFalse(onLink3.HasPropertyBlock() || onLink4.HasPropertyBlock());

            CollectionAssert.AreEqual(shared, renderers.Select(r => r.sharedMaterial).ToArray(),
                "No renderer was given a material instance.");
            yield break;
        }

        [UnityTest]
        public IEnumerator WithOneRobot_NothingNamesOrTintsTheActiveRobot()
        {
            Object.DestroyImmediate(left.gameObject);
            Object.DestroyImmediate(third.gameObject);
            yield return null;

            Select(right.Interaction.Handle);

            Assert.AreEqual("End Effector", selection.Current.DisplayName);
            Assert.IsFalse(right.Interaction.ShowsActive);
            Assert.AreSame(right, robots.Active);
        }
    }
}
#endif
