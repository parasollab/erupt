using System;
using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Environment
{
    /// <summary>
    /// Single writer for the environment mirror: every object the user can see and act on,
    /// keyed by id, plus the frame they are expressed in.
    /// </summary>
    /// <remarks>
    /// Core owns the mirror; planners keep it in sync through <see cref="IEnvironmentSync"/>
    /// components that subscribe to the events here. The registry never destroys or spawns
    /// GameObjects itself: <c>Register</c> / <c>Unregister</c> only record membership, so an
    /// <c>Unregister(id, RemovalOrigin.Remote)</c> lets a sync suppress its removal echo
    /// before the caller destroys the object.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class EnvironmentRegistry : MonoBehaviour
    {
        [Tooltip("Frame every synced pose is expressed in. If null, this transform.")]
        [SerializeField] private GameObject worldOrigin;

        private readonly Dictionary<string, EnvironmentObject> objects = new();

        public event Action<EnvironmentObject> Added;
        public event Action<EnvironmentObject, RemovalOrigin> Removed;
        public event Action<EnvironmentObject, Transform> Attached;
        public event Action<EnvironmentObject> Detached;

        public Transform WorldOrigin => worldOrigin != null ? worldOrigin.transform : transform;

        /// <summary>The origin GameObject as configured, for components that store one.</summary>
        public GameObject WorldOriginObject => worldOrigin;

        public int Count => objects.Count;
        public IEnumerable<EnvironmentObject> All => objects.Values;

        public void SetWorldOrigin(GameObject origin) => worldOrigin = origin;

        /// <summary>
        /// Adds the object to the mirror and fires <see cref="Added"/>. Re-registering an id
        /// that already maps to the same object is a no-op; a different object replaces it.
        /// </summary>
        public void Register(EnvironmentObject obj)
        {
            if (obj == null || string.IsNullOrEmpty(obj.Id))
            {
                Debug.LogWarning("[EnvironmentRegistry] Register ignored: null object or empty id.");
                return;
            }

            if (objects.TryGetValue(obj.Id, out var existing) && existing == obj) return;

            objects[obj.Id] = obj;
            Added?.Invoke(obj);
        }

        /// <summary>
        /// Removes the id from the mirror and fires <see cref="Removed"/> with the origin.
        /// Returns false if the id was unknown. Does not destroy the GameObject.
        /// </summary>
        public bool Unregister(string id, RemovalOrigin origin)
        {
            if (string.IsNullOrEmpty(id) || !objects.TryGetValue(id, out var obj)) return false;

            objects.Remove(id);
            Removed?.Invoke(obj, origin);
            return true;
        }

        public bool TryGet(string id, out GameObject go)
        {
            go = null;
            if (!TryGetObject(id, out var obj)) return false;
            go = obj.gameObject;
            return true;
        }

        public bool TryGetObject(string id, out EnvironmentObject obj)
        {
            obj = null;
            if (string.IsNullOrEmpty(id) || !objects.TryGetValue(id, out obj)) return false;

            if (obj == null)
            {
                // Destroyed behind our back; drop the stale entry.
                objects.Remove(id);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Records that the object now rides on a robot link and fires <see cref="Attached"/>.
        /// The caller performs the reparent; the registry only tracks the state.
        /// </summary>
        public void Attach(string id, Transform link)
        {
            if (!TryGetObject(id, out var obj)) return;
            obj.AttachedTo = link;
            Attached?.Invoke(obj, link);
        }

        public void Detach(string id)
        {
            if (!TryGetObject(id, out var obj)) return;
            obj.AttachedTo = null;
            Detached?.Invoke(obj);
        }

        /// <summary>
        /// Adopts an object that already carries an <see cref="EnvironmentObject"/> or, if it
        /// does not, gives it one initialised from the arguments, then registers it.
        /// </summary>
        public EnvironmentObject Adopt(GameObject go, string id, EnvironmentOwner owner,
                                       PrimitiveType? primitive, bool isMesh)
        {
            if (go == null) return null;
            var env = go.GetComponent<EnvironmentObject>();
            if (env == null)
            {
                env = go.AddComponent<EnvironmentObject>();
                env.Initialize(id, owner, primitive, isMesh);
            }
            Register(env);
            return env;
        }
    }
}
