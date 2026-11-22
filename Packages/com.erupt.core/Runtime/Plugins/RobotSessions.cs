using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Robot;

namespace Erupt.Plugins
{
    /// <summary>
    /// One plugin, any number of robots: keeps one <typeparamref name="T"/> per robot in the
    /// registry. A session is created for every robot present when the plugin registers and
    /// for every robot that joins later, and disposed when its robot leaves or the plugin
    /// unregisters. The plugin itself stays a single instance with a single id and a single tab.
    /// </summary>
    /// <typeparam name="T">
    /// What the plugin keeps per robot: its ROS clients on <see cref="RobotInstance.Ros"/>,
    /// captured state, players. Disposed through <see cref="IDisposable"/> unless a dispose
    /// callback is given.
    /// </typeparam>
    public sealed class RobotSessions<T> : IDisposable, IEnumerable<T> where T : class
    {
        private readonly IRobotRegistry robots;
        private readonly Func<RobotInstance, T> create;
        private readonly Action<T> dispose;
        private readonly List<RobotInstance> order = new();
        private readonly Dictionary<RobotInstance, T> sessions = new();
        private bool disposed;

        /// <summary>A session was created for a robot (at construction or when the robot joined).</summary>
        public event Action<RobotInstance, T> Created;
        /// <summary>A robot's session is about to be disposed because the robot left.</summary>
        public event Action<RobotInstance, T> Removing;

        /// <param name="robots">The context's registry; null means no robots, ever.</param>
        /// <param name="create">Builds a robot's session; null means the plugin cannot serve that robot.</param>
        /// <param name="dispose">Tears a session down; default is <see cref="IDisposable.Dispose"/> when the session has one.</param>
        public RobotSessions(IRobotRegistry robots, Func<RobotInstance, T> create, Action<T> dispose = null)
        {
            this.robots = robots;
            this.create = create ?? throw new ArgumentNullException(nameof(create));
            this.dispose = dispose ?? (session => (session as IDisposable)?.Dispose());
            if (robots == null) return;

            foreach (RobotInstance robot in new List<RobotInstance>(robots.All)) Add(robot);
            robots.Added += Add;
            robots.Removed += Remove;
        }

        /// <summary>Sessions that exist, in the order their robots registered.</summary>
        public int Count => sessions.Count;

        /// <summary>The robots that have a session, in registration order.</summary>
        public IReadOnlyList<RobotInstance> Robots => order;

        /// <summary>The session of the robot the user is working with; null when there is none.</summary>
        public T Active => For(robots?.Active);

        /// <summary>A robot's session; null for a robot the plugin does not serve.</summary>
        public T For(RobotInstance robot) =>
            !ReferenceEquals(robot, null) && sessions.TryGetValue(robot, out T session) ? session : null;

        /// <summary>The session of the robot a selection belongs to, falling back to the active robot.</summary>
        public T For(ISelectable target) => For(RobotOf(target));

        /// <summary>The session of the robot an object belongs to, falling back to the active robot.</summary>
        public T For(GameObject go) => For(RobotOf(go));

        /// <summary>The robot a selection belongs to (<see cref="IRobotRegistry.OwnerOf"/>), or the active one.</summary>
        public RobotInstance RobotOf(ISelectable target) => RobotOf(target?.GameObject);

        /// <summary>The robot an object belongs to (<see cref="IRobotRegistry.OwnerOf"/>), or the active one.</summary>
        public RobotInstance RobotOf(GameObject go)
        {
            if (robots == null) return null;
            RobotInstance owner = go != null ? robots.OwnerOf(go) : null;
            return owner != null ? owner : robots.Active;
        }

        public bool TryGet(RobotInstance robot, out T session)
        {
            session = For(robot);
            return session != null;
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (RobotInstance robot in order) yield return sessions[robot];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (robots != null)
            {
                robots.Added -= Add;
                robots.Removed -= Remove;
            }
            for (int i = order.Count - 1; i >= 0; i--) Teardown(sessions[order[i]]);
            order.Clear();
            sessions.Clear();
        }

        private void Add(RobotInstance robot)
        {
            if (disposed || ReferenceEquals(robot, null) || sessions.ContainsKey(robot)) return;
            T session;
            try { session = create(robot); }
            catch (Exception e)
            {
                Debug.LogException(e);
                return;
            }
            if (session == null) return;
            sessions[robot] = session;
            order.Add(robot);
            Created?.Invoke(robot, session);
        }

        private void Remove(RobotInstance robot)
        {
            if (ReferenceEquals(robot, null) || !sessions.TryGetValue(robot, out T session)) return;
            Removing?.Invoke(robot, session);
            sessions.Remove(robot);
            order.Remove(robot);
            Teardown(session);
        }

        private void Teardown(T session)
        {
            try { dispose(session); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
