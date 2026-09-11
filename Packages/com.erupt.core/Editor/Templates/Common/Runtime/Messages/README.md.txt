# Generated ROS messages — {{DisplayName}}

Generate this plugin's ROS message bindings into this folder so they compile into
`{{AssemblyName}}.Messages`:

1. Open **Robotics > Generate ROS Messages...** (ROS-TCP-Connector message browser).
2. Set the ROS message path to your package's `msg` / `srv` / `action` folders.
3. Set the **output** path to `Packages/{{PackageId}}/Runtime/Messages` and generate.

Do not generate `std_msgs`, `geometry_msgs`, `sensor_msgs`, `trajectory_msgs` or `shape_msgs`
here; the connector ships them in `Unity.Robotics.ROSTCPConnector.Messages`, and duplicates
produce CS0436 conflicts. Record what you generated in `MESSAGES.md` at the package root.
