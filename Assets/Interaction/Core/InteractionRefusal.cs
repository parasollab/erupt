using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Why an interaction could not proceed. Guidelines Part 3, "Refusals carry reasons":
    /// silent no-ops are the most common source of novice confusion, so every refusal
    /// carries a user-facing string and the world point to surface it at.
    /// </summary>
    public readonly struct InteractionRefusal
    {
        public readonly bool IsRefused;
        public readonly string Reason;
        public readonly Vector3 WorldPoint;

        private InteractionRefusal(bool isRefused, string reason, Vector3 worldPoint)
        {
            IsRefused = isRefused;
            Reason = reason;
            WorldPoint = worldPoint;
        }

        public static readonly InteractionRefusal None = new InteractionRefusal(false, null, Vector3.zero);

        public static InteractionRefusal Refuse(string reason, Vector3 worldPoint) =>
            new InteractionRefusal(true, reason, worldPoint);
    }
}
