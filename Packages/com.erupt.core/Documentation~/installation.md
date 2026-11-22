# Using ERUPT in your own project

The ERUPT repository is a Unity project you can clone and use as a template. You can also add
ERUPT's packages to a project of your own. This page is about the second way.

## The packages

| Package | Git URL (Package Manager > + > Install package from git URL) |
|---|---|
| ERUPT Core | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.core` |
| MoveIt plugin | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.moveit` |
| MTC plugin (needs MoveIt) | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.mtc` |
| Demonstration plugin | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.demonstration` |
| Robots | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.robot.franka`, `.universal-robots`, `.kinova`, `.ufactory`, `.kuka` |
| AR module (optional, Meta Quest) | `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.ar` |

Add a tag or a commit to pin a version: `...?path=/Packages/com.erupt.core#v1.0.0`. Use the same
one for every ERUPT package.

## Steps

1. **A Unity 6 project** (6000.3), with `git` on the PATH for the Package Manager.

2. **Add ERUPT Core first**, by its git URL. The Package Manager installs the registry
   packages core depends on (XR Interaction Toolkit, XR Hands, Input System, uGUI, XR Core
   Utils) with it.

3. **Let ERUPT add what it needs from git.** A package installed from git cannot declare git
   dependencies, so the Package Manager does not install the ROS-TCP-Connector and the
   URDF-Importer forks for core. When the editor has loaded core it asks:
   *ERUPT needs more packages … Add them to this project's manifest?* Press **Add**. (If you
   pressed *Not now*, `ERUPT > Project Setup` offers them again.) Until the connector is in
   the project and the `ROS2` scripting define is set (step 5), the parts of ERUPT that talk
   to ROS are not compiled; that is on purpose, so that a project in between has no compile
   errors and the question can be asked at all. The next time the editor starts it reminds
   you of Project Setup while the define is missing.

4. **Add the plugins and the robots you want**, by their git URLs: MoveIt before MTC (the
   Package Manager refuses a package whose ERUPT dependency is not in the project yet and
   names the one that is missing).
   The AR module is optional: add it only for augmented reality (robots placed at printed
   AprilTags, the room's furniture as obstacles). It asks for two more packages, the AprilTag
   fork and Meta's MR Utility Kit, in the same way core asks for its forks (step 3); a
   project without the module has neither. See the module's `README.md`.

5. **Run `ERUPT > Project Setup` and press Fix.** It checks, and where it can puts right:

   | Item | Why |
   |---|---|
   | Packages ERUPT needs | the two forks of step 3, and what an optional package in the project asks for (the AR module: the AprilTag fork and Meta's MR Utility Kit) |
   | Tags `Selectable`, `SnapSurface`, `robot` | ERUPT tags what it makes selectable and finds snap surfaces by tag; the URDF Importer tags robots |
   | Scripting define `ROS2` for Standalone and Android | without it the ROS-TCP-Connector speaks the ROS 1 protocol and Play never connects |
   | TextMesh Pro essential resources | the font of ERUPT's menus |
   | Active input handling | ERUPT reads controllers through the Input System package |
   | Render pipeline | told, not changed: ERUPT works under any; some shipped robot materials are URP materials |
   | Samples the Starter Scene's XR rig is built from | XR Interaction Toolkit: *Starter Assets*, *Hands Interaction Demo*; XR Hands: *HandVisualizer* |
   | OpenXR plug-in | the headset |
   | OpenXR is the XR plug-in for Standalone and Android | XR Plug-in Management |
   | OpenXR features | Oculus Touch controller profile, hand tracking, Meta hand tracking aim, Meta Quest support |

   Each item ends up **fine**, **fixed** or **needs you**, with what to do. Adding a package
   makes the editor recompile; press **Check Again** (or Fix again) afterwards, because the XR
   settings can only be looked at once the OpenXR plug-in is there.

6. **Import the Starter Scene**: Package Manager > ERUPT Core > Samples > Starter Scene >
   Import. Open `Assets/Samples/ERUPT Core/<version>/Starter Scene/StarterScene`. It has the
   ERUPT root, an XR rig with the controller and hand backends, the three plugins, a floor
   and a table, and no robot.

7. **Drag a robot in**: `Packages/ERUPT Robots: …/Robots/<Name>.prefab` onto the table
   (y = 0.75). Nothing else has to be wired; see `robots.md`.

8. **ROS.** Start the ERUPT ROS workspace for that robot
   (`ROBOT=ur5e docker compose -f docker/compose.yaml up erupt`), set its address under
   `Robotics > ROS Settings` when it is not this machine, and press Play. No headset is
   needed for a first look: without one the mouse and the keyboard drive the scene (left
   click selects, the right button drags, `Tab` or the Menu button opens the panel, `W A S D` and the arrow
   keys move the view; the keys are shown in the Game view and listed in core's README).

From the command line (CI, or to see what the window would say):

```bash
# what is missing from git, added to the manifest (no -quit: it waits for the Package Manager)
Unity -batchmode -projectPath . -executeMethod Erupt.Editor.Setup.EruptDependencies.AddMissingFromCommandLine
# every item checked and fixed; add -check to only look
Unity -batchmode -projectPath . -executeMethod Erupt.Editor.Setup.ProjectSetup.RunFromCommandLine
```

## What is left for you

- **The headset build**: switch the build target to Android, and set what Meta asks for a
  Quest app (minimum API level, IL2CPP and ARM64, the package name). Project Setup does not
  touch build settings.
- **The render pipeline.** The robots' meshes take the project's pipeline when they are
  imported (for Collada meshes ERUPT sees to that itself, because the Universal Render
  Pipeline converts FBX and OBJ materials at import and leaves Collada's alone; reimport
  the `.dae` files after changing the pipeline), but the materials the URDF Importer made for colours a URDF defines are
  Universal Render Pipeline materials (the xArm7's are all of this kind): in a project with
  the built-in pipeline they are pink until you convert them or assign your own.
- **Your own rig.** The Starter Scene's rig is built from XR Interaction Toolkit's sample
  rig. To use another, put an `XriControllerBackend` on each controller and an
  `OpenXrHandBackend` for each hand, as the sample scene does.
- **The ROS address** (`Robotics > ROS Settings`), which is per machine.

## How the offer works

Every ERUPT package may list, in its `package.json`, packages it needs that it cannot declare
as dependencies:

```json
"erupt": {
  "requires": [
    { "name": "com.unity.robotics.ros-tcp-connector", "source": "https://github.com/…#<commit>", "reason": "the ROS 2 transport" }
  ]
}
```

`Erupt.Setup.Editor` (in core, `Editor/Setup`) reads these lists from every `com.erupt.*`
package in the project and adds what is missing to `Packages/manifest.json` when you agree.
It references no other package and has no define constraint, so it compiles in a project
where the requirements are still missing. A plugin of your own can ask for its own
requirements the same way.
