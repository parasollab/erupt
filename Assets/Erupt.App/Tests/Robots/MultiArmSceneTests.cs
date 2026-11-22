#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Ros.Tests;

namespace Erupt.App.Robots.Tests
{
    /// <summary>
    /// The reference app's multi-arm sample scene: three FR3 robots with their own ids and ROS
    /// namespaces, each with its own planning frame (the anchor its prefab carries), so each
    /// robot's ROS stack has it at the origin of its own world. Skipped in a project that does
    /// not have the scene.
    /// </summary>
    public class MultiArmSceneTests
    {
        private const string ScenePath = "Assets/Scenes/MultiArmFR3.unity";

        // The ROS side is started without poses: ARMS="left=fr3 right=fr3 third=fr3".
        private static readonly string[] Ids = { "left", "right", "third" };

        private Scene scene;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            scene = default;
            RosBus.Reset();
            // The scene's plugin host gave the default registry the fake bus; later tests start without one.
            RobotRegistry.Default.SetBus(null);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator TheScene_LoadsWithThreeRobots_EachInItsOwnNamespace_WithItsOwnPlanningFrame()
        {
            Assume.That(File.Exists(ScenePath), "The reference app's multi-arm scene is not in this project.");

            var bus = new FakeRosBus();
            RosBus.Override(bus);
            // The scene carries the XR rig, which reports missing devices when run headless.
            LogAssert.ignoreFailingMessages = true;
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
                scene = SceneManager.GetSceneByPath(ScenePath);
                Assert.IsTrue(scene.IsValid() && scene.isLoaded);
                yield return null;
                yield return null;   // the plugin host has started and the plugins have their sessions

                List<RobotInstance> robots = RobotRegistry.Default.All.Where(r => r.gameObject.scene == scene).ToList();
                CollectionAssert.AreEquivalent(Ids, robots.Select(r => r.Id), "Three robots with distinct ids.");
                CollectionAssert.AreEquivalent(Ids, robots.Select(r => r.Namespace), "Each in the namespace of its id.");
                Assert.AreEqual(3, robots.Select(r => r.Profile).Count(p => p != null));
                Assert.AreEqual(3, robots.Select(r => r.PlanningFrame).Distinct().Count(), "Each robot has its own planning frame.");
                Assert.AreEqual(3, robots.Select(r => r.transform.position).Distinct().Count(), "The robots stand in three places.");

                foreach (RobotInstance robot in robots)
                {
                    Transform frame = robot.PlanningFrame;
                    Assert.IsNotNull(frame, robot.Id);
                    Assert.IsTrue(frame.IsChildOf(robot.transform), $"{robot.Id}: the planning frame is the robot's own anchor, so it moves with the robot.");

                    // In its own frame the robot's base is at the origin, looking along ROS x:
                    // what a ROS stack started without a pose believes.
                    Assert.IsTrue(robot.Model.TryFindLinkTransform(robot.Profile.baseLink, out Transform baseLink), robot.Id);
                    Vector3 local = frame.InverseTransformPoint(baseLink.position);
                    Assert.AreEqual(0f, local.magnitude, 1e-4f, $"{robot.Id}: base at the origin of its planning frame");
                    // The imported robot looks along its local z; that direction in ROS x/y gives the yaw.
                    Vector3 forward = frame.InverseTransformDirection(baseLink.forward);
                    float yaw = Mathf.Atan2(forward.z, forward.x);
                    Assert.AreEqual(0f, yaw * Mathf.Rad2Deg, 0.01f, $"{robot.Id} yaw");

                    Assert.IsNotNull(robot.Ros, "The plugin host gave the registry the bus.");
                    Assert.IsNotNull(robot.Interaction);
                }

                // The scene's plugins serve all three: each robot's traffic is in its own namespace.
                foreach (string id in Ids)
                {
                    CollectionAssert.Contains(bus.Subscriptions, $"/{id}/joint_states");
                    CollectionAssert.Contains(bus.RegisteredPublishers, $"/{id}/collision_object");
                    CollectionAssert.Contains(bus.Subscriptions, $"/{id}/collision_objects_ros");
                    CollectionAssert.Contains(bus.Subscriptions, $"/{id}/pick_place/statistics");
                }
                CollectionAssert.DoesNotContain(bus.RegisteredPublishers, "/collision_object", "Nothing is sent to a planning scene without a robot.");
            }
        }
    }
}
#endif
