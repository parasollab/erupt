using Erupt.Interaction;
using Erupt.Ros;

namespace Erupt.Plugins
{
    /// <summary>
    /// The ROS address the user set in the app, kept between sessions
    /// (<see cref="UserSettings"/>) and put back on the bus before anything connects.
    /// </summary>
    /// <remarks>
    /// The address a build carries (Robotics > ROS Settings) is the default; what the user
    /// enters on the Settings tab replaces it on that device. A saved address is remembered
    /// together with the built-in one it replaced and applies only while the build still
    /// carries that one: after the ROS settings are changed, or a build with another address
    /// is installed, the new built-in address is used, and not an address set in the app
    /// before that.
    ///
    /// Only the address is set in the app. The port is the one the build carries.
    /// </remarks>
    public static class RosEndpointSetting
    {
        /// <summary>The address the user set.</summary>
        public const string AddressKey = "erupt.ros.address";

        /// <summary>The built-in endpoint (<c>host:port</c>) that address was set in place of.</summary>
        public const string BuiltInKey = "erupt.ros.address.builtIn";

        /// <summary>
        /// Send the bus to the address the user saved, when there is one for the address this
        /// build carries. Changes nothing that is saved. True when a saved address was applied.
        /// </summary>
        public static bool ApplySaved(IRosEndpoint endpoint)
        {
            if (!TryGetSaved(endpoint, out string host)) return false;
            endpoint.ConnectTo(host, endpoint.DefaultPort);
            return true;
        }

        /// <summary>The address the user saved for the address this build carries, if any.</summary>
        public static bool TryGetSaved(IRosEndpoint endpoint, out string host)
        {
            host = null;
            if (endpoint == null) return false;
            if (!UserSettings.Store.TryGetString(AddressKey, out string saved)) return false;
            if (!UserSettings.Store.TryGetString(BuiltInKey, out string builtIn) || builtIn != BuiltIn(endpoint)) return false;
            return RosAddress.TryParseIPv4(saved, out host);
        }

        /// <summary>
        /// Remember <paramref name="host"/> as the address to use on this device, written at
        /// once. The built-in address needs no remembering: saving it forgets what was set.
        /// </summary>
        public static void Save(IRosEndpoint endpoint, string host)
        {
            if (endpoint == null) return;
            if (host == endpoint.DefaultHost)
            {
                UserSettings.Remove(AddressKey);
                UserSettings.Remove(BuiltInKey);
            }
            else
            {
                UserSettings.SetString(AddressKey, host);
                UserSettings.SetString(BuiltInKey, BuiltIn(endpoint));
            }
            UserSettings.Save();
        }

        private static string BuiltIn(IRosEndpoint endpoint) => $"{endpoint.DefaultHost}:{endpoint.DefaultPort}";
    }
}
