using System.Collections.Generic;
using UnityEngine;
using Erupt.Ros;

namespace Erupt.Robot.Tests
{
    /// <summary>
    /// Robots for tests: a <see cref="RobotRegistry"/> of their own (never the scene default)
    /// holding <see cref="RobotInstance"/> objects whose model is a fake, each with an id, a ROS
    /// namespace and optionally a profile. Destroy them with <see cref="DestroyAll"/> in teardown.
    /// </summary>
    public static class FakeRobots
    {
        /// <summary>A registry on <paramref name="bus"/> with no robot yet.</summary>
        public static RobotRegistry Registry(IRosBus bus)
        {
            var registry = new RobotRegistry();
            registry.SetBus(bus);
            return registry;
        }

        /// <summary>A registry on <paramref name="bus"/> holding one robot, which is therefore the active one.</summary>
        public static RobotRegistry Single(IRosBus bus, IRobotModel model, string rosNamespace = "",
            RobotProfile profile = null, string id = "robot")
        {
            RobotRegistry registry = Registry(bus);
            Add(registry, id, rosNamespace, model, profile);
            return registry;
        }

        /// <summary>Ids and ROS namespaces of the robots <see cref="Three"/> creates. Three, so that nothing can pass by assuming two.</summary>
        public static readonly string[] ThreeIds = { "left", "right", "third" };

        /// <summary>
        /// A registry on <paramref name="bus"/> holding three robots, <c>left</c>, <c>right</c>
        /// and <c>third</c>, each in the namespace of its id and with its own fake model (two
        /// arm joints named after the robot and a gripper joint, unless
        /// <paramref name="model"/> makes another). <c>left</c> registers first and is active.
        /// </summary>
        public static RobotRegistry Three(IRosBus bus, System.Func<string, IRobotModel> model = null,
            System.Func<string, RobotProfile> profile = null)
        {
            RobotRegistry registry = Registry(bus);
            foreach (string id in ThreeIds)
            {
                IRobotModel robotModel = model != null
                    ? model(id)
                    : new FakeRobotModel(id + "_joint1", id + "_joint2").WithGripper(id + "_finger");
                Add(registry, id, id, robotModel, profile != null ? profile(id) : null);
            }
            return registry;
        }

        /// <summary>
        /// A selectable end effector under a robot's object, so a selection can be owned by
        /// that robot (<see cref="IRobotRegistry.OwnerOf"/>). Also becomes the fake model's end effector.
        /// </summary>
        public static Erupt.Interaction.SelectableMarker EndEffectorOf(RobotInstance robot)
        {
            Transform existing = robot.transform.Find("end effector");
            GameObject go = existing != null ? existing.gameObject : new GameObject("end effector");
            go.transform.SetParent(robot.transform, false);
            if (robot.Model is FakeRobotModel fake && fake.EndEffector == null) fake.EndEffector = go.transform;
            return Erupt.Interaction.SelectableMarker.Ensure(go, Erupt.Interaction.SelectionKind.EndEffector, "End Effector");
        }

        /// <summary>Create a robot on its own GameObject (named after the id) and register it.</summary>
        public static RobotInstance Add(RobotRegistry registry, string id, string rosNamespace,
            IRobotModel model, RobotProfile profile = null)
        {
            var go = new GameObject(id);
            go.SetActive(false);
            var robot = go.AddComponent<RobotInstance>();
            robot.Configure(id, rosNamespace, profile, model);
            robot.UseRegistry(registry);
            go.SetActive(true);
            return robot;
        }

        /// <summary>
        /// Make an existing object a robot and register it. The object is switched off while it
        /// is set up, so call this before anything on it has to stay alive (physics bodies).
        /// </summary>
        public static RobotInstance Attach(RobotRegistry registry, GameObject go, string id, string rosNamespace,
            RobotProfile profile = null, IRobotModel model = null)
        {
            bool wasActive = go.activeSelf;
            go.SetActive(false);
            var robot = go.AddComponent<RobotInstance>();
            robot.Configure(id, rosNamespace, profile, model);
            robot.UseRegistry(registry);
            go.SetActive(wasActive);
            return robot;
        }

        /// <summary>Destroy every robot object of a registry (which unregisters them).</summary>
        public static void DestroyAll(IRobotRegistry registry)
        {
            if (registry == null) return;
            foreach (RobotInstance robot in new List<RobotInstance>(registry.All))
                if (robot != null) Object.DestroyImmediate(robot.gameObject);
        }

        /// <summary>
        /// A profile for a robot whose ROS side names joints and links with another prefix
        /// (<c>panda_joint1</c> on the wire, <c>fr3_joint1</c> in Unity).
        /// </summary>
        public static RobotProfile ProfileWithPrefixes(string localPrefix, string rosPrefix)
        {
            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.names = new RobotNameMap { localPrefix = localPrefix, rosPrefix = rosPrefix };
            return profile;
        }
    }
}
