#if UNITY_EDITOR && ERUPT_PLUGIN_MOVEIT
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Erupt.Environment;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ros.Tests;
using Erupt.Ui;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using RosMessageTypes.Moveit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.TestTools;

namespace Erupt.Plugins.Ar.Tests
{
    public class TabletopWorkbenchTests
    {
        private const string ScenePath = "Assets/Scenes/ARWorkbenchFR3.unity";
        private Scene scene;
        private GameObject connection;
        private string temporaryScene;

        private IEnumerator OpenScene(string path, FakeRosBus bus)
        {
            Assume.That(File.Exists(path), "The reference app is absent.");
            RosBus.Override(bus);
            connection = IdleRosConnection.Create();
            // The Meta and XR components report unavailable hardware in a headless editor.
            LogAssert.ignoreFailingMessages = true;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(path);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator RoomOverlayButtonKeepsNewFurnitureInThePlanningSceneWhenHidden()
        {
            // Package Manager samples live outside the asset database until imported.
            temporaryScene = AssetDatabase.GenerateUniqueAssetPath("Assets/RoomOverlayTest.unity");
            File.Copy("Packages/com.erupt.plugin.ar/Samples~/ArScene/ArScene.unity", temporaryScene);
            AssetDatabase.ImportAsset(temporaryScene);
            yield return OpenScene(temporaryScene, new FakeRosBus());
            var roots = scene.GetRootGameObjects();
            var bridge = roots.SelectMany(root => root.GetComponentsInChildren<SceneAnchorCollisionBridge>()).Single();
            var button = roots.SelectMany(root => root.GetComponentsInChildren<Button>(true)).Single(b => b.name == "RoomOverlays");
            var registry = roots.SelectMany(root => root.GetComponentsInChildren<EnvironmentRegistry>()).Single();
            var room = new GameObject("Test room").AddComponent<MRUKRoom>();
            room.transform.SetParent(bridge.transform, false);
            var anchor = new GameObject("Scanned furniture").AddComponent<MRUKAnchor>();
            anchor.transform.SetParent(room.transform, false);
            // The SDK fills these properties from the device scan.
            typeof(MRUKAnchor).GetProperty(nameof(MRUKAnchor.VolumeBounds)).SetValue(anchor, new Bounds(Vector3.zero, Vector3.one));
            room.Anchors.Add(anchor);

            Assert.IsTrue(bridge.VisualsVisible);
            button.onClick.Invoke();
            Assert.IsFalse(bridge.VisualsVisible);
            MRUK.Instance.RoomCreatedEvent.Invoke(room);
            var box = bridge.GetComponentsInChildren<MeshRenderer>().Single();
            string id = $"scene_{anchor.Label}_{anchor.GetInstanceID()}";
            Assert.IsFalse(box.enabled);
            Assert.IsTrue(box.GetComponent<BoxCollider>().enabled && box.gameObject.activeInHierarchy);
            Assert.IsTrue(registry.TryGet(id, out var obstacle));

            button.onClick.Invoke();
            Assert.IsTrue(box.enabled);
            button.onClick.Invoke();
            Assert.IsFalse(box.enabled);
            Assert.IsTrue(registry.TryGet(id, out var hiddenObstacle));
            Assert.AreSame(obstacle, hiddenObstacle);
        }

        [UnityTest]
        public IEnumerator WorkbenchRegistersTheBlockAndPlacesTheArmOnlyOnConfirmation()
        {
            var bus = new FakeRosBus();
            yield return OpenScene(ScenePath, bus);
            var host = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PluginHost>()).Single();
            Assert.IsTrue(host.TryGet(out ArPlugin plugin), "The AR rig should be present when the module is installed.");
            var robot = host.Context.Robots.All.Single();
            Assert.IsTrue(robot.PlanningFrame.IsChildOf(robot.transform));
            Assert.IsTrue(host.Context.Environment.TryGet("workbench_block", out var block));
            Assert.IsNotNull(block.GetComponent<SelectableGrabController>());
            Assert.IsNotNull(block.GetComponent<BoxCollider>());
            Vector3 blockPosition = block.transform.position;
            Vector3 initial = robot.BasePose.position;
            Assert.Less(Vector3.Distance(initial, blockPosition), 0.6f);

            // The block publishes its geometry on its own timer.
            bool SentBlock(sbyte operation) => bus.PublishedOn("/collision_object").OfType<CollisionObjectMsg>()
                .Any(message => message.id == "workbench_block" && message.operation == operation);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!SentBlock(CollisionObjectMsg.ADD) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(SentBlock(CollisionObjectMsg.ADD), "MoveIt received the initial workbench block.");

            // Aim at a tag in view, as the user does when accepting the preview.
            var viewer = Camera.main.transform;
            Vector3 proposed = viewer.position + viewer.forward * 1.2f;
            Assert.IsTrue(plugin.Tracker.InjectObservation(0, MarkerMathTests.FlatTag(proposed)));
            yield return null;
            Assert.IsTrue(plugin.SessionFor(robot).ShowsProposal);
            Assert.AreEqual(initial, robot.BasePose.position);
            var ui = (TierUiRig)host.Context.Ui;
            Canvas canvas = ui.Widgets.Single(w => w.Id == $"ar.place.{robot.Id}").GameObject.GetComponent<Canvas>();
            Assert.Less(Vector3.Distance(canvas.transform.position, proposed), 0.4f);
            Assert.Greater(canvas.transform.position.y, proposed.y);
            Camera camera = canvas.worldCamera;
            Assert.IsNotNull(camera);
            Assert.Greater(Vector3.Dot(canvas.transform.forward, (canvas.transform.position - camera.transform.position).normalized), 0.99f);

            camera.transform.LookAt(canvas.transform.position);
            plugin.Refresh();
            yield return null; // Let the new canvas submit its graphics before the pointer press.
            Canvas.ForceUpdateCanvases();
            var pointer = new PointerEventData(EventSystem.current) { position = camera.WorldToScreenPoint(canvas.transform.position) };
            var hits = new List<RaycastResult>();
            canvas.GetComponent<GraphicRaycaster>().Raycast(pointer, hits);
            Assert.IsNotEmpty(hits, $"The preview button must be reachable at viewport {camera.WorldToViewportPoint(canvas.transform.position)}, " +
                $"active={canvas.gameObject.activeInHierarchy}, graphic depth={canvas.GetComponentInChildren<Image>().depth}.");
            ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.IsFalse(canvas.gameObject.activeSelf);
            Assert.Less(Vector3.Distance(proposed, robot.BasePose.position), 0.001f);
            Assert.AreEqual(blockPosition, block.transform.position);
            Assert.IsTrue(SentBlock(CollisionObjectMsg.MOVE), "MoveIt received the block in the new base frame.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            if (temporaryScene != null) AssetDatabase.DeleteAsset(temporaryScene);
            temporaryScene = null;
            if (connection != null) Object.DestroyImmediate(connection);
            RosBus.Reset();
            RobotRegistry.Default.SetBus(null);
            LogAssert.ignoreFailingMessages = false;
        }
    }
}
#endif
