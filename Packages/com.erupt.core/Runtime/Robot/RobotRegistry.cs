using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Ros;

namespace Erupt.Robot
{
    /// <summary>The robots in the scene, as plugins see them.</summary>
    public interface IRobotRegistry
    {
        /// <summary>Every registered robot, in registration order.</summary>
        IReadOnlyList<RobotInstance> All { get; }

        /// <summary>The robot the user is working with: the first registered one until <see cref="SetActive"/> picks another. Null when there is none.</summary>
        RobotInstance Active { get; }

        bool TryGet(string id, out RobotInstance robot);

        /// <summary>
        /// The robot an object belongs to: the <see cref="RobotInstance"/> above it in the
        /// hierarchy, or the one named by a <see cref="RobotOwned"/> marker on it or above it
        /// (handles, ghosts and other objects that are not parented under their robot). Null
        /// when the object belongs to no robot.
        /// </summary>
        RobotInstance OwnerOf(GameObject go);

        /// <summary>Make a registered robot the active one.</summary>
        void SetActive(RobotInstance robot);

        event Action<RobotInstance> Added;
        event Action<RobotInstance> Removed;
        /// <summary>The active robot changed; the argument is the new one (null when the last robot left).</summary>
        event Action<RobotInstance> ActiveChanged;
    }

    /// <summary>
    /// The robot registry. Robots register themselves when enabled; the plugin host gives the
    /// registry the ROS bus, from which every robot derives its own namespaced bus.
    /// </summary>
    public sealed class RobotRegistry : IRobotRegistry
    {
        private static RobotRegistry defaultRegistry;

        /// <summary>The registry scene robots join unless they were given another one.</summary>
        public static RobotRegistry Default => defaultRegistry ??= new RobotRegistry();

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => defaultRegistry = null;

        private readonly List<RobotInstance> robots = new();

        public IReadOnlyList<RobotInstance> All => robots;
        public RobotInstance Active { get; private set; }

        /// <summary>The bus every robot's <see cref="RobotInstance.Ros"/> is scoped from; null until <see cref="SetBus"/>.</summary>
        public IRosBus Bus { get; private set; }

        /// <summary>
        /// The Unity transform that stands for the ROS planning frame of a robot that names
        /// none of its own (the scene's world origin).
        /// </summary>
        public Transform DefaultPlanningFrame { get; set; }

        public event Action<RobotInstance> Added;
        public event Action<RobotInstance> Removed;
        public event Action<RobotInstance> ActiveChanged;

        /// <summary>Hand over the ROS transport. Robots resolve their names on it from now on.</summary>
        public void SetBus(IRosBus bus) => Bus = bus;

        /// <summary>
        /// Add a robot. Registering the same robot again does nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">Another robot already has this id; the message names both objects.</exception>
        public void Register(RobotInstance robot)
        {
            if (robot == null) throw new ArgumentNullException(nameof(robot));
            if (robots.Contains(robot)) return;

            string id = robot.Id;
            if (TryGet(id, out RobotInstance existing))
                throw new InvalidOperationException(
                    $"Robot id '{id}' is used by both '{PathOf(existing)}' and '{PathOf(robot)}'. Give each RobotInstance its own id.");

            robots.Add(robot);
            Added?.Invoke(robot);
            if (Active == null) ChangeActive(robot);
        }

        public void Unregister(RobotInstance robot)
        {
            if (ReferenceEquals(robot, null) || !robots.Remove(robot)) return;
            Removed?.Invoke(robot);
            if (ReferenceEquals(Active, robot)) ChangeActive(robots.Count > 0 ? robots[0] : null);
        }

        public bool TryGet(string id, out RobotInstance robot)
        {
            foreach (RobotInstance candidate in robots)
                if (candidate.Id == id) { robot = candidate; return true; }
            robot = null;
            return false;
        }

        public RobotInstance OwnerOf(GameObject go)
        {
            if (go == null) return null;
            RobotInstance robot = go.GetComponentInParent<RobotInstance>(true);
            if (robot != null) return robot;
            RobotOwned marker = go.GetComponentInParent<RobotOwned>(true);
            return marker != null ? marker.Owner : null;
        }

        public void SetActive(RobotInstance robot)
        {
            if (robot == null) throw new ArgumentNullException(nameof(robot));
            if (!robots.Contains(robot))
                throw new ArgumentException($"Robot '{robot.Id}' is not registered.", nameof(robot));
            if (!ReferenceEquals(Active, robot)) ChangeActive(robot);
        }

        private void ChangeActive(RobotInstance robot)
        {
            Active = robot;
            ActiveChanged?.Invoke(robot);
        }

        private static string PathOf(RobotInstance robot)
        {
            if (robot == null) return "(destroyed)";
            Transform t = robot.transform;
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
