using UnityEngine;
using Erupt.Obstacles;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// Turns a GameObject into a selectable, grabbable MoveIt collision object: the core grab
    /// stack (<see cref="GrabbableObjectSetup"/>) plus a <see cref="CollisionObjectPublisher"/>.
    /// One place for every object the plugin builds, so they all handle and publish the same way.
    /// </summary>
    public static class GrabbableCollisionObjectFactory
    {
        /// <summary>
        /// Adds the grab / publish stack to <paramref name="go"/>, which already has its renderer
        /// (and, for a primitive, its collider; a mesh gets a mesh collider).
        /// </summary>
        /// <param name="isMesh">Publish the object as a mesh; meshes are movable but not scalable.</param>
        /// <param name="markAlreadyPublished">true for objects mirrored from ROS, so the publisher
        /// does not re-ADD what the planning scene already holds.</param>
        public static CollisionObjectPublisher Configure(GameObject go, string objectId, GameObject worldOrigin,
            bool isMesh = false, bool markAlreadyPublished = false)
        {
            GrabbableObjectSetup.Configure(go, scalable: !isMesh);

            CollisionObjectPublisher publisher = go.GetComponent<CollisionObjectPublisher>();
            if (publisher == null) publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = isMesh;
            publisher.objectId = objectId;
            publisher.worldOrigin = worldOrigin;
            publisher.hasBeenPublished = markAlreadyPublished;
            return publisher;
        }
    }
}
