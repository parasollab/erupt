using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// The desktop controls on an ERUPT root built in code: without a head device they take
    /// the view (the robots in sight, tier 1 at the top of it, tier 3 square in front of the
    /// camera), with one they stand down and hand everything back, and a checkbox switches
    /// them off altogether.
    /// </summary>
    public class DesktopControlsTests
    {
        private sealed class Tips : IProximitySource
        {
            public readonly List<Vector3> Points = new();
            public void GetProbePoints(List<Vector3> points) => points.AddRange(Points);
            public bool IsOwnCollider(Collider collider) => false;
        }

        private readonly InputTestFixture input = new InputTestFixture();
        private Mouse mouse;
        private Keyboard keyboard;

        private GameObject rigGo, cameraGo, wristGo, routerGo, eruptGo;
        private Camera cam;
        private TrackedPoseDriver driver;
        private InteractionRouter router;
        private TierUiRig ui;
        private DesktopMouseBackend backend;
        private DesktopControls controls;
        private readonly Tips tips = new Tips();

        private static readonly Vector3 RigStart = new Vector3(60f, 0f, -4f);
        private static readonly Vector3 Robot = new Vector3(60f, 1.2f, 0f);

        // Builds the root inactive, so a test can set the switches before anything wakes.
        private void Build(bool showHint = true)
        {
            input.Setup();
            // The fixture's clock starts at zero and gains 1/60 s per frame. A headless run renders
            // thousands of frames a second, so that clock soon crosses the moment the editor entered
            // play mode, and the Input System drops the state events stamped around it. Start past it.
            input.currentTime = 1e6;
            mouse = InputSystem.AddDevice<Mouse>();
            keyboard = InputSystem.AddDevice<Keyboard>();

            // An XR rig as the scenes have it: the camera under an offset, with a pose driver.
            rigGo = new GameObject("XR Rig");
            rigGo.transform.position = RigStart;
            cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraGo.transform.SetParent(rigGo.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 1.36f, 0f);
            cam = cameraGo.AddComponent<Camera>();
            driver = cameraGo.AddComponent<TrackedPoseDriver>();
            // The wrist anchor tier 1 rides in a headset; with no controller it is hidden.
            wristGo = new GameObject("Wrist Anchor");
            wristGo.transform.SetParent(rigGo.transform, false);
            wristGo.SetActive(false);

            eruptGo = new GameObject("ERUPT");
            eruptGo.SetActive(false);
            ui = eruptGo.AddComponent<TierUiRig>();
            backend = eruptGo.AddComponent<DesktopMouseBackend>();
            controls = eruptGo.AddComponent<DesktopControls>();
            controls.ShowsHint = showHint;

            routerGo = new GameObject("router");
            routerGo.SetActive(false);
            router = routerGo.AddComponent<InteractionRouter>();

            tips.Points.Clear();
            tips.Points.Add(Robot);
        }

        private readonly List<string> logged = new();
        private void OnLog(string condition, string stackTrace, LogType type) => logged.Add(condition);
        private bool LoggedTheKeys => logged.Any(l => l.StartsWith("[ERUPT] No headset: desktop controls are on."));

        private IEnumerator Wake()
        {
            logged.Clear();
            Application.logMessageReceived += OnLog;
            // The router wakes first (it finds the backend on the root that is still asleep),
            // so the rig finds it. In a scene the bar hangs on the wrist anchor the rig was
            // given; here it is put there before the first Update.
            routerGo.SetActive(true);
            router.AddProximitySource(tips);
            eruptGo.SetActive(true);
            ui.TierOne.transform.SetParent(wristGo.transform, false);
            yield return null;
            yield return null;
            // The view is chosen once the robots have stood still for a moment.
            yield return Seconds(0.4f);
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= OnLog;
            Object.DestroyImmediate(eruptGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(rigGo);
            input.TearDown();
        }

        private Vector3 Viewport(Vector3 world) => cam.WorldToViewportPoint(world);

        private void AssertInView(Vector3 world, string what)
        {
            Vector3 v = Viewport(world);
            Assert.IsTrue(v.z > cam.nearClipPlane && v.x > 0f && v.x < 1f && v.y > 0f && v.y < 1f, $"{what} is in the view (viewport {v})");
        }

        private IEnumerator Tap(Key key)
        {
            input.Press(keyboard[key]);
            yield return null;
            input.Release(keyboard[key]);
            yield return null;
        }

        private static IEnumerator Seconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        [UnityTest]
        public IEnumerator WithoutAHeadDevice_TheControlsTakeTheView_AndShowTheRobots()
        {
            Build();
            yield return Wake();
            Assert.IsTrue(LoggedTheKeys, "The keys are logged when the controls start.");
            Assert.AreEqual(1, logged.Count(l => l.StartsWith("[ERUPT] No headset")), "once");

            Assert.IsFalse(DesktopControls.XrHeadPresent());
            Assert.IsTrue(controls.IsActive);
            Assert.AreSame(cam, controls.View);
            Assert.IsTrue(backend.IsActive, "The mouse backend is in use.");
            Assert.IsFalse(driver.enabled, "The camera's pose driver would pin it to the rig's origin.");

            // The robots' tips are in view, from the side the rig stands on, at working distance.
            AssertInView(Robot, "The robot's end effector");
            Vector3 fromRobot = cam.transform.position - Robot;
            Assert.Greater(Vector3.Dot(new Vector3(fromRobot.x, 0f, fromRobot.z).normalized, Vector3.back), 0.95f, "The camera is on the rig's side of the robot.");
            Assert.That(fromRobot.magnitude, Is.InRange(1.2f, 1.8f));
            Assert.Greater(cam.transform.position.y, Robot.y, "It looks down at the end effector a little.");
            Assert.That(Viewport(Robot).y, Is.InRange(0.25f, 0.5f), "The end effector is below the middle of the view: its menu opens above it.");

            // A flat view cannot step closer or look round things: tier 2 menus are kept readable and in front.
            Assert.Greater(ui.TierTwo.ReadableDistance, 0f);
            Assert.IsTrue(ui.TierTwo.ClearsLineOfSight);

            // The hint is on screen, once, and H takes it away.
            Assert.IsTrue(controls.HintVisible);
            StringAssert.Contains("Tab or the Menu button at the top: panel", controls.ControlsSummary,
                "The hint names the key and the tier 1 button that open the panel.");
            yield return Tap(Key.H);
            Assert.IsFalse(controls.HintVisible);
        }

        [UnityTest]
        public IEnumerator TheView_GoesRoundWhatHidesTheEndEffector_AndKeepsClearOfThings()
        {
            Build(showHint: false);
            // A wall between the rig's side and the robot: from there the end effector is hidden.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = Robot + Vector3.back * 0.5f;
            wall.transform.localScale = new Vector3(1.2f, 3f, 0.05f);
            try
            {
                yield return Wake();
                Assert.IsTrue(controls.IsActive);
                AssertInView(Robot, "The robot's end effector");
                Vector3 eye = cam.transform.position;
                Assert.IsFalse(Physics.Raycast(eye, (Robot - eye).normalized, (Robot - eye).magnitude - 0.15f, ~0, QueryTriggerInteraction.Ignore),
                    "Nothing stands between the camera and the end effector.");
                Vector3 fromRobot = eye - Robot;
                Assert.Less(Vector3.Dot(new Vector3(fromRobot.x, 0f, fromRobot.z).normalized, Vector3.back), 0.9f, "The view went round the wall.");

                // A panel that hangs where the camera would go (a trigger, like a canvas has): the camera goes elsewhere.
                Object.DestroyImmediate(wall);
                wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.GetComponent<Collider>().isTrigger = true;
                wall.transform.localScale = new Vector3(0.8f, 0.6f, 0.02f);
                controls.SetView(RigStart + Vector3.up * 1.36f, Quaternion.identity);
                Vector3 fromRigSide = Robot + Vector3.back * (1.4f * Mathf.Cos(18f * Mathf.Deg2Rad)) + Vector3.up * (1.4f * Mathf.Sin(18f * Mathf.Deg2Rad));
                wall.transform.position = fromRigSide + new Vector3(0.1f, 0f, 0.1f);
                Physics.SyncTransforms();
                Assert.IsTrue(controls.FrameRobots());
                Assert.Greater(Vector3.Distance(cam.transform.position, wall.transform.position), 0.5f, "The camera does not start with its nose in a panel.");
                AssertInView(Robot, "The robot's end effector");
            }
            finally
            {
                Object.DestroyImmediate(wall);
            }
        }

        [UnityTest]
        public IEnumerator TheHintCheckbox_TakesTheHintAndTheLogAway()
        {
            Build(showHint: false);
            yield return Wake();
            Assert.IsTrue(controls.IsActive);
            Assert.IsFalse(controls.HintVisible);
            Assert.IsFalse(LoggedTheKeys);
        }

        [UnityTest]
        public IEnumerator TierOne_RidesTheTopOfTheView_AndTheKeysMoveTheView()
        {
            Build(showHint: false);
            yield return Wake();

            Button[] buttons = ui.TierOne.Canvas.GetComponentsInChildren<Button>();
            Assert.AreEqual(3, buttons.Length);
            foreach (Button button in buttons)
            {
                AssertInView(button.transform.position, $"Tier 1 '{button.name}'");
                Assert.Greater(Viewport(button.transform.position).y, 0.8f, "at the top of the view");
            }

            Vector3 start = cam.transform.position;
            Vector3 forward = cam.transform.forward;
            input.Press(keyboard[Key.W]);
            yield return Seconds(0.2f);
            input.Release(keyboard[Key.W]);
            yield return null;
            Assert.Greater(Vector3.Dot(cam.transform.position - start, forward), 0.05f, "W moves forward.");

            float yaw = cam.transform.eulerAngles.y;
            input.Press(keyboard[Key.LeftArrow]);
            yield return Seconds(0.2f);
            input.Release(keyboard[Key.LeftArrow]);
            yield return null;
            Assert.Less(Mathf.DeltaAngle(yaw, cam.transform.eulerAngles.y), -2f, "The left arrow turns the view to the left.");

            float height = cam.transform.position.y;
            input.Press(keyboard[Key.E]);
            yield return Seconds(0.2f);
            input.Release(keyboard[Key.E]);
            yield return null;
            Assert.Greater(cam.transform.position.y, height + 0.05f, "E moves up.");

            // Wherever the view goes, tier 1 stays in it.
            foreach (Button button in buttons) AssertInView(button.transform.position, $"Tier 1 '{button.name}' after moving");

            yield return Tap(Key.F);
            Assert.Less(Vector3.Distance(cam.transform.position, start), 1e-3f, "F is back at the view of the robots.");
        }

        [UnityTest]
        public IEnumerator ThePanelKey_OpensTierThree_SquareInFrontOfTheCamera_WhateverItsPitch()
        {
            Build(showHint: false);
            yield return Wake();
            ui.AddTab("test", "Test", content => UiBuilder.CreateButton("in tab", content, "In tab", new Vector2(300f, 60f)));

            // Looking steeply down at a table.
            controls.SetView(new Vector3(60f, 2.2f, -1f), Quaternion.Euler(55f, 10f, 0f));
            yield return null;
            Assert.AreEqual(55f, cam.transform.eulerAngles.x, 0.01f);

            yield return Tap(backend.PanelKey);
            Assert.IsTrue(ui.TierThree.IsOpen);
            var corners = new Vector3[4];
            ui.TierThree.Canvas.GetComponent<RectTransform>().GetWorldCorners(corners);
            foreach (Vector3 corner in corners) AssertInView(corner, "A corner of the panel");
            Assert.Greater(Vector3.Dot(ui.TierThree.transform.forward, cam.transform.forward), 0.999f, "The panel faces the camera squarely.");

            yield return Tap(backend.PanelKey);
            Assert.IsFalse(ui.TierThree.IsOpen);
        }

        [UnityTest]
        public IEnumerator WithAHeadDevice_TheControlsStandDown_AndHandEverythingBack()
        {
            Build(showHint: false);
            yield return Wake();
            Assert.IsTrue(controls.IsActive);
            Transform tierOne = ui.TierOne.transform;
            Assert.AreSame(cam.transform, tierOne.parent);

            XRHMD head = InputSystem.AddDevice<XRHMD>();
            yield return null;
            Assert.IsTrue(DesktopControls.XrHeadPresent());
            Assert.IsFalse(controls.IsActive);
            Assert.IsNull(controls.View);
            Assert.IsTrue(backend.Suppressed);
            Assert.IsFalse(backend.IsActive);
            Assert.IsTrue(driver.enabled, "The camera's pose driver is on again.");
            Assert.AreSame(wristGo.transform, tierOne.parent, "Tier 1 is back on the wrist anchor.");
            Assert.AreEqual(0f, ui.TierTwo.ReadableDistance, "Tier 2 is a thing in the room again.");
            Assert.IsFalse(ui.TierTwo.ClearsLineOfSight);
            Assert.AreEqual(Vector3.one, tierOne.localScale);
            Assert.AreEqual(Vector3.zero, tierOne.localPosition);

            // The keys move nothing, the panel key opens nothing, and the panel would open in
            // front of the head again (the default placement).
            Vector3 position = cam.transform.position;
            Quaternion rotation = cam.transform.rotation;
            input.Press(keyboard[Key.W]);
            input.Press(keyboard[Key.LeftArrow]);
            yield return Seconds(0.1f);
            input.Release(keyboard[Key.W]);
            input.Release(keyboard[Key.LeftArrow]);
            yield return null;
            Assert.AreEqual(position, cam.transform.position);
            Assert.AreEqual(rotation, cam.transform.rotation);
            yield return Tap(backend.PanelKey);
            Assert.IsFalse(ui.TierThree.IsOpen);

            // Started with a head device there, the controls never take the view at all: see
            // the next test. Here the device goes away again and they take over.
            InputSystem.RemoveDevice(head);
            yield return null;
            yield return null;
            Assert.IsTrue(controls.IsActive);
            Assert.IsFalse(backend.Suppressed);
            Assert.IsFalse(driver.enabled);
            Assert.AreSame(cam.transform, tierOne.parent);
        }

        [UnityTest]
        public IEnumerator StartedWithAHeadDevice_TheControlsNeverTouchTheScene()
        {
            Build();
            InputSystem.AddDevice<XRHMD>();
            Vector3 position = cam.transform.position;
            yield return Wake();

            Assert.IsFalse(controls.IsActive);
            Assert.IsTrue(backend.Suppressed);
            Assert.IsTrue(driver.enabled);
            Assert.AreEqual(position, cam.transform.position, "The camera stays where the rig has it.");
            Assert.AreSame(wristGo.transform, ui.TierOne.transform.parent);
            Assert.IsFalse(controls.HintVisible);
            Assert.IsFalse(LoggedTheKeys);
        }

        [UnityTest]
        public IEnumerator SwitchedOff_TheControlsDoNothing()
        {
            Build();
            controls.Enabled = false;
            Vector3 position = cam.transform.position;
            yield return Wake();

            Assert.IsFalse(controls.IsActive);
            Assert.IsTrue(backend.Suppressed);
            Assert.IsFalse(backend.IsActive);
            Assert.IsTrue(driver.enabled);
            Assert.AreEqual(position, cam.transform.position);
            Assert.IsFalse(LoggedTheKeys);

            // Switched on at runtime they take over; switched off again they hand back.
            controls.Enabled = true;
            yield return null;
            yield return null;
            Assert.IsTrue(controls.IsActive);
            controls.Enabled = false;
            yield return null;
            Assert.IsFalse(controls.IsActive);
            Assert.IsTrue(driver.enabled);
        }

        [UnityTest]
        public IEnumerator WithoutARobot_TheViewStaysWhereTheRigHasIt()
        {
            Build(showHint: false);
            tips.Points.Clear();
            Vector3 position = cam.transform.position;
            yield return Wake();
            yield return Seconds(0.1f);

            Assert.IsTrue(controls.IsActive);
            Assert.Less(Vector3.Distance(position, cam.transform.position), 1e-4f);
            Assert.IsFalse(controls.FrameRobots(), "Nothing to look at.");
        }
    }
}
