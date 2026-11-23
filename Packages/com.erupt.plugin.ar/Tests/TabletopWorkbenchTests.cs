#if UNITY_EDITOR && ERUPT_PLUGIN_MOVEIT
using System.Collections;
using System.IO;
using System.Linq;
using Erupt.Environment;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ros.Tests;
using NUnit.Framework;
using RosMessageTypes.Moveit;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Erupt.Plugins.Ar.Tests
{
    public class TabletopWorkbenchTests
    {
        private const string ScenePath = "Assets/Scenes/ARWorkbenchFR3.unity";
        private Scene scene;
        private GameObject connection;

        [UnityTest]
        public IEnumerator WorkbenchRegistersTheBlockAndPlacesTheArmOnlyOnConfirmation()
        {
            Assume.That(File.Exists(ScenePath), "The reference app is absent.");
            var bus = new FakeRosBus();
            RosBus.Override(bus);
            connection = IdleRosConnection.Create();
            // The Meta and XR components report unavailable hardware in a headless editor.
            LogAssert.ignoreFailingMessages = true;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            yield return null;
            yield return null;

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

            Vector3 proposed = initial + Vector3.right * 0.3f;
            Assert.IsTrue(plugin.Tracker.InjectObservation(0, MarkerMathTests.FlatTag(proposed)));
            yield return null;
            Assert.IsTrue(plugin.SessionFor(robot).ShowsProposal);
            Assert.AreEqual(initial, robot.BasePose.position);
            var placement = plugin.PlaceAtMarker(robot);
            Assert.IsFalse(placement.IsRefused, placement.Reason);
            Assert.Less(Vector3.Distance(proposed, robot.BasePose.position), 0.001f);
            Assert.AreEqual(blockPosition, block.transform.position);
            Assert.IsTrue(bus.PublishedOn("/collision_object").OfType<CollisionObjectMsg>()
                .Any(message => message.id == "workbench_block" && message.operation == CollisionObjectMsg.ADD));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            if (connection != null) Object.DestroyImmediate(connection);
            RosBus.Reset();
            RobotRegistry.Default.SetBus(null);
            LogAssert.ignoreFailingMessages = false;
        }
    }
}
#endif
