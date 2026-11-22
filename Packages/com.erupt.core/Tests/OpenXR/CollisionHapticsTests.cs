using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends.Tests
{
    /// <summary>
    /// Proximity haptics read what to measure from the interaction router: every robot's tip is
    /// a probe point and every robot's own colliders are not obstacles, without assigning a
    /// robot to the component.
    /// </summary>
    public class CollisionHapticsTests
    {
        private sealed class Robots : IProximitySource
        {
            public readonly List<Transform> Tips = new();
            public readonly List<Collider> Own = new();
            public void GetProbePoints(List<Vector3> points)
            {
                foreach (Transform tip in Tips) points.Add(tip.position);
            }
            public bool IsOwnCollider(Collider collider) => Own.Contains(collider);
        }

        private GameObject routerGo, hapticsGo;
        private InteractionRouter router;
        private CollisionHaptics haptics;
        private readonly List<GameObject> objects = new();

        [SetUp]
        public void SetUp()
        {
            // What the person at this machine set on the Settings tab is not part of the test.
            UserSettings.Store = new MemoryUserSettingsStore();
            LogAssert.Expect(LogType.Error, new Regex("no interaction sources found"));
            routerGo = new GameObject("router");
            router = routerGo.AddComponent<InteractionRouter>();
            hapticsGo = new GameObject("haptics");
            haptics = hapticsGo.AddComponent<CollisionHaptics>();
            haptics.SetRouter(router);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in objects) Object.DestroyImmediate(go);
            objects.Clear();
            Object.DestroyImmediate(hapticsGo);
            Object.DestroyImmediate(routerGo);
            UserSettings.Store = null;
        }

        private GameObject Box(string name, Vector3 position, float size = 0.1f)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * size;
            objects.Add(go);
            return go;
        }

        [Test]
        public void WithoutAnyRobot_NothingIsNear()
        {
            Box("obstacle", Vector3.zero);
            Physics.SyncTransforms();
            Assert.AreEqual(float.MaxValue, haptics.FindClosestObstacleDistance());
        }

        [Test]
        public void EveryRobotsTip_IsMeasured_AndEveryRobotsOwnBody_IsNotAnObstacle()
        {
            // Three tips, far apart; an obstacle 15 cm from the third one's.
            var robots = new Robots();
            for (int i = 0; i < 3; i++)
            {
                GameObject tip = Box($"tip {i}", new Vector3(10f * i, 0f, 0f), 0.02f);
                robots.Tips.Add(tip.transform);
                robots.Own.Add(tip.GetComponent<Collider>());
                // A link of the robot right next to its tip.
                robots.Own.Add(Box($"link {i}", new Vector3(10f * i, 0.05f, 0f), 0.05f).GetComponent<Collider>());
            }
            router.AddProximitySource(robots);
            Physics.SyncTransforms();
            Assert.AreEqual(float.MaxValue, haptics.FindClosestObstacleDistance(), "A robot's own body is not an obstacle.");

            Box("obstacle", new Vector3(20f, 0f, 0.2f));   // its near face is 15 cm from the third tip
            Physics.SyncTransforms();
            Assert.AreEqual(0.15f, haptics.FindClosestObstacleDistance(), 1e-3f, "The third robot's tip counts like the first.");

            // One robot's link next to another robot's tip is still not an obstacle.
            robots.Own.Add(Box("other robot's link", new Vector3(0f, 0f, 0.1f)).GetComponent<Collider>());
            Physics.SyncTransforms();
            Assert.AreEqual(0.15f, haptics.FindClosestObstacleDistance(), 1e-3f);

            router.RemoveProximitySource(robots);
            Assert.AreEqual(float.MaxValue, haptics.FindClosestObstacleDistance());
        }
    }
}
