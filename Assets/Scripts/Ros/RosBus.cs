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

        public static IRosBus Instance
        {
            get
            {
                if (instance == null) instance = new LiveRosBus();
                return instance;
            }
        }

        /// <summary>
        /// Drop any bus left over from a previous play session.
        /// </summary>
        /// <remarks>
        /// Static state survives entering play mode when domain reload is disabled, so
        /// without this a stale LiveRosBus - or a FakeRosBus left behind by a test run -
        /// would still be serving Instance on the next Play.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => instance = null;

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
