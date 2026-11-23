#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Interaction.Backends;
using Erupt.Plugins;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.App.Desktop.Tests
{
    /// <summary>
    /// The reference scene used the way the quick start says: in the editor, without a headset,
    /// with the mouse and the keyboard. The input is a simulated mouse and keyboard (the Input
    /// System's test fixture), so every click goes through the scene's own event system, the
    /// mouse backend, the interaction router and the tier UI; the ROS side is the fake bus.
    /// Skipped in a project that does not have the scene.
    /// </summary>
    public class DesktopFlowTests
    {
        private const string ScenePath = "Assets/Scenes/KitchenFR3.unity";

        private readonly InputTestFixture input = new InputTestFixture();
        private Mouse mouse;
        private Keyboard keyboard;
        private FakeRosBus bus;
        private MemoryUserSettingsStore settings;
        private Scene scene;
        private GameObject rosConnection;

        private Camera cam;
        private DesktopControls controls;
        private TierUiRig ui;
        private PluginHost host;
        private MoveItPlugin moveIt;
        private SelectionService selection;
        private RobotInstance robot;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assume.That(File.Exists(ScenePath), "The reference scene is not in this project.");

            input.Setup();

            // The fixture's clock starts at zero and gains 1/60 s per frame. A headless run renders

            // thousands of frames a second, so that clock soon crosses the moment the editor entered

            // play mode, and the Input System drops the state events stamped around it. Start past it.

            input.currentTime = 1e6;
            mouse = InputSystem.AddDevice<Mouse>();
            keyboard = InputSystem.AddDevice<Keyboard>();

            bus = new FakeRosBus();
            RosBus.Override(bus);
            // The scene's visualization suite asks the connector itself for a connection: give it
            // one that never connects, or a real one keeps trying (and logging) into later tests.
            rosConnection = IdleRosConnection.Create();
            // The scene's collision haptics start from the scene's values and remember nothing
            // on this machine.
            settings = new MemoryUserSettingsStore();
            UserSettings.Store = settings;
            // The scene carries the XR rig, which reports missing devices when run headless.
            LogAssert.ignoreFailingMessages = true;

            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded);
            SceneManager.SetActiveScene(scene);
            yield return null;
            yield return null;
            yield return new WaitForFixedUpdate();
            yield return null;

            host = Find<PluginHost>();
            controls = Find<DesktopControls>();
            ui = Find<TierUiRig>();
            selection = Find<SelectionService>();
            Assert.IsNotNull(host, "the ERUPT root");
            Assert.IsNotNull(controls, "The ERUPT root carries the desktop controls: no scene wires them.");
            Assert.IsTrue(host.TryGet(out moveIt), "the MoveIt plugin");
            robot = host.Context.Robots.All.Single(r => r.gameObject.scene == scene);
            cam = Camera.main;
            Assert.IsNotNull(cam);

            // The view has settled on the robot.
            yield return Seconds(0.5f);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            scene = default;
            if (rosConnection != null) UnityEngine.Object.DestroyImmediate(rosConnection);
            RosBus.Reset();
            RobotRegistry.Default.SetBus(null);
            UserSettings.Store = null;
            input.TearDown();
            LogAssert.ignoreFailingMessages = false;
        }

        private T Find<T>() where T : Component =>
            UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(c => c.gameObject.scene == scene);

        // ---- the simulated mouse and keyboard -------------------------------------------------------

        private Vector2 OnScreen(Vector3 world, string what)
        {
            Vector3 view = cam.WorldToViewportPoint(world);
            Assert.Greater(view.z, cam.nearClipPlane, $"{what} is in front of the camera");
            Assert.IsTrue(view.x > 0.02f && view.x < 0.98f && view.y > 0.02f && view.y < 0.98f, $"{what} is in the view (viewport {view.x:F2}, {view.y:F2})");
            return cam.WorldToScreenPoint(world);
        }

        private IEnumerator Point(Vector2 screen)
        {
            input.Move(mouse.position, screen);
            yield return null;
        }

        private IEnumerator Click(Vector2 screen)
        {
            yield return Point(screen);
            input.Press(mouse.leftButton);
            yield return null;
            input.Release(mouse.leftButton);
            yield return null;
            yield return null;
        }

        private IEnumerator Tap(Key key)
        {
            input.Press(keyboard[key]);
            yield return null;
            input.Release(keyboard[key]);
            yield return null;
        }

        private IEnumerator Hold(Key key, float seconds)
        {
            input.Press(keyboard[key]);
            yield return null;
            yield return null;
            Assert.IsTrue(keyboard[key].isPressed, $"{key} is held");
            yield return Seconds(seconds);
            input.Release(keyboard[key]);
            yield return null;
        }

        // Frames, not WaitForSeconds: the view moves by unscaled time, and a headless run renders thousands of frames a second.
        private static IEnumerator Seconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private Vector2 OnScreen(Button button, string what)
        {
            Assert.IsNotNull(button, $"The button '{what}' is on the menu.");
            Assert.IsTrue(button.interactable, $"The button '{what}' can be pressed.");
            return OnScreen(button.transform.position, $"The button '{what}'");
        }

        private IEnumerator ClickVerb(string verbId)
        {
            Assert.IsTrue(ui.TierTwo.Canvas.gameObject.activeInHierarchy, "The tier 2 menu is showing.");
            yield return Click(OnScreen(ui.TierTwo.ButtonFor(verbId), verbId));
        }

        private Button PanelButton(string name) =>
            ui.TierThree.Canvas.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == name);

        private Vector3 HandlePosition => robot.Interaction.Handle.position;

        // ---- the fake ROS side ------------------------------------------------------------------------

        // MoveIt's answer to a plan request: straight from the start state to the goal constraints.
        private void AnswerPlans()
        {
            bus.SetServiceHandler("/plan_kinematic_path", message =>
            {
                MotionPlanRequestMsg request = ((GetMotionPlanRequest)message).motion_plan_request;
                JointConstraintMsg[] goal = request.goal_constraints[0].joint_constraints;
                JointStateMsg start = request.start_state.joint_state;
                string[] joints = goal.Select(c => c.joint_name).ToArray();
                double[] from = joints.Select(j => start.position[Array.IndexOf(start.name, j)]).ToArray();
                double[] to = goal.Select(c => c.position).ToArray();
                return new GetMotionPlanResponse(new MotionPlanResponseMsg
                {
                    error_code = new MoveItErrorCodesMsg { val = MoveItErrorCodesMsg.SUCCESS },
                    planning_time = 0.1,
                    group_name = request.group_name,
                    trajectory = new RobotTrajectoryMsg
                    {
                        joint_trajectory = new JointTrajectoryMsg
                        {
                            joint_names = joints,
                            points = new[]
                            {
                                new JointTrajectoryPointMsg { positions = from, time_from_start = new DurationMsg { sec = 0 } },
                                new JointTrajectoryPointMsg { positions = to, time_from_start = new DurationMsg { sec = 2 } }
                            }
                        }
                    }
                });
            });
        }

        // ---- the tests --------------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator AfterPlay_TheViewShowsTheRobot_AndTierOneIsInIt()
        {
            Assert.IsTrue(controls.IsActive, "Without a headset the desktop controls drive the view.");
            Assert.AreSame(cam, controls.View);
            foreach (TrackedPoseDriver driver in cam.GetComponents<TrackedPoseDriver>())
                Assert.IsFalse(driver.enabled, "Nothing pins the camera to the rig's origin while the desktop controls drive it.");

            OnScreen(HandlePosition, "The end-effector handle");
            Assert.IsTrue(robot.Model.TryFindLinkTransform(robot.Profile.baseLink, out Transform baseLink));
            OnScreen(baseLink.position, "The robot's base");
            float distance = Vector3.Distance(cam.transform.position, HandlePosition);
            Assert.That(distance, Is.InRange(0.8f, 2.6f), "The camera stands at a working distance from the end effector.");

            // Tier 1 (menu, undo, redo) is in the view, where the mouse reaches it.
            foreach (Button button in ui.TierOne.Canvas.GetComponentsInChildren<Button>())
                OnScreen(button.transform.position, $"Tier 1 '{button.name}'");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlanAndExecute_WithTheMouse()
        {
            AnswerPlans();

            // 1. Left click on the end effector selects it and its tier 2 menu appears.
            yield return Click(OnScreen(HandlePosition, "The end-effector handle"));
            Assert.AreEqual(SelectionKind.EndEffector, selection.CurrentKind, "A left click on the handle selects the end effector.");
            Assert.IsTrue(ui.TierTwo.Canvas.gameObject.activeInHierarchy, "Its tier 2 menu appears.");
            CollectionAssert.AreEqual(new[] { "set-goal", "gripper", "set-start", "plan" }, ui.TierTwo.Model.Verbs.Select(v => v.Id).ToArray(),
                "The menu holds what can be pressed, and nothing else.");

            // 2. Set Start.
            yield return ClickVerb("set-start");
            Assert.IsTrue(moveIt.HasStart, "A click on Set Start captured the start.");
            Assert.AreEqual(SelectionKind.EndEffector, selection.CurrentKind, "A click on a menu button does not change the selection.");

            // 3. Right-drag on the handle: the arm follows.
            Vector3 handleBefore = HandlePosition;
            float[] jointsBefore = robot.Model.GetJointStatePositions();
            Vector2 from = OnScreen(HandlePosition, "The end-effector handle");
            Vector2 to = from + new Vector2(-0.10f, -0.08f) * cam.pixelHeight;
            yield return Point(from);
            input.Press(mouse.rightButton);
            yield return null;
            for (int i = 1; i <= 12; i++)
            {
                input.Move(mouse.position, Vector2.Lerp(from, to, i / 12f));
                yield return Seconds(0.03f);
            }
            yield return Seconds(0.6f);   // the IK catches up
            input.Release(mouse.rightButton);
            yield return null;
            yield return null;
            Assert.Greater(Vector3.Distance(handleBefore, robot.Model.EndEffector.position), 0.05f, "The end effector followed the drag.");
            float[] jointsAfter = robot.Model.GetJointStatePositions();
            Assert.IsTrue(jointsBefore.Zip(jointsAfter, (a, b) => Mathf.Abs(a - b)).Any(d => d > 0.02f), "The arm's joints moved.");
            Vector2 where = cam.WorldToScreenPoint(robot.Model.EndEffector.position);
            Assert.Less(Vector2.Distance(where, to), 0.04f * cam.pixelHeight, "The end effector is under the pointer.");

            // 4. Set Goal, Plan.
            yield return ClickVerb("set-goal");
            Assert.IsTrue(moveIt.HasGoal, "A click on Set Goal captured the goal.");
            yield return ClickVerb("plan");
            Assert.IsNotNull(moveIt.LastResult, "A click on Plan produced a plan.");
            TrajectorySelectable plan = moveIt.Results.Single();
            Assert.AreSame(plan, selection.Current, "The new plan is the selection: its menu is there.");
            var handleRenderer = plan.GetComponentInChildren<MeshRenderer>();
            Assert.IsTrue(UnityEditor.AssetDatabase.Contains(handleRenderer.sharedMaterial),
                "The handle must use a material asset included in the player.");
            Assert.AreEqual("Universal Render Pipeline/Lit", handleRenderer.sharedMaterial.shader.name);
            var handleTint = new MaterialPropertyBlock();
            handleRenderer.GetPropertyBlock(handleTint);
            Assert.That(handleTint.GetColor(MaterialColor.BaseColorId), Is.EqualTo(new Color(0.55f, 0.35f, 1f, 0.9f))
                .Using(UnityEngine.TestTools.Utils.ColorEqualityComparer.Instance));
            // The plan plays on the robot's blue copy, over and over; the robot itself stays put,
            // and the plan's menu offers to stop the preview where it would offer to play it.
            CollectionAssert.AreEquivalent(new[] { "execute", Erupt.Plugins.PlanningPlugin.StopPreviewVerb },
                ui.TierTwo.Model.Verbs.Select(v => v.Id).ToArray());
            Assert.IsTrue(robot.Ghosts.IsPreviewPlaying, "The new plan plays on a copy of the robot.");
            float[] posed = robot.Model.GetJointStatePositions();
            for (int i = 0; i < 20; i++) yield return null;
            float[] during = robot.Model.GetJointStatePositions();
            for (int i = 0; i < posed.Length; i++)
                Assert.AreEqual(posed[i], during[i], 1e-4f, "The robot itself does not move during a preview.");

            yield return ClickVerb(Erupt.Plugins.PlanningPlugin.StopPreviewVerb);
            Assert.IsFalse(robot.Ghosts.IsPreviewPlaying, "A click on Stop Preview stops it and removes the copy.");
            CollectionAssert.AreEquivalent(new[] { "execute", "preview" }, ui.TierTwo.Model.Verbs.Select(v => v.Id).ToArray());
            yield return ClickVerb("preview");
            Assert.IsTrue(robot.Ghosts.IsPreviewPlaying, "A click on Preview plays it again.");

            // 5. The plan can also be selected by its handle: click somewhere empty to deselect,
            //    then on the handle (when the previewing arm is not in front of it).
            yield return Click(new Vector2(cam.pixelWidth * 0.03f, cam.pixelHeight * 0.5f));
            Assert.AreNotEqual(SelectionKind.Trajectory, selection.CurrentKind, "A click away from everything dropped the plan.");
            Vector2 handle = default;
            float until = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < until)
            {
                handle = OnScreen(plan.transform.position, "The plan's handle");
                Physics.SyncTransforms();
                if (Physics.Raycast(cam.ScreenPointToRay(handle), out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Collide)
                    && hit.collider.GetComponentInParent<TrajectorySelectable>() == plan) break;
                yield return null;
            }
            yield return Click(handle);
            Assert.AreSame(plan, selection.Current, "A left click on the plan's handle selects the plan.");
            string command = "/" + robot.Profile.trajectoryCommandTopic;
            Assert.AreEqual(0, bus.CountOn(command));
            yield return ClickVerb("execute");
            Assert.AreEqual(1, bus.CountOn(command), "A click on Execute sent the trajectory to the robot's controller.");

            // The ROS robot moves; the Unity robot follows it.
            var sent = (JointTrajectoryMsg)bus.PublishedOn(command).Single();
            double[] end = sent.points[sent.points.Length - 1].positions;
            double[] half = end.Select((v, i) => (v + sent.points[0].positions[i]) * 0.5).ToArray();
            bus.Inbound("/joint_states", new JointStateMsg { name = sent.joint_names, position = half });
            yield return null;
            string[] unityNames = robot.Names.ToLocal(sent.joint_names);
            for (int i = 0; i < unityNames.Length; i++)
            {
                Assert.IsTrue(robot.Model.TryGetJointAngle(unityNames[i], out float shown));
                Assert.AreEqual(half[i], shown, 2e-3, $"{unityNames[i]} follows the ROS robot while it executes");
            }
        }

        [UnityTest]
        public IEnumerator ThePanel_OpensFromTheKeyboard_InTheView_AndItsButtonsTakeClicks()
        {
            Assert.IsFalse(ui.TierThree.IsOpen);
            Key panelKey = controls.MouseBackend.PanelKey;
            yield return Tap(panelKey);
            Assert.IsTrue(ui.TierThree.IsOpen, $"{panelKey} opens the tier 3 panel.");

            // All of it is in the view, whatever the camera's pitch.
            var corners = new Vector3[4];
            ui.TierThree.Canvas.GetComponent<RectTransform>().GetWorldCorners(corners);
            foreach (Vector3 corner in corners) OnScreen(corner, "A corner of the panel");

            // It opens on the Scene tab, the first of the strip, whatever order the plugins started in.
            Assert.AreEqual("scene", ui.TierThree.ActiveTab.Id, "The panel opens on its first tab.");
            Assert.AreEqual("scene", ui.TierThree.Tabs[0].Id);
            Assert.IsFalse(ui.TierThree.ActiveTab.Content.GetComponentsInChildren<Transform>(true).Any(x => x.name == "Hint"),
                "The Scene tab has nothing to explain: its buttons are always available.");

            // Tier 1's Menu button does what the key does: a click closes the panel, another opens it.
            Button menu = ui.TierOne.Canvas.GetComponentsInChildren<Button>().First(b => b.name == "Menu");
            yield return Click(OnScreen(menu, "Menu"));
            Assert.IsFalse(ui.TierThree.IsOpen, "A click on Menu closes the open panel.");
            yield return Click(OnScreen(menu, "Menu"));
            Assert.IsTrue(ui.TierThree.IsOpen, "A click on Menu opens the panel.");
            ui.TierThree.Canvas.GetComponent<RectTransform>().GetWorldCorners(corners);
            foreach (Vector3 corner in corners) OnScreen(corner, "A corner of the panel opened from Menu");

            // The Scene tab, which the panel opened on, adds an obstacle.
            Assert.AreEqual("scene", ui.TierThree.ActiveTab.Id);
            int before = host.Context.Environment.Count;
            yield return Click(OnScreen(PanelButton("Cube"), "Add Cube"));
            Assert.AreEqual(before + 1, host.Context.Environment.Count, "A click on Add Cube added an obstacle.");
            Assert.AreEqual(SelectionKind.Obstacle, selection.CurrentKind, "The new obstacle is selected.");

            // The MoveIt tab.
            yield return Click(OnScreen(PanelButton($"Tab_{moveIt.SettingsTabId}"), "MoveIt tab"));
            Assert.AreEqual(moveIt.SettingsTabId, ui.TierThree.ActiveTab.Id);
            MoveItPlanningClient client = moveIt.SessionFor(robot).Client;
            Assert.IsFalse(client.IsMirroring);
            yield return Click(OnScreen(PanelButton("Mirror"), "Mirror joints"));
            Assert.IsTrue(client.IsMirroring, "A click on Mirror joints switched the mirror on.");

            yield return Tap(panelKey);
            Assert.IsFalse(ui.TierThree.IsOpen, $"{panelKey} closes the panel again.");
        }

        // The Settings tab: the scene wires nothing, its collision haptics put their controls
        // there. The sliders are dragged with the mouse, through the scene's own event system.
        [UnityTest]
        public IEnumerator TheSettingsTab_SetsTheCollisionHaptics_WithTheMouse()
        {
            CollisionHaptics haptics = Find<CollisionHaptics>();
            Assert.IsNotNull(haptics, "The reference scene has collision haptics.");
            Assert.IsTrue(haptics.HapticsEnabled);
            Assert.AreEqual(1f, haptics.Strength, 1e-6f, "The scene's value is the default.");
            Assert.AreEqual(0.3f, haptics.Distance, 1e-6f);

            yield return Tap(controls.MouseBackend.PanelKey);
            Assert.IsTrue(ui.TierThree.IsOpen);
            Assert.AreEqual(SettingsTab.TabId, ui.TierThree.Tabs[^1].Id, "Settings is the last tab of the strip.");
            CollectionAssert.AreEqual(new[] { "Scene", "MoveIt", "MTC", "Demonstrations", "Settings" },
                ui.TierThree.Tabs.Select(t => t.Label).ToArray(), "The strip, left to right.");
            yield return Click(OnScreen(PanelButton($"Tab_{SettingsTab.TabId}"), "Settings tab"));
            Assert.AreEqual(SettingsTab.TabId, ui.TierThree.ActiveTab.Id);
            Assert.AreEqual(3, ui.Settings.InteractiveElementCount, "A switch and two sliders.");

            Slider distance = PanelSlider("CollisionHapticsDistance");
            Slider strength = PanelSlider("CollisionHapticsStrength");
            Assert.AreEqual("Distance: 30 cm", TextOf(distance));
            Assert.AreEqual("Strength: 100%", TextOf(strength));
            Assert.AreEqual("Collision haptics: on", TextOf(PanelButton("CollisionHaptics")));

            // Drag the distance from where it is to four fifths of the way along.
            float pixels = Vector2.Distance(OnSlider(distance, 0f), OnSlider(distance, 1f));
            float tolerance = 0.45f / pixels * 2f + 0.011f;   // two pixels and a step
            Vector2 from = OnSlider(distance, Mathf.InverseLerp(distance.minValue, distance.maxValue, distance.value));
            Vector2 to = OnSlider(distance, 0.8f);
            yield return Point(from);
            input.Press(mouse.leftButton);
            yield return null;
            yield return null;
            for (int i = 1; i <= 6; i++)
            {
                input.Move(mouse.position, Vector2.Lerp(from, to, i / 6f));
                yield return null;
                if (i == 3) Assert.Greater(haptics.Distance, 0.31f, "The value follows the drag while the button is held.");
            }
            input.Release(mouse.leftButton);
            yield return null;
            yield return null;
            Assert.AreEqual(Mathf.Lerp(distance.minValue, distance.maxValue, 0.8f), haptics.Distance, tolerance, "The dragged value reached the collision haptics.");
            Assert.AreEqual(haptics.Distance, distance.value, 1e-5f);
            Assert.AreEqual($"Distance: {Mathf.RoundToInt(haptics.Distance * 100f)} cm", TextOf(distance));
            Assert.IsTrue(settings.TryGetFloat(CollisionHaptics.DistanceKey, out float kept) && Mathf.Approximately(kept, haptics.Distance), "It is kept for the next session.");
            Assert.IsNull(selection.Current, "A drag on the panel selects nothing in the world behind it.");

            // A click on the strength slider puts the handle there.
            yield return Click(OnSlider(strength, 0.25f));
            Assert.AreEqual(0.25f, haptics.Strength, 0.051f);
            Assert.AreEqual($"Strength: {Mathf.RoundToInt(haptics.Strength * 100f)}%", TextOf(strength));
            Assert.AreEqual(haptics.Strength, haptics.IntensityAt(0f), 1e-5f);

            // The switch.
            yield return Click(OnScreen(PanelButton("CollisionHaptics"), "Collision haptics"));
            Assert.IsFalse(haptics.HapticsEnabled, "A click on the switch turns the haptics off.");
            Assert.AreEqual("Collision haptics: off", TextOf(PanelButton("CollisionHaptics")));
            Assert.AreEqual(0f, haptics.IntensityAt(0f), 1e-6f);
            Assert.IsFalse(strength.interactable);
            yield return Click(OnScreen(PanelButton("CollisionHaptics"), "Collision haptics"));
            Assert.IsTrue(haptics.HapticsEnabled);
        }

        private Slider PanelSlider(string name) =>
            ui.TierThree.Canvas.GetComponentsInChildren<Slider>(false).FirstOrDefault(s => s.name == name);

        private static string TextOf(Component control) => control.GetComponentInChildren<TMPro.TextMeshProUGUI>().text;

        // The screen point over a slider at a fraction of its travel (0 = left end, 1 = right end).
        private Vector2 OnSlider(Slider slider, float fraction)
        {
            Assert.IsNotNull(slider, "The slider is on the tab.");
            var corners = new Vector3[4];
            ((RectTransform)slider.handleRect.parent).GetWorldCorners(corners);
            Vector3 left = (corners[0] + corners[1]) * 0.5f, right = (corners[2] + corners[3]) * 0.5f;
            return OnScreen(Vector3.Lerp(left, right, fraction), $"The slider '{slider.name}'");
        }

        [UnityTest]
        public IEnumerator AnAddedObstacle_IsDraggedWithTheRightButton()
        {
            var tab = Find<Erupt.UiBindings.ScenePlacementTab>();
            GameObject cube = tab.Add(PrimitiveType.Cube);
            Assert.IsNotNull(cube);
            yield return null;
            yield return null;
            Physics.SyncTransforms();
            Assert.AreSame(cube, selection.Current.GameObject, "A new shape is the selection.");

            Vector3 before = cube.transform.position;
            Vector2 from = OnScreen(before, "The new cube");
            Vector2 to = from + new Vector2(0.12f, 0.05f) * cam.pixelHeight;
            yield return Point(from);
            input.Press(mouse.rightButton);
            yield return null;
            for (int i = 1; i <= 8; i++)
            {
                input.Move(mouse.position, Vector2.Lerp(from, to, i / 8f));
                yield return Seconds(0.03f);
            }
            yield return Seconds(0.3f);   // the router smooths a drag
            input.Release(mouse.rightButton);
            yield return null;
            yield return null;

            Assert.Greater(Vector3.Distance(before, cube.transform.position), 0.05f, "The cube followed the drag.");
            Vector2 where = cam.WorldToScreenPoint(cube.transform.position);
            Assert.Less(Vector2.Distance(where, to), 0.03f * cam.pixelHeight, "The cube is under the pointer.");

            Assert.IsTrue(host.Context.Undo.Undo(), "The move can be undone.");
            Assert.Less(Vector3.Distance(before, cube.transform.position), 1e-4f);
        }

        [UnityTest]
        public IEnumerator TheKeys_MoveAndTurnTheView()
        {
            Vector3 start = cam.transform.position;
            Vector3 forward = cam.transform.forward;
            yield return Hold(Key.W, 0.25f);
            Vector3 moved = cam.transform.position - start;
            Assert.Greater(Vector3.Dot(moved, forward), 0.01f, "W moves the view forward.");

            float yaw = cam.transform.eulerAngles.y;
            yield return Hold(Key.RightArrow, 0.25f);
            Assert.Greater(Mathf.DeltaAngle(yaw, cam.transform.eulerAngles.y), 0.5f, "The right arrow turns the view to the right.");

            yield return Tap(Key.F);
            OnScreen(HandlePosition, "After F, the end-effector handle");
            Assert.Less(Vector3.Distance(cam.transform.position, start), 0.05f, "F goes back to the view of the robots.");
        }

        [UnityTest]
        public IEnumerator WithAnXrHeadDevice_TheDesktopControlsStandDown()
        {
            Vector2 handle = OnScreen(HandlePosition, "The end-effector handle");
            Transform tierOne = ui.TierOne.transform;
            Assert.IsTrue(tierOne.IsChildOf(cam.transform), "On the desktop tier 1 rides the camera.");

            XRHMD head = InputSystem.AddDevice<XRHMD>();
            yield return null;
            yield return null;
            Assert.IsFalse(controls.IsActive, "A head device takes the view: the desktop controls stand down.");
            Assert.IsFalse(controls.MouseBackend.IsActive, "No mouse ray next to the controllers' rays.");
            Assert.IsFalse(tierOne.IsChildOf(cam.transform), "Tier 1 is back on the wrist anchor.");
            foreach (TrackedPoseDriver driver in cam.GetComponents<TrackedPoseDriver>())
                Assert.IsTrue(driver.enabled, "The camera is the head's again.");

            // Neither the keys nor the mouse do anything to the scene now.
            Vector3 position = cam.transform.position;
            Quaternion rotation = cam.transform.rotation;
            yield return Hold(Key.W, 0.15f);
            yield return Hold(Key.RightArrow, 0.15f);
            // (The tracked pose driver has the camera; without a pose it leaves it at the rig's origin.)
            Vector3 after = cam.transform.position;
            yield return Hold(Key.W, 0.15f);
            Assert.AreEqual(after, cam.transform.position, "The keys do not move the view.");
            yield return Click(handle);
            Assert.AreEqual(SelectionKind.None, selection.CurrentKind, "A click selects nothing.");
            yield return Tap(controls.MouseBackend.PanelKey);
            Assert.IsFalse(ui.TierThree.IsOpen, "The panel key does nothing.");

            InputSystem.RemoveDevice(head);
            yield return null;
            yield return null;
            Assert.IsTrue(controls.IsActive, "With the head device gone the desktop controls take over again.");
            Assert.IsTrue(tierOne.IsChildOf(cam.transform));
            _ = position; _ = rotation;
        }
    }
}
#endif
