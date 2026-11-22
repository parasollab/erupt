using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Something whose closeness to obstacles the user should feel (the robots' tips). Feature
    /// code registers one with the <see cref="InteractionRouter"/>; a backend that can pulse
    /// reads them there, so the backend never names a robot and the robots never name a device.
    /// </summary>
    public interface IProximitySource
    {
        /// <summary>Add the world points to measure from (one per robot tip).</summary>
        void GetProbePoints(List<Vector3> points);

        /// <summary>True for a collider that belongs to the source itself (any robot's own body) and so is not an obstacle.</summary>
        bool IsOwnCollider(Collider collider);
    }
}
