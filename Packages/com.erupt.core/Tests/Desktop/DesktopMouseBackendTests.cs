using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// The mouse backend through the router, driven by a simulated mouse and keyboard: the
    /// left button selects what is under the pointer, the right button drags, the wheel is the
    /// axis (or the depth of a drag), one key is Activate, a press on a menu is not a world
    /// select, and a suppressed backend says nothing.
    /// </summary>
    public class DesktopMouseBackendTests
    {
        private readonly InputTestFixture input = new InputTestFixture();
        private Mouse mouse;
        private Keyboard keyboard;

        private GameObject cameraGo, hostGo, routerGo, cube;
        private Camera cam;
        private DesktopMouseBackend backend;
        private InteractionRouter router;
        private readonly List<InteractionIntent> intents = new();
        private readonly List<GameObject> extras = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            input.Setup();
            // The fixture's clock starts at zero and gains 1/60 s per frame. A headless run renders
            // thousands of frames a second, so that clock soon crosses the moment the editor entered
            // play mode, and the Input System drops the state events stamped around it. Start past it.
            input.currentTime = 1e6;
            mouse = InputSystem.AddDevice<Mouse>();
            keyboard = InputSystem.AddDevice<Keyboard>();

            cameraGo = new GameObject("camera") { tag = "MainCamera" };
            cam = cameraGo.AddComponent<Camera>();
            cameraGo.transform.SetPositionAndRotation(new Vector3(40f, 1f, -3f), Quaternion.identity);

            cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "cube";
            cube.transform.position = new Vector3(40f, 1f, 0f);
            cube.transform.localScale = Vector3.one * 0.4f;

            // The backend exists before the router wakes, which is how the router finds it.
            hostGo = new GameObject("ERUPT");
            backend = hostGo.AddComponent<DesktopMouseBackend>();
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            intents.Clear();
            router.Select += intents.Add;
            router.BeginDrag += intents.Add;
            router.Drag += intents.Add;
            router.EndDrag += intents.Add;
            router.Axis += intents.Add;
            router.Activate += intents.Add;

            yield return null;
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in extras) if (go != null) Object.DestroyImmediate(go);
            extras.Clear();
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(hostGo);
            Object.DestroyImmediate(cube);
            Object.DestroyImmediate(cameraGo);
            input.TearDown();
        }

        private Vector2 ScreenOf(Vector3 world) => cam.WorldToScreenPoint(world);

        private IEnumerator Point(Vector2 screen)
        {
            input.Move(mouse.position, screen);
            yield return null;
        }

        private InteractionIntent[] Of(IntentKind kind) => intents.Where(i => i.Kind == kind).ToArray();

        [UnityTest]
        public IEnumerator TheBackend_IsFoundByTheRouter_AndSaysItIsAMouse()
        {
            CollectionAssert.Contains(router.Sources.ToList(), backend);
            Assert.AreEqual(Modality.Mouse, backend.Modality);
            Assert.IsTrue(backend.Has(Capability.Ray));
            Assert.IsTrue(backend.IsActive);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ALeftClick_SelectsWhatIsUnderThePointer()
        {
            yield return Point(ScreenOf(cube.transform.position));
            input.Press(mouse.leftButton);
            yield return null;
            yield return null;   // held: one press is one select
            input.Release(mouse.leftButton);
            yield return null;

            InteractionIntent select = Of(IntentKind.Select).Single();
            Assert.AreSame(cube, select.Target);
            Assert.AreEqual(Modality.Mouse, select.Sample.Modality);
            Assert.AreEqual(backend.SourceId, select.Sample.SourceId);

            // A click next to everything is a select that hit nothing (it clears the selection).
            intents.Clear();
            yield return Point(new Vector2(2f, 2f));
            input.Press(mouse.leftButton);
            yield return null;
            input.Release(mouse.leftButton);
            yield return null;
            Assert.IsFalse(Of(IntentKind.Select).Single().HasHit);
        }

        [UnityTest]
        public IEnumerator TheRightButtonHeld_Drags_AndTheWheelMovesTheDraggedPointAlongTheRay()
        {
            Vector2 start = ScreenOf(cube.transform.position);
            yield return Point(start);
            input.Press(mouse.rightButton);
            yield return null;
            InteractionIntent begin = Of(IntentKind.BeginDrag).Single();
            Assert.AreSame(cube, begin.Target);
            Assert.IsEmpty(Of(IntentKind.Select), "The right button does not select.");

            yield return Point(start + new Vector2(30f, 0f));
            yield return null;
            InteractionIntent[] drags = Of(IntentKind.Drag);
            Assert.IsNotEmpty(drags);
            Assert.Greater(drags.Last().Ray.direction.x, begin.Ray.direction.x, "The ray follows the pointer.");

            // One notch away from the user: the ray's origin moves along the ray, and with it
            // whatever is held at a fixed distance on it. No axis intent while dragging.
            input.Set(mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            input.Set(mouse.scroll, Vector2.zero);
            // The router smooths a drag, so the pushed ray settles over a moment.
            float until = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            InteractionIntent pushed = Of(IntentKind.Drag).Last();
            Ray pointer = cam.ScreenPointToRay(start + new Vector2(30f, 0f));
            Assert.AreEqual(0.05f, Vector3.Dot(pushed.Ray.origin - pointer.origin, pointer.direction), 0.005f,
                "One notch pushes the dragged point 5 cm along the ray.");
            Assert.IsEmpty(Of(IntentKind.Axis));

            input.Release(mouse.rightButton);
            yield return null;
            Assert.AreEqual(1, Of(IntentKind.EndDrag).Length);
        }

        [UnityTest]
        public IEnumerator TheWheel_WithoutADrag_IsTheAxis()
        {
            yield return Point(ScreenOf(cube.transform.position));
            input.Set(mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            input.Set(mouse.scroll, Vector2.zero);
            yield return null;
            InteractionIntent[] axis = Of(IntentKind.Axis);
            Assert.IsNotEmpty(axis);
            Assert.Greater(axis[0].Axis.y, 0f);
            Assert.AreEqual(0f, axis[0].Axis.x);
        }

        [UnityTest]
        public IEnumerator ThePanelKey_IsTheActivateIntent()
        {
            Assert.AreEqual(Key.Tab, backend.PanelKey);
            input.Press(keyboard[backend.PanelKey]);
            yield return null;
            yield return null;   // held: one press, one intent
            input.Release(keyboard[backend.PanelKey]);
            yield return null;
            Assert.AreEqual(1, Of(IntentKind.Activate).Length);
        }

        [UnityTest]
        public IEnumerator APressOnAMenu_IsNotAWorldSelect()
        {
            var events = new GameObject("events", typeof(EventSystem));
            extras.Add(events);
            // A menu between the camera and the cube, a little above the line between them:
            // it hides the upper part of the cube and leaves the lower edge in the clear.
            Canvas canvas = UiBuilder.CreateWorldCanvas("menu", null, new Vector2(400f, 200f));
            extras.Add(canvas.gameObject);
            canvas.worldCamera = cam;
            canvas.transform.position = new Vector3(40f, 1.05f, -1.5f);
            Button button = UiBuilder.CreateButton("press me", canvas.transform, "Press me", new Vector2(300f, 64f));
            yield return null;
            yield return null;
            // The menu's press-consuming collider is where the menu is, whether or not a physics
            // step has run since it was put there.
            Physics.SyncTransforms();

            Vector2 onButton = ScreenOf(button.transform.position);
            Assert.IsTrue(backend.IsOverUi(onButton), "The pointer is over the menu.");
            yield return Point(onButton);
            input.Press(mouse.leftButton);
            yield return null;
            input.Release(mouse.leftButton);
            yield return null;
            Assert.IsEmpty(Of(IntentKind.Select), "The press belongs to the menu.");

            // Next to the menu (below it) the cube behind it is selected as ever.
            Vector2 beside = ScreenOf(cube.transform.position + Vector3.down * 0.15f);
            Assert.IsFalse(backend.IsOverUi(beside));
            yield return Point(beside);
            input.Press(mouse.leftButton);
            yield return null;
            input.Release(mouse.leftButton);
            yield return null;
            Assert.AreSame(cube, Of(IntentKind.Select).Single().Target);
        }

        [UnityTest]
        public IEnumerator Suppressed_TheBackendSaysNothing_AndLetsGoOfADrag()
        {
            yield return Point(ScreenOf(cube.transform.position));
            input.Press(mouse.rightButton);
            yield return null;
            Assert.AreEqual(1, Of(IntentKind.BeginDrag).Length);

            backend.Suppressed = true;
            Assert.AreEqual(1, Of(IntentKind.EndDrag).Length, "A drag in progress ends when the backend is suppressed.");
            Assert.IsFalse(backend.IsActive);
            Assert.IsFalse(router.AnySourceHas(Capability.Ray), "No mouse ray for the router's users.");

            intents.Clear();
            input.Release(mouse.rightButton);
            yield return null;
            input.Press(mouse.leftButton);
            yield return null;
            input.Release(mouse.leftButton);
            input.Press(keyboard[backend.PanelKey]);
            yield return null;
            input.Release(keyboard[backend.PanelKey]);
            input.Set(mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            Assert.IsEmpty(intents, "A suppressed backend raises no intent.");

            backend.Suppressed = false;
            input.Set(mouse.scroll, Vector2.zero);
            yield return null;
            input.Press(mouse.leftButton);
            yield return null;
            Assert.AreEqual(1, Of(IntentKind.Select).Length, "Back in use, it selects again.");
        }
    }
}
