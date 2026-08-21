using UnityEngine;

namespace Erupt.Ros
{
    /// <summary>
    /// Provider for the ROS transport. Defaults to <see cref="LiveRosBus"/>, so every
    /// existing scene behaves exactly as before; tests substitute a fake.
    /// </summary>
    public static class RosBus
    {
        private static IRosBus instance;

        public static IRosBus Instance => instance ??= new LiveRosBus();

        /// <summary>True when a test has substituted the transport.</summary>
        public static bool IsOverridden => instance != null && !(instance is LiveRosBus);

        /// <summary>Substitute the transport. Tests only — call <see cref="Reset"/> in teardown.</summary>
        public static void Override(IRosBus bus)
        {
            if (Application.isPlaying == false && bus != null)
                Debug.LogWarning("RosBus.Override called outside play mode.");
            instance = bus;
        }

        /// <summary>Drop any substitution and fall back to the live connection.</summary>
        public static void Reset() => instance = null;
    }
}
