# Generated ROS messages — RADER

The RADER plugin uses only message types the connector ships (`std_msgs`, `sensor_msgs`,
`trajectory_msgs`, `builtin_interfaces`), so this folder holds no generated bindings.
The assembly `Erupt.Plugins.Rader.Messages` exists so the package keeps the plugin
template's shape and has a home if RADER-specific messages are added later.

To add some:

1. Open **Robotics > Generate ROS Messages...** (ROS-TCP-Connector message browser).
2. Set the ROS message path to your package's `msg` / `srv` / `action` folders.
3. Set the **output** path to `Packages/com.erupt.plugin.rader/Runtime/Messages` and generate.

Do not generate `std_msgs`, `geometry_msgs`, `sensor_msgs`, `trajectory_msgs` or `shape_msgs`
here; duplicates produce CS0436 conflicts. Record what you generated in `MESSAGES.md`.

The legacy `hri/JointQuery` message that shipped with the RADER submodule had no consumer in
this project and was dropped (recoverable from the `pre-plugin-refactor` tag or parasollab/RADER).
