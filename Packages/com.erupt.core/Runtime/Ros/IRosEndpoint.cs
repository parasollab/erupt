namespace Erupt.Ros
{
    /// <summary>
    /// Where a ROS bus connects to, and the way to send it somewhere else while the app runs.
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="IRosBus"/>: a plugin talks over the connection it is given and
    /// has no business moving it. The core offers the address to the user on the Settings tab
    /// (<c>RosConnectionSection</c>); a bus that cannot change its endpoint does not implement
    /// this, and then there is no such setting.
    /// </remarks>
    public interface IRosEndpoint
    {
        /// <summary>The address the bus connects to now.</summary>
        string Host { get; }

        /// <summary>The port the bus connects to now.</summary>
        int Port { get; }

        /// <summary>The address the app was built with (Robotics > ROS Settings), whatever was set since.</summary>
        string DefaultHost { get; }

        /// <summary>The port the app was built with.</summary>
        int DefaultPort { get; }

        /// <summary>
        /// Use this endpoint from now on. A connection that is up, or still trying another
        /// address, is closed and opened again there; what is subscribed and registered stays
        /// and is announced to the new endpoint. Before the first connection this only says
        /// where it will go.
        /// </summary>
        void ConnectTo(string host, int port);
    }

    public static class RosEndpointExtensions
    {
        /// <summary>
        /// The endpoint of the connection under this bus (through a namespaced view of it), or
        /// null when the bus has none it can change.
        /// </summary>
        public static IRosEndpoint Endpoint(this IRosBus bus) =>
            bus is NamespacedRosBus scoped ? scoped.Inner as IRosEndpoint : bus as IRosEndpoint;
    }
}
