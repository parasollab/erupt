# Add ERUPT to your own project

The [quick start](quick-start.md) uses the ERUPT repository as a ready-made Unity project.
This page is for adding ERUPT to a Unity project you already have.

You need Unity 6 (6000.3) and `git` on your PATH.

## 1. Add ERUPT Core

In **Window > Package Manager**, choose **+ > Install package from git URL** and enter:

```text
https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.core
```

When the editor has loaded the package it asks whether to add two more packages that ERUPT
needs (the ROS connector and the URDF importer). Press **Add**.

## 2. Add the plugins and robots you want

Add each the same way. Add the MoveIt plugin before the MTC plugin.

| Package | Git URL |
| --- | --- |
| MoveIt plugin: planning and execution | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.moveit` |
| MTC plugin: pick and place | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.mtc` |
| Demonstration plugin: recording demonstrations | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.demonstration` |
| Franka Research 3 | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.franka` |
| Universal Robots UR5e, UR10e | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.universal-robots` |
| Kinova Gen3 | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.kinova` |
| UFactory xArm7 | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.ufactory` |
| KUKA LBR iiwa 14 | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.kuka` |

To stay on one version, add the same tag to every URL, for example `…com.erupt.core#v1.0.0`.

## 3. Run Project Setup

Open **ERUPT > Project Setup** and press **Fix**. It sets what ERUPT needs in the project:
the tags, the `ROS2` scripting define, the menu font, the Input System, the XR samples the
starter rig is built from, and OpenXR with the Quest features. Each line ends as *fine*,
*fixed* or *needs you*, with what to do.

Press **Fix** a second time after the editor has recompiled. The XR settings can only be
checked once the OpenXR package is there. If Project Setup changed the input handling, restart
the editor when Unity asks.

## 4. Import the Starter Scene

In the Package Manager, select **ERUPT Core**, open **Samples** and import **Starter Scene**.
Open the scene. It has the ERUPT root, an XR rig for controllers and hands, the plugins, a
floor and a table.

## 5. Add a robot and run

1. Drag a robot prefab from its package (`Robots/<Name>`) onto the table.
2. Start the ROS side for that robot, as in the [quick start](quick-start.md):
   `ROBOT=ur5e docker compose -f docker/compose.yaml up`.
3. Open **Robotics > ROS Settings** and set the address of the computer that runs ROS
   (`127.0.0.1` when it is this one).
4. Press Play.

## What is left for you

- **The headset build.** Switch the build target to Android and set what Meta asks of a Quest
  app (minimum API level, IL2CPP, ARM64, a package name). Project Setup leaves build settings
  alone.
- **Materials under another render pipeline.** Some robot materials are Universal Render
  Pipeline materials (all of the xArm7's). In a project with the built-in pipeline they show
  pink until you convert them or assign your own.
- **Your own XR rig.** The Starter Scene's rig comes from the XR Interaction Toolkit samples.
  To use another rig, add an `XriControllerBackend` to each controller and an
  `OpenXrHandBackend` for each hand, as the Starter Scene does.

The reference page
[Using ERUPT in your own project](../../Packages/com.erupt.core/Documentation~/installation.md)
lists every item Project Setup checks and how to run it from the command line.
