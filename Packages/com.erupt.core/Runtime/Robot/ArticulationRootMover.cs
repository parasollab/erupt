using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// Moves the root of an articulated robot. The links of an imported robot are a chain of
    /// <see cref="ArticulationBody"/> components, and PhysX ignores a Transform write on an
    /// articulation root: the pose shows for one frame and the next physics step puts the old
    /// one back. <see cref="MoveRoot"/> sets the Transform first, so the children that are not
    /// part of the articulation (the planning-frame anchor, handles) are at the target, and
    /// then teleports every articulation root under the object to where its Transform now is.
    /// </summary>
    public static class ArticulationRootMover
    {
        /// <summary>Put <paramref name="robot"/> at a world pose. Joint positions are kept; velocities are zeroed.</summary>
        public static void MoveRoot(GameObject robot, Vector3 position, Quaternion rotation)
        {
            if (robot == null) return;
            robot.transform.SetPositionAndRotation(position, rotation);

            foreach (ArticulationBody body in robot.GetComponentsInChildren<ArticulationBody>(true))
            {
                // isRoot is only meaningful for a body that is in the physics scene.
                if (!body.isActiveAndEnabled || !body.isRoot) continue;
                body.TeleportRoot(body.transform.position, body.transform.rotation);
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            // Colliders and whoever reads link transforms this frame see the new pose.
            Physics.SyncTransforms();
        }
    }
}
