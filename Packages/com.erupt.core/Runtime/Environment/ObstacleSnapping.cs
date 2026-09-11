using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace Erupt.Obstacles
{
    /// <summary>
    /// Drops an obstacle onto the nearest surface below it.
    /// </summary>
    /// <remarks>
    /// Lifted from WristMenuController.SnapSelectedToSurface so the verb has an
    /// implementation independent of the menu being replaced. Behaviour is unchanged:
    /// raycast down from the collider centre with the collider disabled so it does not
    /// self-hit, take the nearest "SnapSurface" hit, align +Y to the surface normal with
    /// the minimal rotation, and seat the object on it.
    /// </remarks>
    public static class ObstacleSnapping
    {
        public const string SurfaceTag = "SnapSurface";

        /// <summary>Returns false when there is no tagged surface beneath the object.</summary>
        public static bool TrySnapToSurface(GameObject obstacle)
        {
            if (obstacle == null) return false;

            Collider col = obstacle.GetComponent<Collider>();
            Vector3 origin = col != null ? col.bounds.center : obstacle.transform.position;

            if (col != null) col.enabled = false;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, Mathf.Infinity);
            if (col != null) col.enabled = true;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                if (!hit.collider.CompareTag(SurfaceTag)) continue;

                Quaternion alignment = Quaternion.FromToRotation(obstacle.transform.up, hit.normal);
                obstacle.transform.rotation = alignment * obstacle.transform.rotation;

                float halfHeight = LocalHalfHeight(col, obstacle.transform);
                obstacle.transform.position = hit.point + hit.normal * halfHeight;

                // The grab pipeline caches a rotation; without this the snap reverts on
                // the next grab.
                var lockPose = obstacle.GetComponent<XRGrabTransformerLockPose>();
                if (lockPose != null) lockPose.SyncInitialRotation();

                return true;
            }

            return false;
        }

        private static float LocalHalfHeight(Collider col, Transform t)
        {
            if (col == null) return t.lossyScale.y * 0.5f;

            float scaleY = Mathf.Abs(t.lossyScale.y);
            if (col is BoxCollider box) return box.size.y * 0.5f * scaleY;
            if (col is SphereCollider sphere) return sphere.radius * scaleY;
            if (col is CapsuleCollider capsule) return capsule.height * 0.5f * scaleY;

            return col.bounds.extents.y;
        }
    }
}
