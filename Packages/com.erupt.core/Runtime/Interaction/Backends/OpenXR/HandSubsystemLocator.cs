using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace Erupt.Interaction.Backends
{
    /// <summary>Finds the running XR Hands subsystem. It may start after the scene does.</summary>
    internal static class HandSubsystemLocator
    {
        private static readonly List<XRHandSubsystem> subsystems = new();

        public static bool TryGet(ref XRHandSubsystem subsystem)
        {
            if (subsystem != null && subsystem.running) return true;

            SubsystemManager.GetSubsystems(subsystems);
            subsystem = null;
            for (int i = 0; i < subsystems.Count; i++)
            {
                if (!subsystems[i].running) continue;
                subsystem = subsystems[i];
                break;
            }

            return subsystem != null;
        }
    }
}
