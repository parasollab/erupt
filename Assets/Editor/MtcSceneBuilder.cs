using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// Bakes the MTC pick/place stack from KitchenFR3 (fr3 robot, PickPlaceClient, recorder,
// dashboard) into a demo scene in place of the ur5e. Two targets share the composition:
//   * "ARMTC": the AR demo scene (Build ARMTC).
//   * "FactoryDemo": the VR work cell, which additionally gets the Haas VF-2 (prefab built
//     from the Blender pipeline's FBX files, sliding doors + mesh collision objects), the
//     door menu, and the FR3 station (raw-stock stand = the 'table' collision object, vise
//     block on the machine table, conveyor to the far parts table) with GrabbablePartSet
//     billets (Build FactoryDemo MTC station from the ur5e scene, Relayout FactoryDemo on a
//     scene that already has the MTC stack). Batch entry points: BuildFactoryBatch,
//     RelayoutFactoryBatch, ValidateFactoryBatch, RenderFactoryShotsBatch (FACTORY_SHOT_DIR).
//
// What the AR build does, in order:
//   1. Opens Demo Scenes/AR.unity and saves it as Demo Scenes/ARMTC.unity (AR.unity is
//      never modified).
//   2. Loads Old Scenes/KitchenFR3.unity additively and moves over: the "fr3" robot (with
//      its MTCTrajectoryPlayer), the inactive "fr3 (1)" ghost template, "MTCManager"
//      (PickPlaceClient) and the "MTCMenu" dashboard prefab instance.
//   3. Moves the AR world origin ("BaseTransform", which lives under the ur5e) under the
//      fr3 at the same world pose, then deletes the ur5e and points everything that
//      referenced it at the fr3: the IK manager, marker placement, base TF publisher, tag
//      reachability, ghost spawner.
//   4. Adds the AttachedCollisionObjectListener to the AR CollisionObjectListener and a
//      PickPlaceTaskRecorder to MTCManager (KitchenFR3 kept its recorder inside the
//      inactive XR rig, which the persistent-infrastructure bootstrap destroys at runtime).
//   5. Re-points every reference the moved objects still hold into KitchenFR3 at the AR
//      scene's counterpart of the same name, enables the wrist menus' MTC buttons and
//      gives them the world origin, adds a passive XROrigin to the Meta camera rig (XRI far
//      grabs cannot move objects without one), and adds an XRInteractionGroup to each Meta
//      hand anchor so only one XRI interactor per hand can grab (as in the XRI rig).
//   6. Copies ARLoading.unity to ARMTCLoading.unity targeting the new scene, and adds both
//      scenes to the build settings.
public static class MtcSceneBuilder
{
    private const string FactoryScenePath = "Assets/Scenes/Demo Scenes/FactoryDemo.unity";
    private const string FactoryMaterialFolder = "Assets/Materials/Factory";
    private const string FactoryStationRoot = "FR3 Station";
    // The MTC server raises the requested place pose by 0.5 * object_dimensions[0] +
    // place_surface_offset (Panda demo config: 0.125 + 0.0001); cancel it so parts land where
    // the visitor released them.
    private const float FactoryPlaceHeightOffset = -0.1251f;
    private const string ArScenePath = "Assets/Scenes/Demo Scenes/AR.unity";
    private const string ArLoadingScenePath = "Assets/Scenes/Demo Scenes/ARLoading.unity";
    private const string KitchenScenePath = "Assets/Scenes/Old Scenes/KitchenFR3.unity";
    private const string OutScenePath = "Assets/Scenes/Demo Scenes/ARMTC.unity";
    private const string OutLoadingScenePath = "Assets/Scenes/Demo Scenes/ARMTCLoading.unity";
    private const string OutSceneName = "ARMTC";
    private const string MetaCameraRigName = "[BuildingBlock] Camera Rig";

    // Planning-side names of the fr3 as the MTC server exposes it (a "panda_"-prefixed
    // robot; see MTCTrajectoryPlayer.rosNamePrefix and the KitchenFR3 planning menu).
    private const string Fr3PlanningGroup = "panda_arm";
    private const string Fr3PlanningFrame = "panda_link0";
    // The ROS side runs the Panda MoveIt config while the scene shows the fr3; the IK seed
    // must be renamed on the way out, as MTCTrajectoryPlayer renames trajectories on the way in.
    private const string Fr3UnityJointPrefix = "fr3_";
    private const string Fr3RosJointPrefix = "panda_";
    private const string Fr3EndEffectorLink = "fr3_hand_tcp";

    private static readonly StringBuilder Log = new StringBuilder();

    [MenuItem("ERUPT/Scenes/Build ARMTC (AR + MTC on fr3)")]
    public static void BuildAr()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        Log.Clear();
        try
        {
            BuildMainScene();
            BuildLoadingScene();
            AddToBuildSettings(OutLoadingScenePath);
            AddToBuildSettings(OutScenePath);
            EditorSceneManager.OpenScene(OutScenePath, OpenSceneMode.Single);
            Debug.Log("MtcSceneBuilder: done.\n" + Log);
        }
        catch (Exception e)
        {
            Debug.LogError("MtcSceneBuilder: FAILED.\n" + Log + "\n" + e);
            throw;
        }
    }

    [MenuItem("ERUPT/Scenes/Build FactoryDemo MTC station")]
    public static void BuildFactory()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        Log.Clear();
        try
        {
            BuildFactoryScene();
            Debug.Log("MtcSceneBuilder: done.\n" + Log);
        }
        catch (Exception e)
        {
            Debug.LogError("MtcSceneBuilder: FAILED.\n" + Log + "\n" + e);
            throw;
        }
    }

    [MenuItem("ERUPT/Scenes/Relayout FactoryDemo (VF-2 cell)")]
    public static void RelayoutFactory()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        Log.Clear();
        try
        {
            RelayoutFactoryScene();
            Debug.Log("MtcSceneBuilder: done.\n" + Log);
        }
        catch (Exception e)
        {
            Debug.LogError("MtcSceneBuilder: FAILED.\n" + Log + "\n" + e);
            throw;
        }
    }

    public static void BuildFactoryBatch() => Batch(BuildFactoryScene);
    public static void RelayoutFactoryBatch() => Batch(RelayoutFactoryScene);
    public static void ValidateFactoryBatch() => Batch(() => { if (ValidateFactory() > 0) throw new InvalidOperationException("validation failed"); });
    public static void RenderFactoryShotsBatch() => Batch(() => RenderFactoryShots(Environment.GetEnvironmentVariable("FACTORY_SHOT_DIR")));

    private static void Batch(Action action)
    {
        Log.Clear();
        int rc = 0;
        try { action(); Debug.Log("MtcSceneBuilder: done.\n" + Log); }
        catch (Exception e) { Debug.LogError("MtcSceneBuilder: FAILED.\n" + Log + "\n" + e); rc = 1; }
        if (Application.isBatchMode) EditorApplication.Exit(rc);
    }

    // ------------------------------------------------------------------ main scene

    private static void BuildMainScene()
    {
        Scene ar = EditorSceneManager.OpenScene(ArScenePath, OpenSceneMode.Single);
        if (!EditorSceneManager.SaveScene(ar, OutScenePath))
            throw new InvalidOperationException($"Could not save {OutScenePath}");
        Line($"Saved {ArScenePath} as {OutScenePath}");

        Scene kitchen = EditorSceneManager.OpenScene(KitchenScenePath, OpenSceneMode.Additive);
        Compose(ar, kitchen, arExtras: true, label: OutSceneName);
        EditorSceneManager.CloseScene(kitchen, true);
        Line($"Closed {KitchenScenePath} without saving");

        EditorSceneManager.MarkSceneDirty(ar);
        if (!EditorSceneManager.SaveScene(ar))
            throw new InvalidOperationException($"Could not save {OutScenePath}");
        Line($"Saved {OutScenePath}");
    }

    private static void BuildFactoryScene()
    {
        Scene factory = EditorSceneManager.OpenScene(FactoryScenePath, OpenSceneMode.Single);
        if (factory.GetRootGameObjects().Any(r => r.name == "fr3"))
            throw new InvalidOperationException(
                $"{FactoryScenePath} already contains 'fr3'; use Relayout FactoryDemo, or restore the committed ur5e version before rebuilding");

        Scene kitchen = EditorSceneManager.OpenScene(KitchenScenePath, OpenSceneMode.Additive);
        Composition c = Compose(factory, kitchen, arExtras: false, label: "FactoryDemo");
        LayoutVf2Cell(factory, c);
        EditorSceneManager.CloseScene(kitchen, true);
        Line($"Closed {KitchenScenePath} without saving");

        EditorSceneManager.MarkSceneDirty(factory);
        if (!EditorSceneManager.SaveScene(factory))
            throw new InvalidOperationException($"Could not save {FactoryScenePath}");
        Line($"Saved {FactoryScenePath}");
    }

    // Rebuilds the VF-2 cell (machine, door menu, station) in a FactoryDemo that already holds
    // the MTC stack. Everything under 'Machines', 'FR3 Station' and the door menu is recreated;
    // the robot, menus and ROS wiring stay as they are.
    private static void RelayoutFactoryScene()
    {
        Scene factory = EditorSceneManager.OpenScene(FactoryScenePath, OpenSceneMode.Single);
        if (!factory.GetRootGameObjects().Any(r => r.name == "fr3"))
            throw new InvalidOperationException($"{FactoryScenePath} has no 'fr3'; run Build FactoryDemo MTC station first");
        Composition c = ComposeExisting(factory);
        LayoutVf2Cell(factory, c);
        EditorSceneManager.MarkSceneDirty(factory);
        if (!EditorSceneManager.SaveScene(factory))
            throw new InvalidOperationException($"Could not save {FactoryScenePath}");
        Line($"Saved {FactoryScenePath}");
    }

    // Everything the two targets share once the MTC stack is in the scene.
    private sealed class Composition
    {
        public GameObject fr3, fr3Ghost, mtcManager, mtcMenu, baseTransform;
        public GameObject selectionGo, ikManagerGo, listenerGo, ghostsGo, planningMenuGo;
        public SelectionManager selection;
        public DirectArticulationIKController ik;
        public CollisionObjectsListenerSimple listener;
        public PickPlaceTaskRecorder recorder;
        public PickPlaceClient client;
        public MTCDashboardPanel dashboard;
    }

    // Moves the MTC stack from KitchenFR3 into 'target' and swaps the fr3 in for the ur5e.
    // arExtras wires the AR-only objects (marker placement, base TF publisher, tag reachability).
    private static Composition Compose(Scene target, Scene kitchen, bool arExtras, string label)
    {
        var c = new Composition();

        // --- target side ------------------------------------------------------------
        c.selectionGo = Root(target, "SelectionManager");
        c.ikManagerGo = Root(target, "Robot IK Manager");
        c.listenerGo = Root(target, "CollisionObjectListener");
        c.ghostsGo = Root(target, "SpawnGhosts");
        c.planningMenuGo = Root(target, "MoveItPlanningRequestMenu");
        GameObject ur5e = Root(target, "ur5e_robot");

        c.selection = Require<SelectionManager>(c.selectionGo);
        c.ik = Require<DirectArticulationIKController>(c.ikManagerGo);
        var robotInteraction = Require<Quest3RobotInteractionController>(c.ikManagerGo);
        c.listener = Require<CollisionObjectsListenerSimple>(c.listenerGo);
        var ghosts = Require<SpawnGhosts>(c.ghostsGo);
        var replay = Require<TrajectoryReplay>(c.ghostsGo);
        var planningMenuUi = c.planningMenuGo.GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
        if (planningMenuUi == null)
            throw new InvalidOperationException("MoveItPlanningRequestMenu has no MoveItPlanningRequestMenuUI");

        MarkerRobotPlacement markerPlacement = null;
        RobotBaseTFPublisher baseTf = null;
        TagReachabilityIndicator tagReach = null;
        if (arExtras)
        {
            GameObject cameraGo = Root(target, "Camera");
            markerPlacement = Require<MarkerRobotPlacement>(cameraGo);
            baseTf = Require<RobotBaseTFPublisher>(cameraGo);
            tagReach = Require<TagReachabilityIndicator>(Root(target, "Tag Reachability"));
        }

        c.baseTransform = c.listener.worldOrigin != null ? c.listener.worldOrigin : FindByName(target, "BaseTransform");
        if (c.baseTransform == null)
            throw new InvalidOperationException($"{label} has no BaseTransform world origin");
        Line($"World origin: '{c.baseTransform.name}'");

        // --- Kitchen side ---------------------------------------------------------
        c.fr3 = Root(kitchen, "fr3");
        c.fr3Ghost = Root(kitchen, "fr3 (1)");
        c.mtcManager = Root(kitchen, "MTCManager");
        c.mtcMenu = Root(kitchen, "MTCMenu");
        var kitchenAttached = Require<AttachedCollisionObjectListener>(Root(kitchen, "CollisionObjectListener"));
        var kitchenRecorder = FindInScene<PickPlaceTaskRecorder>(kitchen);
        if (kitchenRecorder == null)
            throw new InvalidOperationException("KitchenFR3 has no PickPlaceTaskRecorder");
        var kitchenPlanningMenuUi = Root(kitchen, "MoveItPlanningRequestMenu")
            .GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
        var fr3Player = Require<MTCTrajectoryPlayer>(c.fr3);
        c.client = Require<PickPlaceClient>(c.mtcManager);

        Transform fr3Tcp = c.fr3.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == Fr3EndEffectorLink);
        if (fr3Tcp == null)
            throw new InvalidOperationException($"fr3 has no '{Fr3EndEffectorLink}' link");

        // --- Move the MTC stack and the robot into the target scene -----------------
        var moved = new[] { c.fr3, c.fr3Ghost, c.mtcManager, c.mtcMenu };
        foreach (GameObject go in moved)
        {
            go.transform.SetParent(null, true);
            SceneManager.MoveGameObjectToScene(go, target);
            Line($"Moved '{go.name}' into {label}");
        }

        // --- Robot swap: fr3 takes the ur5e's place ---------------------------------
        c.fr3.transform.SetPositionAndRotation(ur5e.transform.position, ur5e.transform.rotation);
        c.fr3.transform.localScale = ur5e.transform.localScale;
        c.fr3.SetActive(true);

        var reachProfile = c.fr3.GetComponent<RobotReachProfile>();
        if (reachProfile == null) reachProfile = c.fr3.AddComponent<RobotReachProfile>();
        reachProfile.planningGroupName = Fr3PlanningGroup;
        reachProfile.ikLinkName = "";
        reachProfile.planningFrameId = Fr3PlanningFrame;
        reachProfile.baseAnchor = c.baseTransform.transform;
        reachProfile.unityJointNamePrefix = Fr3UnityJointPrefix;
        reachProfile.rosJointNamePrefix = Fr3RosJointPrefix;
        reachProfile.reachabilityMap = null;
        EditorUtility.SetDirty(reachProfile);

        SetRef(c.ik, "robotRoot", c.fr3.transform);
        SetRef(c.ik, "endEffector", fr3Tcp);
        SetRef(robotInteraction, "endEffector", fr3Tcp);
        if (arExtras)
        {
            SetRef(markerPlacement, "leftRobot", c.fr3);
            SetRef(tagReach, "robot", c.fr3);
            SetString(tagReach, "planningGroupName", Fr3PlanningGroup);
            SetString(tagReach, "planningFrameId", Fr3PlanningFrame);
            SetString(tagReach, "unityJointNamePrefix", Fr3UnityJointPrefix);
            SetString(tagReach, "rosJointNamePrefix", Fr3RosJointPrefix);
            baseTf.robotBase = c.fr3;
            EditorUtility.SetDirty(baseTf);
        }
        ghosts.robotPrefab = c.fr3Ghost;
        ghosts.realRobot = c.fr3;
        SetRef(replay, "ikController", c.ik);
        EditorUtility.SetDirty(ghosts);

        // The planning menu was configured for the UR; take the fr3 values from the
        // KitchenFR3 menu instead of hard-coding them here.
        CopyStrings(kitchenPlanningMenuUi, planningMenuUi,
            "planningGroupName", "defaultPlannerId", "executeTrajectoryTopic", "planningPipelineId",
            "motionPlanServiceName", "rosJointNamePrefix", "unityJointNamePrefix");

        // The world origin ("BaseTransform": the robot base frame with the URDF root's extra
        // yaw removed) lives under the ur5e. Keep it, at the same world pose, under the fr3 --
        // otherwise every worldOrigin / baseAnchor reference silently serializes as null once
        // the ur5e is deleted.
        if (c.baseTransform.transform.IsChildOf(ur5e.transform))
        {
            c.baseTransform.transform.SetParent(c.fr3.transform, true);
            Line($"Moved world origin '{c.baseTransform.name}' under '{c.fr3.name}' (world pose kept)");
        }
        SetRef(c.listener, "worldOrigin", c.baseTransform);

        UnityEngine.Object.DestroyImmediate(ur5e);
        Line("Deleted 'ur5e_robot'");

        // --- Components that must live on target-scene objects ----------------------
        var attached = PasteAsNew<AttachedCollisionObjectListener>(kitchenAttached, c.listenerGo);
        SetRef(attached, "sceneListener", c.listener);
        SetRef(attached, "ikController", c.ik);
        SetRef(attached, "worldOrigin", c.baseTransform);

        c.recorder = PasteAsNew<PickPlaceTaskRecorder>(kitchenRecorder, c.mtcManager);
        SetRef(c.recorder, "selectionManager", c.selection);
        SetRef(c.recorder, "worldOrigin", c.baseTransform);
        SetRef(c.recorder, "pickPlaceAction", c.client);

        c.dashboard = c.mtcMenu.GetComponentInChildren<MTCDashboardPanel>(true);
        if (c.dashboard == null)
            throw new InvalidOperationException("MTCMenu has no MTCDashboardPanel");
        SetRef(c.dashboard, "pickPlaceRecorder", c.recorder);
        SetRef(c.dashboard, "pickPlaceAction", c.client);
        SetRef(c.dashboard, "trajectoryPlayer", fr3Player);
        SetRef(c.dashboard, "selectionManager", c.selection);

        SetRef(fr3Player, "ikController", c.ik);
        SetRef(fr3Player, "sceneListener", c.listener);

        // Park the dashboard beside the planning menu rather than at its kitchen spot.
        Transform menuAnchor = c.planningMenuGo.transform;
        c.mtcMenu.transform.SetPositionAndRotation(menuAnchor.position + menuAnchor.right * 0.7f, menuAnchor.rotation);

        // Every wrist menu in the scene (one under the Meta hand anchor, one inside the
        // inactive XRI rig) gets the MTC buttons and their targets; the runtime bootstrap
        // only re-binds the wrist menu inside the persistent XRI rig.
        foreach (WristMenuController wrist in FindAllInScene<WristMenuController>(target))
        {
            SetBool(wrist, "enableMTC", true);
            SetRef(wrist, "pickPlaceRecorder", c.recorder);
            SetRef(wrist, "mtcDashboardPanel", c.mtcMenu);
            SetRef(wrist, "selectionManager", c.selection);
            SetRef(wrist, "collisionObjectsListener", c.listener);
            SetRef(wrist, "worldOrigin", c.baseTransform);
            Line($"Wrist menu '{Path(wrist.transform)}': MTC enabled");
        }

        // Meta-rig fixes needed by the AR scenes only; the VR demo rig (ReachabilityVR lineage)
        // runs its interactors without a group or a passive XROrigin.
        if (arExtras)
        {
            EnsureMetaRigXROrigin(target);
            AddHandInteractionGroups(target, Require<XRInteractionManager>(Root(target, "XR Interaction Manager")));
        }

        // --- Anything still pointing into KitchenFR3 goes to the target counterpart ---
        var counterparts = new Dictionary<string, GameObject>
        {
            { "SelectionManager", c.selectionGo },
            { "Robot IK Manager", c.ikManagerGo },
            { "SpawnGhosts", c.ghostsGo },
            { "CollisionObjectListener", c.listenerGo },
            { "MoveItPlanningRequestMenu", c.planningMenuGo },
            { "XR Interaction Manager", Root(target, "XR Interaction Manager") },
            { "EventSystem", Root(target, "EventSystem") },
            { "XR UI Toolkit Manager", Root(target, "XR UI Toolkit Manager") },
            { "origin", c.baseTransform },
        };
        var rewireRoots = new List<GameObject>(moved) { c.listenerGo };
        RewireKitchenReferences(rewireRoots, kitchen, counterparts);
        return c;
    }

    // ------------------------------------------------------------------ VF-2 work cell

    private const string Vf2ModelPath = "Assets/Models/Factory Machines/HaasVF2TR.fbx";
    private const string Vf2CollisionModelPath = "Assets/Models/Factory Machines/HaasVF2TR_collision.fbx";
    private const string Vf2PrefabPath = "Assets/Prefabs/Factory/HaasVF2TR.prefab";
    private const string Vf2MaterialPrefix = "HaasVF2TR";
    private const string MachineMaterialFolder = "Assets/Materials/Factory/Machines";
    private const string MachineGlassMaterialPath = "Assets/Materials/Factory/MachineGlass.mat";
    private const string PlanningMenuPrefabPath = "Assets/Prefabs/MoveItPlanningRequestMenu.prefab";
    private const string DoorPanelUxmlPath = "Assets/UI Toolkit/MachineDoorPanel.uxml";
    private const string MachineName = "Haas VF-2TR";
    private const string DoorMenuName = "MachineDoorMenu";
    private const string Vf2BodyId = "vf2_body";
    private const string Vf2DoorLeftId = "vf2_door_left";
    private const string Vf2DoorRightId = "vf2_door_right";
    private const string Vf2TrunnionId = "vf2_trunnion";
    private const string Vf2PlatterId = "vf2_platter";
    // Travel of each sliding door (metres); the outer edges stop just inside the enclosure's side panels.
    private const float DoorTravel = 0.48f;
    // Door panel: 300 x DoorPanelHeight document units at the menu prefab's 0.2 scale (1 unit = 2 mm).
    private const float DoorPanelHeight = 440f;

    // Cell geometry (world metres; floor y = 0, visitor at the origin facing +z, fr3 base at
    // (Fr3BaseX, 0.75, 1) on RobotPedestal). The VF-2 stands behind the robot facing it with its
    // door front at DoorPlaneZ, 3 cm behind the pedestal's front face; the arm reaches over the
    // door sill (0.62 m above the floor) onto the machine table (0.85 m). The robot stands off to
    // the conveyor side of the door opening (-0.27..0.67) so the area directly in front of the
    // doors stays clear for the visitor to see and reach the fixture; the arm enters the opening
    // diagonally (the trunnion platter is in the opening's left part, x -0.17; MoveIt checks the
    // door meshes). Raw stock is stacked on a stand in front of the robot on the visitor's side,
    // the conveyor runs from the robot's left to the parts table.
    private const float Fr3BaseX = -0.4f;
    private static readonly Vector3 PedestalSize = new Vector3(0.6f, 0.75f, 0.44f);
    // Pedestal centre is 3 cm in front of the robot base so its back face clears the voxel-thickened
    // front-panel collision mesh (which reaches 3 cm in front of the enclosure's lower panel).
    private const float PedestalZ = 0.97f;
    private const float DoorPlaneZ = 1.25f;
    // The TRT-160 platter sits left of the spindle centreline; the machine is shifted right so the
    // platter is only 0.18 m off the robot's axis while the arm still passes through the door
    // opening (0.93 m wide, so 0.27 m of clearance on the near side).
    private const float MachineXOffset = 0.2f;
    // Self-centring pneumatic vise on the trunnion platter (Factory.MachineVise; the CAD's saddle
    // is jogged 0.25 m toward the doors in spec_vf2tr.json so the platter is within reach). Base
    // plate, a parallels plate the part rests on, two jaws authored open ViseOpenGap apart with
    // hardened plates facing inward, lamps on the front, an air block on the back. The jaws run
    // along x; the clamp axis is z. Placed from the prefab's Platter part at build time; the
    // TRT-160 platter is narrower than the base, which overhangs it front and back the way a
    // 5-axis vise on an adapter plate does.
    private static readonly Vector3 ViseBaseSize = new Vector3(0.16f, 0.03f, 0.18f);
    // The vise sits this far toward the doors off the platter centre: the machine's collision
    // mesh rises 14 cm above the jaws just behind the platter's back edge, and the layout rule
    // wants the fixture's footprint (+2 cm) clear of it.
    private const float ViseZOffset = -0.01f;
    private static readonly Vector3 ViseParallelsSize = new Vector3(0.16f, 0.015f, 0.09f);
    private const float ViseOpenGap = 0.12f; // between the jaw plates, fully open
    private static readonly Vector3 ViseJawSize = new Vector3(0.16f, 0.045f, 0.026f);
    private const float ViseJawPlateThickness = 0.004f;
    // The job wants this much part above the jaw plates: the billet as stacked (6 cm tall) gives
    // 3 cm plus the rest gap, laid on its 6 cm side it gives 2.5 cm and the panel flags it.
    private const float ViseRequiredStickout = 0.03f;
    private const float ViseLampSize = 0.01f;
    private static readonly Vector3 StockStandSize = new Vector3(0.3f, 0.7f, 0.3f);
    private static readonly Vector3 StockStandCentre = new Vector3(Fr3BaseX, 0.35f, 0.40f);
    // Billets are 6 cm tall so a top-down grasp on the top one keeps the fingertips above the next.
    private static readonly Vector3 BilletSize = new Vector3(0.08f, 0.06f, 0.05f);
    private const int BilletCount = 4;
    // The belt runs in front of the VF-2's chip-conveyor shelf, which sticks 0.5 m out of the
    // enclosure's front-left (its collision mesh reaches z 0.91 at belt height).
    private const float ConveyorZ = 0.70f, ConveyorStartX = Fr3BaseX - 0.40f, ConveyorEndX = Fr3BaseX - 2.85f, ConveyorTopY = 0.72f, ConveyorWidth = 0.3f;
    private static readonly Vector3 PartsTableCentre = new Vector3(Fr3BaseX - 3.7f, 0.715f, 0.8f);
    private static readonly Vector3 TableSize = new Vector3(1.6f, 0.06f, 0.9f);
    private static readonly Vector3 DoorMenuPosition = new Vector3(1.4f, 1.3f, 1.0f);
    private static readonly Vector3 XrDashboardPosition = new Vector3(-2.2f, 1.39f, 2.4f);
    // Parts rest this far above their support. MTC's "attach object" stage collision-checks the
    // scene with the part already attached to the hand, one stage before it allows object/table
    // contact, so a part sitting exactly on a surface fails every plan.
    private const float RestGap = 0.005f;
    private const float Fr3Reach = 0.855f;

    // Robot placement phase (RobotPlacementController): the visitor drags a ring on the floor
    // around the pedestal, the robot + pedestal follow it, reach markers show whether the
    // anchors are solvable, Confirm opens the pick-and-place UI. The handle's pivot is the
    // robot axis on the floor; the ring is offset so it circles the pedestal.
    private const string ReachVolumeName = "Reachability Volume";
    private const string PlacementRootName = "RobotPlacement";
    private const string PlacementHandleName = "RobotPlacementHandle";
    private const string ReachMarkersName = "ReachTargetMarkers";
    private const string PlacementMenuName = "RobotPlacementMenu";
    private const string DesktopRigName = "Desktop Mouse Rig";
    private const string PlacementPanelUxmlPath = "Assets/UI Toolkit/RobotPlacementPanel.uxml";
    private const string PlacementRingMeshPath = "Assets/Models/Factory Machines/PlacementRing.asset";
    private const float PlacementPanelHeight = 430f;
    private static readonly Vector3 Fr3BasePosition = new Vector3(Fr3BaseX, 0.75f, 1f);
    private static readonly Vector3 DefaultHandlePosition = new Vector3(Fr3BaseX, 0f, 1f);
    private const float HandleRingInnerRadius = 0.42f, HandleRingOuterRadius = 0.50f, HandleRingThickness = 0.02f;
    // World x/z the handle may be moved within; z max keeps the pedestal front in front of the door plane.
    private static readonly Vector2 PlacementBoundsMin = new Vector2(-1.2f, -0.2f);
    private static readonly Vector2 PlacementBoundsMax = new Vector2(1.2f, 1.03f);
    private static readonly Vector3 PlacementMenuPosition = new Vector3(-1.3f, 1.3f, 0.3f);
    // Where the robot stands when the placement phase opens: front-right of the cell, turned away
    // from the machine, ~1.1 m from the ideal spot. The pedestal must clear the stock stand and the
    // conveyor, and the pose must lie inside the bounds (the validator checks both).
    private static readonly Vector2 PlacementStartOffset = new Vector2(0.8f, -0.8f);
    private const float PlacementStartYaw = 35f;

    // Maps Blender-frame vectors (x right, y back, z up as exported) into the prefab's local frame,
    // read from the Axis_X/Y/Z empties bl_assemble.py / bl_export_urdf.py put in every FBX.
    private sealed class AxisMap
    {
        public Vector3 x, y, z;
        public Vector3 V(float bx, float by, float bz) => bx * x + by * y + bz * z;
        public override string ToString() => $"X->{x} Y->{y} Z->{z}";
    }

    private static Composition ComposeExisting(Scene scene)
    {
        var c = new Composition
        {
            fr3 = Root(scene, "fr3"),
            fr3Ghost = Root(scene, "fr3 (1)"),
            mtcManager = Root(scene, "MTCManager"),
            mtcMenu = Root(scene, "MTCMenu"),
            selectionGo = Root(scene, "SelectionManager"),
            ikManagerGo = Root(scene, "Robot IK Manager"),
            listenerGo = Root(scene, "CollisionObjectListener"),
            ghostsGo = Root(scene, "SpawnGhosts"),
            planningMenuGo = Root(scene, "MoveItPlanningRequestMenu"),
        };
        c.selection = Require<SelectionManager>(c.selectionGo);
        c.ik = Require<DirectArticulationIKController>(c.ikManagerGo);
        c.listener = Require<CollisionObjectsListenerSimple>(c.listenerGo);
        c.recorder = Require<PickPlaceTaskRecorder>(c.mtcManager);
        c.client = Require<PickPlaceClient>(c.mtcManager);
        c.dashboard = c.mtcMenu.GetComponentInChildren<MTCDashboardPanel>(true);
        c.baseTransform = c.listener.worldOrigin != null ? c.listener.worldOrigin : FindByName(scene, "BaseTransform");
        if (c.baseTransform == null)
            throw new InvalidOperationException("FactoryDemo has no BaseTransform world origin");
        Line($"Existing MTC stack found; world origin '{c.baseTransform.name}'");
        return c;
    }

    private static void LayoutVf2Cell(Scene scene, Composition c)
    {
        foreach (string name in new[] { "Machines", FactoryStationRoot, DoorMenuName, PlacementRootName, PlacementHandleName, ReachMarkersName, PlacementMenuName })
        {
            GameObject old = scene.GetRootGameObjects().FirstOrDefault(r => r.name == name);
            if (old == null) continue;
            UnityEngine.Object.DestroyImmediate(old);
            Line($"Removed previous '{name}'");
        }

        // The robot's ideal spot (the scene is authored there; the placement phase starts elsewhere).
        c.fr3.transform.SetPositionAndRotation(Fr3BasePosition, Quaternion.identity);
        GameObject pedestal = Root(scene, "RobotPedestal");
        pedestal.transform.localScale = PedestalSize;
        pedestal.transform.position = new Vector3(Fr3BaseX, PedestalSize.y * 0.5f, PedestalZ);
        Line($"fr3 at {Fr3BasePosition}; RobotPedestal resized to {PedestalSize}");

        Material tableMat = MakeMaterial("StationTable", new Color(0.30f, 0.31f, 0.33f), 0.55f, 0.7f);
        Material trayMat = MakeMaterial("StationTray", new Color(0.16f, 0.17f, 0.18f), 0.3f, 0f);
        Material blankMat = MakeMaterial("Blank", new Color(0.78f, 0.80f, 0.83f), 0.7f, 0.9f);
        Material fixtureMat = MakeMaterial("FixturePlate", new Color(0.22f, 0.22f, 0.24f), 0.5f, 0.6f);
        Material beltMat = MakeMaterial("ConveyorBelt", new Color(0.09f, 0.09f, 0.10f), 0.25f, 0f);
        Material frameMat = MakeMaterial("ConveyorFrame", new Color(0.45f, 0.47f, 0.50f), 0.5f, 0.8f);

        // --- the machine ----------------------------------------------------------------
        GameObject prefab = EnsureVf2Prefab();
        var machines = new GameObject("Machines");
        SceneManager.MoveGameObjectToScene(machines, scene);
        var machine = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        machine.name = MachineName;
        machine.transform.SetParent(machines.transform, false);
        // The prefab's front (Blender -Y) is local +z; turn it toward the visitor.
        machine.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
        var doors = Require<Factory.MachineDoors>(machine);
        if (doors.doors == null || doors.doors.Length != 2 || doors.doors.Any(d => d == null))
            throw new InvalidOperationException("HaasVF2 prefab has no two door SlidingParts");
        Bounds doorBounds = RendererBounds(doors.doors[0].transform);
        doorBounds.Encapsulate(RendererBounds(doors.doors[1].transform));
        Bounds bodyBounds = RendererBounds(machine.transform);
        if (doorBounds.center.z > bodyBounds.center.z)
            throw new InvalidOperationException("VF-2 doors are not on the visitor side; check the prefab's front axis");
        machine.transform.position = new Vector3(MachineXOffset - doorBounds.center.x, 0f, DoorPlaneZ - doorBounds.min.z);
        foreach (CollisionObjectPublisher pub in machine.GetComponentsInChildren<CollisionObjectPublisher>(true))
        {
            pub.worldOrigin = c.baseTransform;
            EditorUtility.SetDirty(pub);
        }
        Bounds placed = RendererBounds(machine.transform);
        Line($"{MachineName} at {machine.transform.position}, bounds {placed.min} .. {placed.max}, door front z = {DoorPlaneZ}");
        Bounds platterBounds = RendererBounds(FindChild(machine, "Platter"));
        Vector3 fixtureCentre = new Vector3(platterBounds.center.x, platterBounds.max.y + ViseBaseSize.y * 0.5f, platterBounds.center.z + ViseZOffset);
        Line($"Platter top at {platterBounds.max.y:F3}, centre ({platterBounds.center.x:F3}, {platterBounds.center.z:F3}), size {platterBounds.size}; vise base at {fixtureCentre}");
        // Parts the robot drops land on the platter or trunnion if they miss the vise; the CAD
        // parts have no colliders of their own (the machine's collision meshes are for MoveIt).
        int machineColliders = 0;
        foreach (string partName in new[] { "Platter", "Trunnion" })
        {
            Transform part = FindChild(machine, partName);
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponent<Renderer>() == null || mf.sharedMesh == null || mf.GetComponent<MeshCollider>() != null) continue;
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                machineColliders++;
            }
        }
        Line($"{machineColliders} mesh collider(s) added to the platter and trunnion");

        // --- the station ----------------------------------------------------------------
        var station = new GameObject(FactoryStationRoot);
        SceneManager.MoveGameObjectToScene(station, scene);
        Transform root = station.transform;

        // Raw stock stand. It is the published 'table' (the MTC server's surface_link): the one
        // object the carried part may touch during lift and place. The conveyor and the vise
        // inside the machine are published as further primitives of this same object
        // (compoundRoots) so the arm avoids them too while a part may rest on any of them.
        GameObject stand = Box("RawStockStand", root, StockStandCentre, StockStandSize, tableMat);
        var tablePub = stand.AddComponent<CollisionObjectPublisher>();
        tablePub.objectId = "table";
        tablePub.isMesh = false;
        tablePub.worldOrigin = c.baseTransform;
        tablePub.publishRateHz = 0.5f;

        float standTop = StockStandCentre.y + StockStandSize.y * 0.5f;
        Transform stock = Group("RawStock", root, new Vector3(StockStandCentre.x, standTop, StockStandCentre.z));
        var stockSlots = new List<Transform>();
        for (int i = 0; i < BilletCount; i++)
        {
            float y = standTop + RestGap + BilletSize.y * 0.5f + i * (BilletSize.y + RestGap);
            stockSlots.Add(Group($"StockSlot{i}", stock, new Vector3(StockStandCentre.x, y, StockStandCentre.z)));
        }

        // The vise on the platter: where a billet is loaded for machining.
        Material viseMat = MakeMaterial("ViseBody", new Color(0.17f, 0.19f, 0.25f), 0.45f, 0.75f);
        Material jawPlateMat = MakeMaterial("ViseJawPlate", new Color(0.56f, 0.57f, 0.60f), 0.65f, 0.95f);
        Material lampMat = MakeMaterial("ViseLamp", new Color(0.25f, 0.25f, 0.25f), 0.3f, 0f);
        (Transform fixture, Factory.MachineVise vise) = BuildVise(root, fixtureCentre, viseMat, jawPlateMat, fixtureMat, lampMat, tablePub);
        Line($"Vise '{fixture.name}' at {fixtureCentre}: opening {ViseOpenGap} m, parallels top {vise.jawWindow.position.y:F3}, jaw plates top {vise.jawWindow.position.y + vise.jawHeight:F3}");

        Transform conveyor = BuildConveyor(root, beltMat, frameMat);
        tablePub.compoundRoots = new[] { conveyor, fixture };
        Line("'table' collision object = stock stand + conveyor + vise (jaws included, re-sent when they move)");

        // Parts table at the far end of the conveyor (phase 2 content goes here).
        Transform table = Group("PartsTable", root, PartsTableCentre);
        Box("Top", table, PartsTableCentre, TableSize, tableMat);
        float legHeight = PartsTableCentre.y - TableSize.y * 0.5f;
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                Box("Leg", table,
                    new Vector3(PartsTableCentre.x + sx * (TableSize.x * 0.5f - 0.05f), legHeight * 0.5f, PartsTableCentre.z + sz * (TableSize.z * 0.5f - 0.05f)),
                    new Vector3(0.06f, legHeight, 0.06f), fixtureMat);

        Transform partsRoot = Group("Parts", root, Vector3.zero);

        var billets = station.AddComponent<Factory.GrabbablePartSet>();
        billets.slots = stockSlots.ToArray();
        billets.partSize = BilletSize;
        billets.partMaterial = blankMat;
        billets.idPrefix = "blank_";
        billets.worldOrigin = c.baseTransform;
        billets.listener = c.listener;
        billets.partsRoot = partsRoot;
        billets.freezeRotation = false; // handle like wrist-menu shapes
        // The billets are baked into the scene (same grab/publish stack the runtime spawner would
        // add) so the stack is visible in the editor; GrabbablePartSet registers them at Start.
        var baked = new List<GameObject>();
        for (int i = 0; i < stockSlots.Count; i++)
        {
            string id = $"{billets.idPrefix}{i + 1}";
            GameObject part = GrabbableCollisionObjectFactory.CreatePrimitive(PrimitiveType.Cube, id, BilletSize, id, c.baseTransform, blankMat, billets.freezeRotation);
            part.transform.SetParent(partsRoot, true);
            part.transform.SetPositionAndRotation(stockSlots[i].position, c.baseTransform.transform.rotation);
            baked.Add(part);
        }
        billets.prePlaced = baked.ToArray();
        Line($"{baked.Count} billets baked on the stock stand");

        // Parts the robot lets go of fall onto whatever is under them and freeze where they land.
        var gravity = station.AddComponent<Factory.ReleasedPartGravity>();
        gravity.partsRoot = partsRoot;
        gravity.restGap = RestGap;
        gravity.attachedListener = UnityEngine.Object.FindFirstObjectByType<AttachedCollisionObjectListener>(FindObjectsInactive.Include);
        gravity.trajectoryPlayer = UnityEngine.Object.FindFirstObjectByType<MTCTrajectoryPlayer>(FindObjectsInactive.Include);
        Line($"ReleasedPartGravity on the station (listener {(gravity.attachedListener != null ? "wired" : "found at runtime")}, player {(gravity.trajectoryPlayer != null ? "wired" : "found at runtime")})");

        GameObject doorMenu = BuildDoorMenu(scene, doors, vise, gravity);
        Line($"Door menu '{doorMenu.name}' at {doorMenu.transform.position}");

        SetFloat(c.client, "placeHeightOffsetMeters", FactoryPlaceHeightOffset);

        // The reachability volume rides on the robot base (its meshes hang under BaseTransform);
        // the placement phase shows it with the baked fr3 map while the robot is moved.
        GameObject reach = scene.GetRootGameObjects().FirstOrDefault(r => r.name == ReachVolumeName)
                           ?? throw new InvalidOperationException($"FactoryDemo has no '{ReachVolumeName}' root");
        var visualizer = Require<ReachabilityVolumeVisualizer>(reach);
        var reachProfile = Require<RobotReachProfile>(c.fr3);
        if (reachProfile.reachabilityMap == null)
            throw new InvalidOperationException("fr3's RobotReachProfile has no baked reachability map (Assets/Resources/ReachabilityMaps/fr3_panda_arm.bytes)");
        reach.SetActive(true);
        SetRef(visualizer, "robot", c.fr3);
        SetBool(visualizer, "useBakedMap", true);
        SetBool(visualizer, "useRosMap", true);
        Line($"{ReachVolumeName} active on the fr3 with map '{reachProfile.reachabilityMap.name}'");

        var reachTargets = new List<Transform> { fixture.Find("DropAnchor") };
        reachTargets.AddRange(stockSlots);
        reachTargets.Add(conveyor.Find("DropAnchor"));
        BuildPlacementStage(scene, c, pedestal, visualizer, doorMenu, reachTargets);

        // The XR dashboard used to float where the machine now stands; park it beside the machine.
        GameObject dashboardRoot = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "XRDashboard");
        UIDocument dashboardDoc = dashboardRoot != null ? dashboardRoot.GetComponentInChildren<UIDocument>(true) : null;
        if (dashboardDoc != null)
        {
            dashboardDoc.transform.position = XrDashboardPosition;
            Line($"XRDashboard moved to {XrDashboardPosition}");
        }

        EditorUtility.SetDirty(station);
        Line($"Cell built: '{MachineName}', '{DoorMenuName}', '{FactoryStationRoot}'");
    }

    private static Transform BuildConveyor(Transform root, Material belt, Material frame)
    {
        float length = ConveyorStartX - ConveyorEndX;
        float midX = (ConveyorStartX + ConveyorEndX) * 0.5f;
        Transform conveyor = Group("Conveyor", root, new Vector3(midX, ConveyorTopY, ConveyorZ));
        Box("Belt", conveyor, new Vector3(midX, ConveyorTopY - 0.01f, ConveyorZ), new Vector3(length, 0.02f, ConveyorWidth), belt);
        Box("Bed", conveyor, new Vector3(midX, ConveyorTopY - 0.07f, ConveyorZ), new Vector3(length, 0.1f, ConveyorWidth - 0.02f), frame);
        foreach (float side in new[] { -1f, 1f })
            Box("Rail", conveyor, new Vector3(midX, ConveyorTopY - 0.03f, ConveyorZ + side * (ConveyorWidth * 0.5f + 0.01f)), new Vector3(length, 0.1f, 0.02f), frame);
        float legHeight = ConveyorTopY - 0.12f;
        foreach (float x in new[] { ConveyorStartX - 0.12f, ConveyorEndX + 0.12f })
            foreach (float side in new[] { -1f, 1f })
                Box("Leg", conveyor, new Vector3(x, legHeight * 0.5f, ConveyorZ + side * (ConveyorWidth * 0.5f - 0.03f)), new Vector3(0.05f, legHeight, 0.05f), frame);
        // Where the robot sets finished parts down: the belt's near end.
        Group("DropAnchor", conveyor, new Vector3(ConveyorStartX - 0.15f, ConveyorTopY + RestGap + BilletSize.y * 0.5f, ConveyorZ));
        return conveyor;
    }

    // The door menu is an instance of the planning-menu prefab with its document swapped for
    // MachineDoorPanel.uxml -- the same recipe as the MTCMenu dashboard -- so it carries the
    // world-space UI Toolkit, poke and grab stack that already works on the headset.
    private static GameObject BuildDoorMenu(Scene scene, Factory.MachineDoors doors, Factory.MachineVise vise, Factory.ReleasedPartGravity gravity)
    {
        (GameObject menu, UIDocument doc) = BuildPanelMenu(scene, DoorMenuName, DoorPanelUxmlPath, DoorPanelHeight, DoorMenuPosition);
        var panel = doc.gameObject.AddComponent<Factory.MachineDoorPanel>();
        SetRef(panel, "uiDocument", doc);
        SetRef(panel, "doors", doors);
        SetRef(panel, "vise", vise);
        SetRef(panel, "gravity", gravity);
        return menu;
    }

    // The self-centring vise: an unscaled group (the 'table' compound root the validator and
    // the publisher walk) holding scaled cubes. Each jaw is its own unscaled group with a body
    // and a plate so the SlidingPart moves both; authored OPEN, openOffset points inward.
    private static (Transform fixture, Factory.MachineVise vise) BuildVise(Transform root, Vector3 baseCentre, Material body, Material plate,
                                                                            Material parallels, Material lamp, CollisionObjectPublisher tablePub)
    {
        Transform fixture = Group("MachineFixture", root, baseCentre);
        Box("Base", fixture, baseCentre, ViseBaseSize, body);
        float baseTop = baseCentre.y + ViseBaseSize.y * 0.5f;
        Box("Parallels", fixture, new Vector3(baseCentre.x, baseTop + ViseParallelsSize.y * 0.5f, baseCentre.z), ViseParallelsSize, parallels);
        float parallelsTop = baseTop + ViseParallelsSize.y;
        float plateHeight = ViseJawSize.y - ViseParallelsSize.y;

        var jaws = new Factory.SlidingPart[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f; // -z faces the visitor
            float faceZ = baseCentre.z + side * ViseOpenGap * 0.5f;
            Transform jaw = Group(i == 0 ? "JawFront" : "JawBack", fixture, new Vector3(baseCentre.x, baseTop, faceZ));
            Box("Body", jaw, new Vector3(baseCentre.x, baseTop + ViseJawSize.y * 0.5f, faceZ + side * (ViseJawPlateThickness + ViseJawSize.z * 0.5f)), ViseJawSize, body);
            Box("Plate", jaw, new Vector3(baseCentre.x, parallelsTop + plateHeight * 0.5f, faceZ + side * ViseJawPlateThickness * 0.5f),
                new Vector3(ViseJawSize.x, plateHeight, ViseJawPlateThickness), plate);
            var slide = jaw.gameObject.AddComponent<Factory.SlidingPart>();
            slide.openOffset = new Vector3(0f, 0f, -side * ViseOpenGap * 0.5f);
            slide.travelTime = 1.2f;
            slide.openAmount = 0f;
            jaws[i] = slide;
        }

        Transform window = Group("JawWindow", fixture, new Vector3(baseCentre.x, parallelsTop, baseCentre.z));
        Group("DropAnchor", fixture, new Vector3(baseCentre.x, parallelsTop + RestGap + BilletSize.y * 0.5f, baseCentre.z));

        float frontZ = baseCentre.z - ViseBaseSize.z * 0.5f - ViseLampSize * 0.5f;
        GameObject presentLamp = Box("PartPresentLamp", fixture, new Vector3(baseCentre.x - 0.045f, baseCentre.y, frontZ), Vector3.one * ViseLampSize, lamp);
        GameObject clampLamp = Box("ClampedLamp", fixture, new Vector3(baseCentre.x - 0.025f, baseCentre.y, frontZ), Vector3.one * ViseLampSize, lamp);
        foreach (GameObject l in new[] { presentLamp, clampLamp })
            UnityEngine.Object.DestroyImmediate(l.GetComponent<Collider>());
        // Air block on the side away from the robot (nothing behind the vise: the machine's
        // collision mesh rises just past the platter's back edge).
        Box("AirBlock", fixture, new Vector3(baseCentre.x + ViseBaseSize.x * 0.5f + 0.01f, baseCentre.y, baseCentre.z + 0.05f), new Vector3(0.02f, 0.02f, 0.03f), body);

        var vise = fixture.gameObject.AddComponent<Factory.MachineVise>();
        vise.jaws = jaws;
        vise.jawWindow = window;
        vise.openGap = ViseOpenGap;
        vise.jawWidth = ViseJawSize.x;
        vise.jawHeight = plateHeight;
        vise.requiredStickout = ViseRequiredStickout;
        vise.presentLamp = presentLamp.GetComponent<Renderer>();
        vise.clampLamp = clampLamp.GetComponent<Renderer>();
        vise.republishOnMove = tablePub;
        vise.machineName = MachineName;
        return (fixture, vise);
    }

    // A world-space panel on an instance of the planning-menu prefab (the recipe the MTCMenu
    // dashboard uses): the document swapped for 'uxmlPath', the inherited planning UI disabled,
    // the poke collider sized to the panel, the whole thing turned toward the visitor's spot.
    private static (GameObject menu, UIDocument doc) BuildPanelMenu(Scene scene, string name, string uxmlPath, float heightUnits, Vector3 position)
    {
        GameObject menuPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlanningMenuPrefabPath)
                                ?? throw new InvalidOperationException($"{PlanningMenuPrefabPath} missing");
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath)
                   ?? throw new InvalidOperationException($"{uxmlPath} missing");
        var menu = (GameObject)PrefabUtility.InstantiatePrefab(menuPrefab, scene);
        menu.name = name;
        var planningUi = menu.GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
        if (planningUi != null) planningUi.enabled = false;
        UIDocument doc = menu.GetComponentInChildren<UIDocument>(true)
                         ?? throw new InvalidOperationException("planning menu prefab has no UIDocument");
        doc.visualTreeAsset = uxml;
        var so = new SerializedObject(doc);
        SetNumber(Prop(so, "m_WorldSpaceWidth"), 300f);
        SetNumber(Prop(so, "m_WorldSpaceHeight"), heightUnits);
        so.ApplyModifiedPropertiesWithoutUndo();
        // The document's position is the panel's top-left corner (1 unit = 1 cm before the root's
        // 0.2 scale); the panel hangs down to just above the grab handle at the prefab root, and
        // the poke collider covers exactly the panel.
        doc.transform.localPosition = new Vector3(-1.5f, heightUnits / 100f + 0.2f, 0f);
        var collider = doc.GetComponent<BoxCollider>();
        if (collider != null)
        {
            collider.center = new Vector3(1.5f, -heightUnits / 200f, 0f);
            collider.size = new Vector3(3f, heightUnits / 100f, 0.4f);
        }
        // A world-space document faces its local -z; turn that toward the visitor's spot.
        Vector3 toVisitor = new Vector3(-position.x, 0f, -position.z).normalized;
        menu.transform.SetPositionAndRotation(position, Quaternion.LookRotation(-toVisitor, Vector3.up));
        return (menu, doc);
    }

    // ------------------------------------------------------------------ robot placement

    // The placement phase: floor handle (ring around the pedestal, XRI far/near grab sliding on
    // world x/z), reach markers on the anchors, the controller that moves robot + pedestal after
    // the handle and gates the MTC / door menus, its panel, and a desktop mouse rig for editor runs.
    private static void BuildPlacementStage(Scene scene, Composition c, GameObject pedestal, ReachabilityVolumeVisualizer visualizer,
                                            GameObject doorMenu, List<Transform> targets)
    {
        if (targets.Any(t => t == null))
            throw new InvalidOperationException("placement targets incomplete (fixture / conveyor DropAnchor missing)");

        // --- the handle ---------------------------------------------------------------------
        var handle = new GameObject(PlacementHandleName);
        SceneManager.MoveGameObjectToScene(handle, scene);
        handle.transform.SetPositionAndRotation(DefaultHandlePosition, Quaternion.identity);
        var rb = handle.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        // Transformers before the interactable so the serialized starting lists hold them in this
        // order (a baked interactable registers only what the lists name, see GrabbableCollisionObjectFactory).
        var general = handle.AddComponent<XRGeneralGrabTransformer>();
        general.permittedDisplacementAxes = XRGeneralGrabTransformer.ManipulationAxes.X | XRGeneralGrabTransformer.ManipulationAxes.Z;
        general.constrainedAxisDisplacementMode = XRGeneralGrabTransformer.ConstrainedAxisDisplacementMode.WorldAxisRelative;
        general.allowOneHandedScaling = false;
        general.allowTwoHandedScaling = false;
        var floorYaw = handle.AddComponent<XRGrabTransformerFloorYaw>();
        floorYaw.lockHeight = true;
        floorYaw.yawOnly = true;
        floorYaw.clampToBounds = true;
        floorYaw.boundsMin = PlacementBoundsMin;
        floorYaw.boundsMax = PlacementBoundsMax;
        // Added before the ring child: XRGrabInteractable.Reset adopts a lone rendered child as
        // its predicted-visuals transform, which the handle must not use.
        var grab = handle.AddComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.trackPosition = true;
        grab.trackRotation = false; // turning is done with the panel's buttons
        grab.trackScale = false;
        grab.throwOnDetach = false;
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.snapToColliderVolume = true;
        grab.selectMode = InteractableSelectMode.Single;
        grab.addDefaultGrabTransformers = false;
        grab.startingSingleGrabTransformers.Clear();
        grab.startingSingleGrabTransformers.Add(general);
        grab.startingSingleGrabTransformers.Add(floorYaw);
        grab.startingMultipleGrabTransformers.Clear();
        grab.startingMultipleGrabTransformers.Add(general);
        grab.startingMultipleGrabTransformers.Add(floorYaw);
        handle.AddComponent<DesktopDragHandle>();

        var ring = new GameObject("Ring");
        ring.transform.SetParent(handle.transform, false);
        ring.transform.localPosition = new Vector3(0f, HandleRingThickness * 0.5f, PedestalZ - DefaultHandlePosition.z);
        ring.AddComponent<MeshFilter>().sharedMesh = EnsureRingMesh();
        ring.AddComponent<MeshRenderer>().sharedMaterial = MakeMaterial("PlacementRing", new Color(0.2f, 0.8f, 1f), 0.4f, 0f);
        var ringCollider = ring.AddComponent<BoxCollider>();
        ringCollider.center = Vector3.zero;
        ringCollider.size = new Vector3(HandleRingOuterRadius * 2f, HandleRingThickness, HandleRingOuterRadius * 2f);
        ringCollider.isTrigger = false;      // triggers break the far-ray grab (NearFar ignores them)
        ringCollider.excludeLayers = ~0;     // rays and pokes hit it, physics never does
        grab.colliders.Clear();
        grab.colliders.Add(ringCollider);
        EditorUtility.SetDirty(grab);
        Line($"{PlacementHandleName} at {DefaultHandlePosition}: ring r {HandleRingInnerRadius}..{HandleRingOuterRadius} around the pedestal, bounds {PlacementBoundsMin}..{PlacementBoundsMax}");

        // --- reach markers (own root: a MeshFilter under the station would be published) ------
        var markersGo = new GameObject(ReachMarkersName);
        SceneManager.MoveGameObjectToScene(markersGo, scene);
        var markerSet = markersGo.AddComponent<Factory.ReachTargetMarkerSet>();
        SetRef(markerSet, "reachability", visualizer);
        SetRef(markerSet, "markerMaterial", MakeMaterial("ReachMarker", Color.white, 0.3f, 0f));
        SetArray(markerSet, "targets", targets.Cast<UnityEngine.Object>().ToArray());
        Line($"{ReachMarkersName}: {targets.Count} targets ({string.Join(", ", targets.Select(t => Path(t)))})");

        // --- the controller -------------------------------------------------------------------
        var placementRoot = new GameObject(PlacementRootName);
        SceneManager.MoveGameObjectToScene(placementRoot, scene);
        var controller = placementRoot.AddComponent<Factory.RobotPlacementController>();
        SetRef(controller, "robotRoot", c.fr3);
        SetRef(controller, "pedestal", pedestal.transform);
        SetRef(controller, "baseAnchor", c.baseTransform.transform);
        SetRef(controller, "handle", handle.transform);
        SetRef(controller, "handleInteractable", grab);
        SetRef(controller, "reachability", visualizer);
        SetRef(controller, "markers", markerSet);
        SetRef(controller, "pickPlaceClient", c.client);
        SetRef(controller, "trajectoryPlayer", Require<MTCTrajectoryPlayer>(c.fr3));
        SetRef(controller, "recorder", c.recorder != null ? c.recorder : FindInScene<PickPlaceTaskRecorder>(scene));
        SetRef(controller, "ghosts", Require<SpawnGhosts>(c.ghostsGo));
        SetArray(controller, "wristMenus", FindAllInScene<WristMenuController>(scene).Cast<UnityEngine.Object>().ToArray());
        SetArray(controller, "gatedObjects", new UnityEngine.Object[] { c.mtcMenu, doorMenu });
        SetArray(controller, "placementObjects", new UnityEngine.Object[] { handle, markersGo });
        SetVector2(controller, "cellBoundsMin", PlacementBoundsMin);
        SetVector2(controller, "cellBoundsMax", PlacementBoundsMax);
        SetVector2(controller, "startOffset", PlacementStartOffset);
        SetFloat(controller, "startYawDegrees", PlacementStartYaw);
        SetBool(controller, "startInPlacement", true);
        SetBool(controller, "hideReachabilityWhenConfirmed", true);
        Line($"Placement starts {PlacementStartOffset.magnitude:F2} m from the ideal spot (offset {PlacementStartOffset}, yaw {PlacementStartYaw})");
        var keys = placementRoot.AddComponent<Factory.RobotPlacementDesktopInput>();
        SetRef(keys, "controller", controller);
        keys.editorOnly = true;
        keys.onlyWithoutHeadset = true;
        // The gated menus are authored active; the controller turns them off at runtime until Confirm.
        c.mtcMenu.SetActive(true);
        doorMenu.SetActive(true);

        // --- the panel ------------------------------------------------------------------------
        (GameObject menu, UIDocument doc) = BuildPanelMenu(scene, PlacementMenuName, PlacementPanelUxmlPath, PlacementPanelHeight, PlacementMenuPosition);
        var panel = doc.gameObject.AddComponent<Factory.RobotPlacementPanel>();
        SetRef(panel, "uiDocument", doc);
        SetRef(panel, "controller", controller);
        Line($"Placement menu '{menu.name}' at {menu.transform.position}");

        // --- desktop stand-in (editor only; inert with a headset) -----------------------------
        if (scene.GetRootGameObjects().All(r => r.name != DesktopRigName))
        {
            var rig = new GameObject(DesktopRigName);
            SceneManager.MoveGameObjectToScene(rig, scene);
            var mouse = rig.AddComponent<DesktopMouseInteractor>();
            mouse.editorOnly = true;
            mouse.onlyWithoutHeadset = true;
            mouse.startPosition = new Vector3(0f, 1.6f, -0.8f);
            mouse.lookAt = new Vector3(Fr3BaseX * 0.5f, 0.8f, 1f);
            Line($"{DesktopRigName} added (editor-only mouse drag of the handle)");
        }
    }

    // Flat annulus on the floor: top face plus outer and inner walls. Created once as an asset.
    private static Mesh EnsureRingMesh()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(PlacementRingMeshPath);
        if (existing != null) return existing;
        const int segments = 64;
        float ri = HandleRingInnerRadius, ro = HandleRingOuterRadius, h = HandleRingThickness * 0.5f;
        var verts = new List<Vector3>();
        var tris = new List<int>();
        void Quad(int a, int b, int c, int d) { tris.AddRange(new[] { a, b, c, a, c, d }); }
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
            Vector3 d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
            int b = verts.Count;
            // 0..3 top face, 4..7 outer wall, 8..11 inner wall (separate vertices so the normals stay hard)
            verts.AddRange(new[] { d0 * ri + Vector3.up * h, d0 * ro + Vector3.up * h, d1 * ro + Vector3.up * h, d1 * ri + Vector3.up * h });
            verts.AddRange(new[] { d0 * ro + Vector3.up * h, d0 * ro - Vector3.up * h, d1 * ro - Vector3.up * h, d1 * ro + Vector3.up * h });
            verts.AddRange(new[] { d0 * ri - Vector3.up * h, d0 * ri + Vector3.up * h, d1 * ri + Vector3.up * h, d1 * ri - Vector3.up * h });
            Quad(b + 0, b + 3, b + 2, b + 1);
            Quad(b + 4, b + 7, b + 6, b + 5);
            Quad(b + 8, b + 11, b + 10, b + 9);
        }
        var mesh = new Mesh { name = "PlacementRing" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string folder = System.IO.Path.GetDirectoryName(PlacementRingMeshPath);
        if (!AssetDatabase.IsValidFolder(folder))
            System.IO.Directory.CreateDirectory(folder);
        AssetDatabase.CreateAsset(mesh, PlacementRingMeshPath);
        Line($"Created {PlacementRingMeshPath}");
        return mesh;
    }

    private static void SetArray(Component target, string field, UnityEngine.Object[] values)
    {
        var so = new SerializedObject(target);
        SerializedProperty p = Prop(so, field);
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        so.ApplyModifiedPropertiesWithoutUndo();
        Line($"{Path(target.transform)}.{target.GetType().Name}.{field} = [{string.Join(", ", values.Select(Describe))}]");
    }

    private static void SetVector2(Component target, string field, Vector2 value)
    {
        var so = new SerializedObject(target);
        Prop(so, field).vector2Value = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static List<UnityEngine.Object> ArrayRefs(Component target, string field)
    {
        SerializedProperty p = new SerializedObject(target).FindProperty(field);
        var list = new List<UnityEngine.Object>();
        for (int i = 0; p != null && i < p.arraySize; i++)
            list.Add(p.GetArrayElementAtIndex(i).objectReferenceValue);
        return list;
    }

    // ------------------------------------------------------------------ VF-2 prefab

    // Builds Assets/Prefabs/Factory/HaasVF2.prefab from the two FBX files bl_assemble.py and
    // bl_export_urdf.py write (visual parts + decimated collision meshes), or returns the
    // existing prefab. Delete the prefab to rebuild it.
    private static GameObject EnsureVf2Prefab()
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(Vf2PrefabPath);
        if (existing != null)
        {
            Line($"Using existing {Vf2PrefabPath}");
            return existing;
        }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Vf2ModelPath) == null || AssetDatabase.LoadAssetAtPath<GameObject>(Vf2CollisionModelPath) == null)
            throw new InvalidOperationException($"{Vf2ModelPath} / {Vf2CollisionModelPath} missing: run the Blender pipeline in Docs/factory_machines (README)");

        ConfigureMachineImport(Vf2ModelPath, readable: false, materials: true);
        ConfigureMachineImport(Vf2CollisionModelPath, readable: true, materials: false);
        ExtractMachineMaterials(Vf2ModelPath, Vf2MaterialPrefix);

        GameObject visual = InstantiateModel(Vf2ModelPath, out AxisMap map);
        GameObject collision = InstantiateModel(Vf2CollisionModelPath, out AxisMap colMap);
        if ((map.x - colMap.x).magnitude > 1e-3f || (map.y - colMap.y).magnitude > 1e-3f || (map.z - colMap.z).magnitude > 1e-3f)
            throw new InvalidOperationException($"visual and collision FBX axis maps differ: {map} vs {colMap}");
        visual.name = Vf2MaterialPrefix;
        Line($"{Vf2MaterialPrefix} axis map {map}");

        Transform doorLeft = FindChild(visual, "DoorLeft"), doorRight = FindChild(visual, "DoorRight"), body = FindChild(visual, "Static");
        Transform trunnion = FindChild(visual, "Trunnion"), platter = FindChild(visual, "Platter");
        var left = doorLeft.gameObject.AddComponent<Factory.SlidingPart>();
        left.openOffset = map.V(-DoorTravel, 0f, 0f);
        left.travelTime = 1.8f;
        var right = doorRight.gameObject.AddComponent<Factory.SlidingPart>();
        right.openOffset = map.V(DoorTravel, 0f, 0f);
        right.travelTime = 1.8f;

        // Collision meshes ride on their visual parts (same pivots), renderer-less, each with its
        // own planning-scene object so a sliding door MOVEs in MoveIt as it moves here.
        AttachCollision(collision, "base_link_col", body, Vf2BodyId, 0.5f);
        AttachCollision(collision, "door_left_col", doorLeft, Vf2DoorLeftId, 2f);
        AttachCollision(collision, "door_right_col", doorRight, Vf2DoorRightId, 2f);
        AttachCollision(collision, "trunnion_col", trunnion, Vf2TrunnionId, 0.5f);
        AttachCollision(collision, "platter_col", platter, Vf2PlatterId, 0.5f);
        UnityEngine.Object.DestroyImmediate(collision);

        var doors = visual.AddComponent<Factory.MachineDoors>();
        doors.doors = new[] { left, right };
        doors.machineName = MachineName;

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(visual, Vf2PrefabPath, out bool ok);
        if (!ok) throw new InvalidOperationException($"could not save {Vf2PrefabPath}");
        UnityEngine.Object.DestroyImmediate(visual);
        Line($"Saved {Vf2PrefabPath}");
        return prefab;
    }

    private static void ConfigureMachineImport(string path, bool readable, bool materials)
    {
        var imp = AssetImporter.GetAtPath(path) as ModelImporter
                  ?? throw new InvalidOperationException($"no ModelImporter for {path}");
        imp.materialImportMode = materials ? ModelImporterMaterialImportMode.ImportStandard : ModelImporterMaterialImportMode.None;
        imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
        imp.importAnimation = false;
        imp.animationType = ModelImporterAnimationType.None;
        imp.importCameras = false;
        imp.importLights = false;
        imp.isReadable = readable;
        imp.addCollider = false;
        imp.meshCompression = ModelImporterMeshCompression.Off;
        imp.importNormals = ModelImporterNormals.Import;
        imp.weldVertices = true;
        imp.generateSecondaryUV = false;
        imp.importBlendShapes = false;
        imp.useFileScale = true;
        imp.globalScale = 1f;
        imp.SaveAndReimport();
        Line($"Imported {path} (readable={readable}, materials={materials})");
    }

    // CAD surfaces are single sheets seen from both sides, so the embedded materials are
    // extracted to Assets/Materials/Factory/Machines and rendered double-sided (the fix the
    // earlier machines needed); the window material maps to the shared MachineGlass.
    private static void ExtractMachineMaterials(string fbxPath, string prefix)
    {
        var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter
                       ?? throw new InvalidOperationException($"no ModelImporter for {fbxPath}");
        Material glass = AssetDatabase.LoadAssetAtPath<Material>(MachineGlassMaterialPath)
                         ?? throw new InvalidOperationException($"{MachineGlassMaterialPath} missing");
        if (!AssetDatabase.IsValidFolder(MachineMaterialFolder))
            AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(MachineMaterialFolder).Replace('\\', '/'), System.IO.Path.GetFileName(MachineMaterialFolder));
        int extracted = 0;
        foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath))
        {
            if (!(sub is Material m)) continue;
            var id = new AssetImporter.SourceAssetIdentifier(m);
            if (m.name == "Glass")
            {
                importer.AddRemap(id, glass);
                continue;
            }
            string path = $"{MachineMaterialFolder}/{prefix}_{m.name}.mat";
            Material already = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (already != null)
            {
                importer.AddRemap(id, already);
                continue;
            }
            string error = AssetDatabase.ExtractAsset(m, path);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"ExtractAsset {m.name}: {error}");
            extracted++;
        }
        AssetDatabase.WriteImportSettingsIfDirty(fbxPath);
        AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
        int doubleSided = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { MachineMaterialFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!System.IO.Path.GetFileName(path).StartsWith(prefix + "_")) continue;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || !mat.HasProperty("_Cull")) continue;
            mat.SetFloat("_Cull", 0f);
            EditorUtility.SetDirty(mat);
            doubleSided++;
        }
        AssetDatabase.SaveAssets();
        Line($"{fbxPath}: {extracted} materials extracted, {doubleSided} double-sided, Glass -> MachineGlass");
    }

    private static GameObject InstantiateModel(string path, out AxisMap map)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path) ?? throw new InvalidOperationException($"{path} missing");
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        map = new AxisMap
        {
            x = FindChild(inst, "Axis_X").localPosition,
            y = FindChild(inst, "Axis_Y").localPosition,
            z = FindChild(inst, "Axis_Z").localPosition,
        };
        foreach (string a in new[] { "Axis_X", "Axis_Y", "Axis_Z" })
            UnityEngine.Object.DestroyImmediate(FindChild(inst, a).gameObject);
        return inst;
    }

    private static void AttachCollision(GameObject collisionRoot, string meshName, Transform parent, string objectId, float rateHz)
    {
        Transform col = FindChild(collisionRoot, meshName);
        var mf = col.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) throw new InvalidOperationException($"{meshName} has no mesh");
        if (!mf.sharedMesh.isReadable) throw new InvalidOperationException($"{meshName} mesh is not readable; the collision FBX must import with Read/Write enabled");
        col.SetParent(parent, true);
        col.name = "Collision";
        var renderer = col.GetComponent<MeshRenderer>();
        if (renderer != null) UnityEngine.Object.DestroyImmediate(renderer);
        var pub = col.gameObject.AddComponent<CollisionObjectPublisher>();
        pub.objectId = objectId;
        pub.isMesh = true;
        pub.frameId = "world";
        pub.publishRateHz = rateHz;
        Line($"'{objectId}': {mf.sharedMesh.triangles.Length / 3} triangles under {Path(parent)} (local pos {col.localPosition})");
    }

    private static Transform FindChild(GameObject root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        throw new InvalidOperationException($"child '{name}' not found under '{root.name}'");
    }

    private static void SetNumber(SerializedProperty p, float value)
    {
        if (p.propertyType == SerializedPropertyType.Integer) p.intValue = Mathf.RoundToInt(value);
        else p.floatValue = value;
    }

    private static Transform Group(string name, Transform parent, Vector3 position)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        return go.transform;
    }

    private static GameObject Box(string name, Transform parent, Vector3 centre, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = centre;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    private static Bounds RendererBounds(Transform t)
    {
        Renderer[] renderers = t.GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0) return new Bounds(t.position, Vector3.zero);
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    private static Material MakeMaterial(string name, Color color, float smoothness, float metallic)
    {
        string path = $"{FactoryMaterialFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? throw new InvalidOperationException("URP Lit shader not found");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        mat.SetFloat("_Metallic", metallic);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void SetFloat(Component target, string field, float value)
    {
        var so = new SerializedObject(target);
        Prop(so, field).floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        Line($"{Path(target.transform)}.{target.GetType().Name}.{field} = {value}");
    }

    // ------------------------------------------------------------------ factory validation

    // Opens FactoryDemo and checks the wiring the VF-2 cell relies on. Returns the failure count.
    public static int ValidateFactory()
    {
        Scene scene = EditorSceneManager.OpenScene(FactoryScenePath, OpenSceneMode.Single);
        int failures = 0;
        void Check(bool ok, string what)
        {
            Line((ok ? "[OK]   " : "[FAIL] ") + what);
            if (!ok) failures++;
        }
        GameObject[] roots = scene.GetRootGameObjects();
        GameObject R(string n) => roots.FirstOrDefault(r => r.name == n);

        GameObject fr3 = R("fr3"), ghost = R("fr3 (1)"), manager = R("MTCManager"), menu = R("MTCMenu"), station = R(FactoryStationRoot);
        GameObject machines = R("Machines"), doorMenu = R(DoorMenuName), pedestal = R("RobotPedestal");
        Check(fr3 != null && fr3.activeSelf, "root 'fr3' present and active");
        Check(ghost != null && !ghost.activeSelf, "root 'fr3 (1)' present and inactive");
        Check(manager != null, "root 'MTCManager' present");
        Check(menu != null, "root 'MTCMenu' present");
        Check(station != null, $"root '{FactoryStationRoot}' present");
        Check(machines != null, "root 'Machines' present");
        Check(doorMenu != null, $"root '{DoorMenuName}' present");
        Check(pedestal != null, "root 'RobotPedestal' present");
        Check(R("ur5e_robot") == null, "ur5e_robot removed");
        GameObject reach = R(ReachVolumeName);
        Check(reach != null && reach.activeSelf, $"root '{ReachVolumeName}' present and active");
        foreach (string n in new[] { "XR Interaction Manager", "EventSystem", "XR UI Toolkit Manager" })
            Check(R(n) != null, $"UI input root '{n}' present");
        Check(FindAllInScene<PanelInputConfiguration>(scene).Any(p => p.gameObject.activeInHierarchy), "an active PanelInputConfiguration exists");
        if (failures > 0 && (fr3 == null || station == null || machines == null)) { Debug.Log(Log.ToString()); return failures; }

        Transform baseTransform = fr3.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BaseTransform");
        Check(baseTransform != null, "BaseTransform is a child of fr3");
        if (baseTransform != null)
        {
            Check(Mathf.Abs(Mathf.DeltaAngle(baseTransform.localEulerAngles.y, -90f)) < 0.5f, "BaseTransform local yaw is -90");
            Check(Vector3.Distance(baseTransform.position, Fr3BasePosition) < 0.01f, $"BaseTransform world position is {Fr3BasePosition}");
        }
        GameObject origin = baseTransform != null ? baseTransform.gameObject : null;

        var profile = fr3.GetComponent<RobotReachProfile>();
        Check(profile != null && profile.planningGroupName == Fr3PlanningGroup && profile.planningFrameId == Fr3PlanningFrame
              && profile.unityJointNamePrefix == Fr3UnityJointPrefix && profile.rosJointNamePrefix == Fr3RosJointPrefix
              && profile.baseAnchor == baseTransform, "RobotReachProfile on fr3 has the panda planning names and baseAnchor");

        Check(profile != null && profile.reachabilityMap != null, "RobotReachProfile carries a baked reachability map");
        var ik = FindInScene<DirectArticulationIKController>(scene);
        Check(ik != null && RefIs(ik, "robotRoot", fr3.transform) && RefName(ik, "endEffector") == Fr3EndEffectorLink, "IK controller targets fr3 / fr3_hand_tcp");
        var ghosts = FindInScene<SpawnGhosts>(scene);
        Check(ghosts != null && ghosts.realRobot == fr3 && ghosts.robotPrefab == ghost, "SpawnGhosts realRobot/robotPrefab are the fr3 and its ghost");
        var listener = FindInScene<CollisionObjectsListenerSimple>(scene);
        Check(listener != null && listener.worldOrigin == origin, "CollisionObjectsListenerSimple.worldOrigin is BaseTransform");
        var attached = FindInScene<AttachedCollisionObjectListener>(scene);
        Check(attached != null && RefSet(attached, "sceneListener") && RefSet(attached, "ikController") && RefSet(attached, "worldOrigin"), "AttachedCollisionObjectListener wired");
        var recorder = FindInScene<PickPlaceTaskRecorder>(scene);
        Check(recorder != null && RefSet(recorder, "selectionManager") && RefSet(recorder, "worldOrigin") && RefSet(recorder, "pickPlaceAction"), "PickPlaceTaskRecorder wired");
        var client = FindInScene<PickPlaceClient>(scene);
        Check(client != null && Mathf.Abs(new SerializedObject(client).FindProperty("placeHeightOffsetMeters").floatValue - FactoryPlaceHeightOffset) < 1e-4f, "PickPlaceClient place height offset set");
        var dashboard = FindInScene<MTCDashboardPanel>(scene);
        Check(dashboard != null && RefSet(dashboard, "pickPlaceRecorder") && RefSet(dashboard, "pickPlaceAction") && RefSet(dashboard, "trajectoryPlayer"), "MTCDashboardPanel wired");
        var player = FindInScene<MTCTrajectoryPlayer>(scene);
        Check(player != null && RefSet(player, "ikController") && RefSet(player, "sceneListener"), "MTCTrajectoryPlayer wired");
        var planningMenu = FindInScene<MoveItPlanningRequestMenuUI>(scene);
        Check(planningMenu != null && new SerializedObject(planningMenu).FindProperty("planningGroupName").stringValue == Fr3PlanningGroup, "planning menu group is panda_arm");
        int wristCount = 0;
        foreach (WristMenuController wrist in FindAllInScene<WristMenuController>(scene))
        {
            wristCount++;
            var so = new SerializedObject(wrist);
            Check(so.FindProperty("enableMTC").boolValue && RefSet(wrist, "pickPlaceRecorder") && RefSet(wrist, "mtcDashboardPanel")
                  && RefSet(wrist, "selectionManager") && RefSet(wrist, "collisionObjectsListener") && RefSet(wrist, "worldOrigin"),
                  $"wrist menu '{Path(wrist.transform)}' MTC-enabled and wired");
        }
        Check(wristCount > 0, "at least one WristMenuController");

        // --- the machine ----------------------------------------------------------------
        Check(machines.transform.childCount == 1, $"'Machines' holds exactly one machine (has {machines.transform.childCount})");
        Transform machineT = machines.transform.childCount > 0 ? machines.transform.GetChild(0) : null;
        Check(machineT != null && machineT.name == MachineName, $"the machine is '{MachineName}'");
        var doors = machineT != null ? machineT.GetComponent<Factory.MachineDoors>() : null;
        Check(doors != null && doors.doors != null && doors.doors.Length == 2 && doors.doors.All(d => d != null), "MachineDoors drives two SlidingParts");
        Bounds machineBounds = machineT != null ? RendererBounds(machineT) : new Bounds();
        Check(machineT != null && Mathf.Abs(machineBounds.min.y) < 0.03f, $"machine stands on the floor (min y {machineBounds.min.y:F3})");
        Bounds doorsClosed = new Bounds(), doorsOpen = new Bounds();
        float openingWidth = 0f;
        if (doors != null && doors.doors != null && doors.doors.Length == 2 && doors.doors.All(d => d != null))
        {
            Check(doors.doors.All(d => d.openAmount < 1e-4f), "doors are authored closed");
            Check(doors.doors.All(d => Mathf.Abs(d.openOffset.magnitude - DoorTravel) < 1e-3f)
                  && Vector3.Dot(doors.doors[0].openOffset, doors.doors[1].openOffset) < 0f, $"doors travel {DoorTravel} m in opposite directions");
            Bounds l = RendererBounds(doors.doors[0].transform), r = RendererBounds(doors.doors[1].transform);
            doorsClosed = l; doorsClosed.Encapsulate(r);
            Check(Mathf.Abs(doorsClosed.min.z - DoorPlaneZ) < 0.01f, $"door front at z = {DoorPlaneZ} (is {doorsClosed.min.z:F3})");
            Check(Mathf.Abs(doorsClosed.center.x - MachineXOffset) < 0.02f, $"door opening centre at x = {MachineXOffset} (is {doorsClosed.center.x:F3})");
            Check(doorsClosed.center.z < machineBounds.center.z, "doors are on the visitor side of the machine");
            // Pose the doors open in memory (never saved) to measure the opening.
            foreach (Factory.SlidingPart d in doors.doors) d.Set(1f);
            Bounds lo = RendererBounds(doors.doors[0].transform), ro = RendererBounds(doors.doors[1].transform);
            doorsOpen = lo; doorsOpen.Encapsulate(ro);
            Bounds leftDoor = lo.center.x < ro.center.x ? lo : ro, rightDoor = lo.center.x < ro.center.x ? ro : lo;
            openingWidth = rightDoor.min.x - leftDoor.max.x;
            Check(openingWidth > 0.8f, $"open doors leave a {openingWidth:F2} m opening");
            Check(doorsOpen.min.x > machineBounds.min.x - 0.01f && doorsOpen.max.x < machineBounds.max.x + 0.01f, "open doors stay within the enclosure width");
            foreach (Factory.SlidingPart d in doors.doors) d.Set(0f);
        }
        // Visibility: every part of the machine renders (CAD surfaces are single sheets, so each
        // material must be double-sided), and no import truncated a mesh.
        if (machineT != null)
        {
            var renderers = machineT.GetComponentsInChildren<MeshRenderer>(true);
            Check(renderers.Length == 5, $"machine has 5 renderers: Static, two doors, trunnion, platter (has {renderers.Length})");
            foreach (MeshRenderer r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                Check(r.enabled && r.gameObject.activeInHierarchy, $"renderer '{Path(r.transform)}' enabled");
                Check(mesh != null && mesh.subMeshCount == r.sharedMaterials.Length, $"'{r.name}' has one material per submesh ({mesh?.subMeshCount} / {r.sharedMaterials.Length})");
                Check(r.sharedMaterials.All(m => m != null && m.shader != null && !m.shader.name.Contains("InternalErrorShader")), $"'{r.name}' materials all assigned");
                foreach (Material m in r.sharedMaterials.Where(m => m != null).Distinct())
                {
                    bool glass = m.name == "MachineGlass";
                    Check(glass || (m.HasProperty("_Cull") && m.GetFloat("_Cull") == 0f), $"'{r.name}' material '{m.name}' is double-sided");
                }
                if (mesh != null)
                {
                    int tris = 0;
                    for (int i = 0; i < mesh.subMeshCount; i++) tris += (int)mesh.GetIndexCount(i) / 3;
                    Check(mesh.vertexCount > 0 && tris > 0, $"'{r.name}' mesh has {mesh.vertexCount} vertices / {tris} triangles");
                    Check(mesh.vertexCount <= 65535 || mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32, $"'{r.name}' mesh index format fits its vertex count");
                }
            }
            long totalTris = renderers.Select(r => r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m != null)
                .Sum(m => { long t = 0; for (int i = 0; i < m.subMeshCount; i++) t += (long)m.GetIndexCount(i) / 3; return t; });
            Check(totalTris > 400000 && totalTris < 480000, $"machine triangle count matches the Blender export (~441k; is {totalTris})");
        }
        var machinePubs = machineT != null ? machineT.GetComponentsInChildren<CollisionObjectPublisher>(true) : new CollisionObjectPublisher[0];
        foreach (string id in new[] { Vf2BodyId, Vf2DoorLeftId, Vf2DoorRightId, Vf2TrunnionId, Vf2PlatterId })
        {
            CollisionObjectPublisher pub = machinePubs.FirstOrDefault(p => p.objectId == id);
            var mf = pub != null ? pub.GetComponent<MeshFilter>() : null;
            Check(pub != null && pub.isMesh && pub.worldOrigin == origin && mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable,
                  $"machine collision object '{id}' is a readable mesh publisher with the world origin");
        }
        if (doors != null && doors.doors != null && doors.doors.Length == 2)
            Check(machinePubs.Any(p => p.objectId == Vf2DoorLeftId && p.transform.IsChildOf(doors.doors[0].transform) || p.objectId == Vf2DoorLeftId && p.transform.IsChildOf(doors.doors[1].transform))
                  && machinePubs.Any(p => p.objectId == Vf2DoorRightId && (p.transform.IsChildOf(doors.doors[0].transform) || p.transform.IsChildOf(doors.doors[1].transform))),
                  "door collision publishers ride on the door parts");

        // --- the door menu --------------------------------------------------------------
        var doorPanel = doorMenu != null ? doorMenu.GetComponentInChildren<Factory.MachineDoorPanel>(true) : null;
        Check(doorPanel != null && doorPanel.Doors == doors && doors != null, "MachineDoorPanel targets the machine's MachineDoors");
        var doorDoc = doorMenu != null ? doorMenu.GetComponentInChildren<UIDocument>(true) : null;
        Check(doorDoc != null && doorDoc.visualTreeAsset != null && doorDoc.visualTreeAsset.name == "MachineDoorPanel", "door menu document shows MachineDoorPanel.uxml");
        if (doorMenu != null && machineT != null)
        {
            Vector3 frontRight = new Vector3(machineBounds.max.x, doorMenu.transform.position.y, DoorPlaneZ);
            float d = Vector3.Distance(doorMenu.transform.position, frontRight);
            Check(d < 0.8f && doorMenu.transform.position.z < DoorPlaneZ, $"door menu just outside the machine's front corner ({d:F2} m)");
        }
        var planningUis = doorMenu != null ? doorMenu.GetComponentsInChildren<MoveItPlanningRequestMenuUI>(true) : new MoveItPlanningRequestMenuUI[0];
        Check(planningUis.All(u => !u.enabled), "door menu's inherited planning UI is disabled");

        // --- the station ----------------------------------------------------------------
        var billets = station.GetComponent<Factory.GrabbablePartSet>();
        Check(billets != null && billets.slots != null && billets.slots.Length == BilletCount && billets.slots.All(s => s != null)
              && billets.worldOrigin == origin && billets.listener != null && billets.partsRoot != null, $"GrabbablePartSet wired ({BilletCount} stock slots)");
        Check(station.GetComponent<Factory.MoldedPartSpawner>() == null && station.GetComponent<Factory.PickPlaceStationController>() == null, "no moulder-era components on the station");
        if (billets != null && billets.slots != null)
        {
            Check(billets.prePlaced != null && billets.prePlaced.Length == billets.slots.Length && billets.prePlaced.All(p => p != null), "a baked billet per stock slot");
            for (int i = 0; billets.prePlaced != null && i < billets.prePlaced.Length; i++)
            {
                GameObject p = billets.prePlaced[i];
                var pub = p != null ? p.GetComponent<CollisionObjectPublisher>() : null;
                Check(pub != null && pub.objectId == $"{billets.idPrefix}{i + 1}" && pub.worldOrigin == origin && !pub.hasBeenPublished, $"billet {i + 1} publishes as '{billets.idPrefix}{i + 1}'");
                Check(p != null && p.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>() != null && p.CompareTag("Selectable"), $"billet {i + 1} is grabbable and selectable");
                Check(p != null && billets.slots[i] != null && Vector3.Distance(p.transform.position, billets.slots[i].position) < 1e-3f, $"billet {i + 1} sits on its slot");
            }
        }
        var tablePub = station.GetComponentsInChildren<CollisionObjectPublisher>(true).FirstOrDefault(p => p.objectId == "table");
        Check(tablePub != null && tablePub.worldOrigin == origin && !tablePub.isMesh && tablePub.name == "RawStockStand", "RawStockStand publishes as 'table'");
        Transform conveyor = station.transform.Find("Conveyor"), fixture = station.transform.Find("MachineFixture"), table = station.transform.Find("PartsTable");
        Check(conveyor != null && fixture != null && table != null, "Conveyor, MachineFixture and PartsTable groups exist");
        Check(tablePub != null && tablePub.compoundRoots != null && tablePub.compoundRoots.Length == 2
              && tablePub.compoundRoots.Contains(conveyor) && tablePub.compoundRoots.Contains(fixture), "'table' compoundRoots are the conveyor and the fixture");
        if (tablePub != null && tablePub.compoundRoots != null)
            foreach (Transform cr in tablePub.compoundRoots.Where(r => r != null))
                foreach (MeshFilter mf in cr.GetComponentsInChildren<MeshFilter>(false))
                    Check(mf.sharedMesh != null && mf.sharedMesh.name.Contains("Cube"), $"compound primitive '{Path(mf.transform)}' is a cube");
        if (billets != null && billets.slots != null && tablePub != null)
        {
            float standTop = tablePub.transform.position.y + tablePub.transform.lossyScale.y * 0.5f;
            Check(billets.slots.All(s => s != null && s.position.y - billets.partSize.y * 0.5f > standTop + RestGap - 1e-4f), "every billet rests above the stand top with the rest gap");
            for (int i = 1; i < billets.slots.Length; i++)
                Check(billets.slots[i].position.y - billets.slots[i - 1].position.y >= billets.partSize.y + RestGap - 1e-4f, $"billet {i} stacks on billet {i - 1} with the rest gap");
        }
        Transform platterT = machineT != null ? machineT.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Platter") : null;
        if (fixture != null && platterT != null)
        {
            Bounds fb = RendererBounds(fixture), pb = RendererBounds(platterT);
            Transform baseT = fixture.Find("Base");
            Bounds bb = baseT != null ? RendererBounds(baseT) : fb;
            // A 5-axis vise on a TRT-160 overhangs the platter; what matters is that it stands on
            // the platter top, centred on it.
            Check(Mathf.Abs(bb.min.y - pb.max.y) < 0.01f && Mathf.Abs(bb.center.x - pb.center.x) < 0.02f && Mathf.Abs(bb.center.z - pb.center.z) < 0.02f,
                  $"vise base stands centred on the trunnion platter (platter top {pb.max.y:F3}, base bottom {bb.min.y:F3})");
            Check(fb.min.z > DoorPlaneZ + 0.05f, "fixture is behind the door sill");
            foreach (string partName in new[] { "Platter", "Trunnion" })
            {
                Transform part = machineT.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == partName);
                Check(part != null && part.GetComponentsInChildren<MeshFilter>(true).Where(mf => mf.GetComponent<Renderer>() != null).All(mf => mf.GetComponent<MeshCollider>() != null),
                      $"{partName} has mesh colliders for dropped parts");
            }
        }
        else Check(false, "machine has a Platter part");

        // --- the vise ---------------------------------------------------------------------
        var vise = fixture != null ? fixture.GetComponent<Factory.MachineVise>() : null;
        Check(vise != null, "MachineFixture carries a MachineVise");
        if (vise != null)
        {
            Check(vise.jaws != null && vise.jaws.Length == 2 && vise.jaws.All(j => j != null), "vise has two jaw SlidingParts");
            Check(vise.jawWindow != null && vise.jawWindow.IsChildOf(fixture), "vise jaw window is under the fixture");
            Check(vise.republishOnMove == tablePub, "vise re-sends the 'table' compound when its jaws move");
            Check(vise.presentLamp != null && vise.clampLamp != null, "vise lamps assigned");
            Check(Mathf.Abs(vise.openGap - ViseOpenGap) < 1e-4f && vise.openGap >= Mathf.Max(BilletSize.x, BilletSize.z) + 0.03f, $"jaw opening {vise.openGap:F3} m takes a billet either way round with clearance");
            if (vise.jaws != null && vise.jaws.Length == 2 && vise.jaws.All(j => j != null))
            {
                foreach (Factory.SlidingPart jaw in vise.jaws)
                    Check(Mathf.Abs(jaw.openOffset.magnitude - ViseOpenGap * 0.5f) < 1e-4f && jaw.openAmount == 0f, $"jaw '{jaw.name}' authored open with half-gap travel inward");
                Transform plateA = vise.jaws[0].transform.Find("Plate"), plateB = vise.jaws[1].transform.Find("Plate");
                if (plateA != null && plateB != null)
                {
                    Bounds a = RendererBounds(plateA), b = RendererBounds(plateB);
                    float faceGap = Mathf.Max(a.min.z, b.min.z) - Mathf.Min(a.max.z, b.max.z);
                    Check(Mathf.Abs(faceGap - ViseOpenGap) < 2e-3f, $"jaw plates are {faceGap:F3} m apart when open");
                    Vector3 mid = (a.center + b.center) * 0.5f;
                    Check(vise.jawWindow != null && Mathf.Abs(vise.jawWindow.position.x - mid.x) < 1e-3f && Mathf.Abs(vise.jawWindow.position.z - mid.z) < 1e-3f, "jaw window centred between the plates");
                    Check(vise.jawWindow != null && Mathf.Abs(a.max.y - (vise.jawWindow.position.y + vise.jawHeight)) < 2e-3f, $"jawHeight matches the plate tops ({a.max.y:F3})");
                }
                else Check(false, "each jaw has a Plate");
                Transform parallelsT = fixture.Find("Parallels");
                Check(parallelsT != null && vise.jawWindow != null && Mathf.Abs(RendererBounds(parallelsT).max.y - vise.jawWindow.position.y) < 1e-3f, "jaw window sits on the parallels' top");
                Transform anchor = fixture.Find("DropAnchor");
                Check(anchor != null && vise.jawWindow != null && Vector3.Distance(anchor.position, vise.jawWindow.position + Vector3.up * (RestGap + BilletSize.y * 0.5f)) < 1e-3f, "fixture DropAnchor is a billet resting on the parallels");
                // Parts rest RestGap above the parallels (the drop lifts them by it), as the vise measures.
                float stackedStickout = BilletSize.y + RestGap - vise.jawHeight, flatStickout = BilletSize.z + RestGap - vise.jawHeight;
                Check(stackedStickout >= vise.requiredStickout - 1e-4f && flatStickout < vise.requiredStickout - 1e-4f,
                      $"required stickout {vise.requiredStickout * 1000f:F0} mm passes the stacked billet ({stackedStickout * 1000f:F0} mm) and fails it laid flat ({flatStickout * 1000f:F0} mm)");
            }
        }
        var gravity = station.GetComponent<Factory.ReleasedPartGravity>();
        Check(gravity != null && gravity.partsRoot == station.transform.Find("Parts") && Mathf.Abs(gravity.restGap - RestGap) < 1e-5f, "ReleasedPartGravity on the station over the Parts root");
        Check(doorPanel != null && RefIs(doorPanel, "vise", vise) && RefIs(doorPanel, "gravity", gravity), "machine panel drives the vise and shows drops");
        GameObject floor = R("Floor");
        Check(floor != null && floor.GetComponentInChildren<Collider>(true) != null, "floor has a collider to catch dropped parts");

        // Reach: every pick/place anchor between 0.25 and 0.8 x reach horizontally from the base.
        Vector3 basePos = baseTransform != null ? baseTransform.position : fr3.transform.position;
        var anchors = new List<Transform>();
        if (billets != null && billets.slots != null) anchors.AddRange(billets.slots.Where(t => t != null));
        anchors.AddRange(station.GetComponentsInChildren<Transform>(true).Where(t => t.name == "DropAnchor"));
        Check(anchors.Count(t => t.name == "DropAnchor") == 2, "two drop anchors (fixture, conveyor)");
        foreach (Transform a in anchors)
        {
            Vector3 d = a.position - basePos; d.y = 0f;
            Check(d.magnitude > 0.25f && d.magnitude < Fr3Reach * 0.8f, $"anchor '{Path(a)}' at {d.magnitude:F2} m from the base");
        }
        Transform fixtureAnchor = anchors.FirstOrDefault(a => a.name == "DropAnchor" && fixture != null && a.IsChildOf(fixture));
        if (fixtureAnchor != null)
        {
            // Sightline from the visitor's eye (origin, 1.6 m) to the fixture, sampled at the robot's depth.
            Vector3 eye = new Vector3(0f, 1.6f, 0f);
            float t = (basePos.z - eye.z) / (fixtureAnchor.position.z - eye.z);
            Vector3 sight = Vector3.Lerp(eye, fixtureAnchor.position, t);
            Check(Mathf.Abs(sight.x - basePos.x) > 0.25f, $"robot axis is {Mathf.Abs(sight.x - basePos.x):F2} m off the visitor's sightline to the fixture (> 0.25)");
        }
        if (table != null)
        {
            Vector3 d = table.position - basePos; d.y = 0f;
            Check(d.magnitude > 2.5f, $"parts table is {d.magnitude:F1} m from the robot");
            Bounds tb = RendererBounds(table), cb = conveyor != null ? RendererBounds(conveyor) : new Bounds();
            Check(conveyor != null && cb.min.x - tb.max.x < 0.1f && cb.min.x - tb.max.x > -0.05f, "conveyor ends at the parts table's edge");
        }

        // Clearances: the pedestal in front of the door plane and inside the door opening; the
        // station props clear the machine's footprint.
        if (pedestal != null)
        {
            Bounds pb = RendererBounds(pedestal.transform);
            Check(pb.max.z < DoorPlaneZ, $"pedestal front ({pb.max.z:F3}) is in front of the door plane");
            float leftEdge = doorsOpen.center.x - openingWidth * 0.5f, rightEdge = doorsOpen.center.x + openingWidth * 0.5f;
            // The arm reaches the platter through the opening, entering it diagonally when the base is
            // off to the side; door-frame clearance itself is MoveIt's job (the door meshes are
            // collision objects). Keep the base near enough that the diagonal stays shallow.
            Check(openingWidth > 0f && basePos.x > leftEdge - 0.3f && basePos.x < rightEdge + 0.3f, $"robot axis within 0.3 m of the door opening ({leftEdge:F2}..{rightEdge:F2})");
            Check(openingWidth > 0f && (basePos.x < leftEdge + 0.1f || basePos.x > rightEdge - 0.1f),
                  $"robot axis ({basePos.x:F2}) leaves the area in front of the door opening clear");
        }
        // The machine's bounding box reaches 0.5 m in front of the doors (chip-conveyor shelf,
        // pendant), so props are tested against the machine's collision-mesh vertices instead.
        List<Vector3> machineVertices = MachineCollisionVertices(machineT);
        Check(machineVertices.Count > 1000, $"machine collision meshes readable ({machineVertices.Count} vertices)");
        foreach (string n in new[] { "RawStockStand", "Conveyor", "PartsTable" })
        {
            Transform t = station.transform.Find(n);
            if (t == null) continue;
            int inside = 0;
            foreach (MeshFilter mf in t.GetComponentsInChildren<MeshFilter>(false))
            {
                Bounds b = mf.GetComponent<Renderer>() != null ? mf.GetComponent<Renderer>().bounds : new Bounds(mf.transform.position, Vector3.zero);
                b.Expand(0.02f);
                inside += machineVertices.Count(v => b.Contains(v));
            }
            Check(inside == 0, $"no machine geometry inside '{n}' ({inside} collision vertices)");
        }
        if (fixture != null)
        {
            // The vise sits on the machine table, so the thickened table mesh reaches into its lower
            // half by design; what matters is that nothing of the machine rises above its top, where
            // the part rests.
            Bounds fb = RendererBounds(fixture);
            var above = new Bounds(new Vector3(fb.center.x, fb.max.y + 0.15f, fb.center.z), new Vector3(fb.size.x + 0.04f, 0.32f, fb.size.z + 0.04f));
            List<Vector3> offending = machineVertices.Where(v => above.Contains(v)).ToList();
            int inside = offending.Count;
            if (inside > 0)
            {
                var ob = new Bounds(offending[0], Vector3.zero);
                foreach (Vector3 v in offending) ob.Encapsulate(v);
                Line($"  machine vertices above the fixture span {ob.min} .. {ob.max} (fixture bounds {fb.min} .. {fb.max})");
            }
            Check(inside == 0, $"fixture top clears the machine's collision mesh ({inside} vertices above it)");
        }
        if (pedestal != null)
        {
            Bounds pb = RendererBounds(pedestal.transform);
            pb.Expand(0.01f);
            int inside = machineVertices.Count(v => pb.Contains(v));
            Check(inside == 0, $"no machine geometry inside the pedestal ({inside} collision vertices)");
        }

        // --- robot placement phase ------------------------------------------------------
        GameObject placementRoot = R(PlacementRootName), handleGo = R(PlacementHandleName), markersGo = R(ReachMarkersName), placementMenu = R(PlacementMenuName);
        Check(placementRoot != null, $"root '{PlacementRootName}' present");
        Check(handleGo != null && handleGo.activeSelf, $"root '{PlacementHandleName}' present and active");
        Check(markersGo != null && markersGo.activeSelf, $"root '{ReachMarkersName}' present and active");
        Check(placementMenu != null && placementMenu.activeSelf, $"root '{PlacementMenuName}' present and active");
        var visualizer = reach != null ? reach.GetComponent<ReachabilityVolumeVisualizer>() : null;
        Check(visualizer != null && RefIs(visualizer, "robot", fr3), "ReachabilityVolumeVisualizer bound to the fr3");
        if (handleGo != null)
        {
            Check(Vector3.Distance(handleGo.transform.position, DefaultHandlePosition) < 1e-3f && Quaternion.Angle(handleGo.transform.rotation, Quaternion.identity) < 0.1f,
                  $"handle authored at the default pose {DefaultHandlePosition}");
            Check(!handleGo.transform.IsChildOf(fr3.transform) && !fr3.transform.IsChildOf(handleGo.transform), "handle and robot are separate roots");
            Check(Vector3.Distance(fr3.transform.position - handleGo.transform.position, Fr3BasePosition - DefaultHandlePosition) < 1e-3f, "robot sits above the handle pivot");
            if (pedestal != null)
                Check(Vector3.Distance(pedestal.transform.position - handleGo.transform.position, new Vector3(0f, PedestalSize.y * 0.5f, PedestalZ - DefaultHandlePosition.z)) < 1e-3f,
                      "pedestal keeps its authored offset from the handle");
            var grab = handleGo.GetComponent<XRGrabInteractable>();
            var general = handleGo.GetComponent<XRGeneralGrabTransformer>();
            var floorYaw = handleGo.GetComponent<XRGrabTransformerFloorYaw>();
            Check(grab != null && grab.movementType == XRBaseInteractable.MovementType.Instantaneous && grab.trackPosition && !grab.trackRotation && !grab.trackScale
                  && !grab.throwOnDetach && grab.selectMode == InteractableSelectMode.Single && !grab.addDefaultGrabTransformers,
                  "handle grab: instantaneous, position only, single select, no default transformers");
            Check(general != null && general.permittedDisplacementAxes == (XRGeneralGrabTransformer.ManipulationAxes.X | XRGeneralGrabTransformer.ManipulationAxes.Z)
                  && general.constrainedAxisDisplacementMode == XRGeneralGrabTransformer.ConstrainedAxisDisplacementMode.WorldAxisRelative
                  && !general.allowOneHandedScaling && !general.allowTwoHandedScaling, "general transformer slides on world x/z only");
            Check(floorYaw != null && floorYaw.lockHeight && floorYaw.yawOnly && floorYaw.clampToBounds && floorYaw.boundsMin == PlacementBoundsMin && floorYaw.boundsMax == PlacementBoundsMax,
                  "floor-yaw transformer locks height and clamps to the cell bounds");
            Check(grab != null && general != null && floorYaw != null && grab.startingSingleGrabTransformers.SequenceEqual(new XRBaseGrabTransformer[] { general, floorYaw })
                  && grab.startingMultipleGrabTransformers.SequenceEqual(new XRBaseGrabTransformer[] { general, floorYaw }), "starting transformer lists are [general, floorYaw]");
            var ringCollider = handleGo.GetComponentInChildren<BoxCollider>(true);
            Check(ringCollider != null && !ringCollider.isTrigger && ringCollider.excludeLayers.value == ~0, "ring collider is solid and excluded from physics");
            Check(grab != null && ringCollider != null && grab.colliders.Count == 1 && grab.colliders[0] == ringCollider, "grab interactable uses the ring collider");
            Check(ringCollider != null && Mathf.Abs(ringCollider.size.x - HandleRingOuterRadius * 2f) < 1e-3f && Mathf.Abs(ringCollider.transform.position.z - PedestalZ) < 1e-3f, "ring circles the pedestal");
            var ringRenderer = handleGo.GetComponentInChildren<MeshRenderer>(true);
            Check(ringRenderer != null && ringRenderer.sharedMaterial != null && ringRenderer.GetComponent<MeshFilter>()?.sharedMesh != null, "handle ring renders");
            Check(handleGo.GetComponentInChildren<CollisionObjectPublisher>(true) == null && handleGo.GetComponent<SelectableGrabController>() == null && !handleGo.CompareTag("Selectable"),
                  "handle is neither a collision object nor selectable");
            var rbody = handleGo.GetComponent<Rigidbody>();
            Check(rbody != null && rbody.isKinematic && !rbody.useGravity, "handle rigidbody is kinematic");
            Check(handleGo.GetComponent<DesktopDragHandle>() != null, "handle is draggable by the desktop mouse rig");
        }
        var markerSet = markersGo != null ? markersGo.GetComponent<Factory.ReachTargetMarkerSet>() : null;
        Check(markerSet != null && RefIs(markerSet, "reachability", visualizer) && visualizer != null && RefSet(markerSet, "markerMaterial"), "ReachTargetMarkerSet wired to the visualizer");
        if (markerSet != null)
        {
            List<UnityEngine.Object> targetRefs = ArrayRefs(markerSet, "targets");
            Check(targetRefs.Count == anchors.Count && targetRefs.All(t => t != null) && anchors.All(a => targetRefs.Contains(a)), $"markers cover the {anchors.Count} pick/place anchors");
            Check(!markersGo.transform.IsChildOf(station.transform) && markersGo.GetComponentInChildren<MeshFilter>(true) == null, "markers root is outside the station and carries no baked meshes");
        }
        var placement = placementRoot != null ? placementRoot.GetComponent<Factory.RobotPlacementController>() : null;
        Check(placement != null && RefIs(placement, "robotRoot", fr3) && pedestal != null && RefIs(placement, "pedestal", pedestal.transform) && RefIs(placement, "baseAnchor", baseTransform)
              && handleGo != null && RefIs(placement, "handle", handleGo.transform) && RefSet(placement, "handleInteractable") && RefIs(placement, "reachability", visualizer)
              && RefIs(placement, "markers", markerSet) && RefIs(placement, "pickPlaceClient", client) && RefIs(placement, "trajectoryPlayer", player)
              && RefIs(placement, "recorder", recorder) && RefIs(placement, "ghosts", ghosts), "RobotPlacementController wired");
        if (placement != null)
        {
            List<UnityEngine.Object> gated = ArrayRefs(placement, "gatedObjects"), placing = ArrayRefs(placement, "placementObjects"), wrists = ArrayRefs(placement, "wristMenus");
            Check(gated.Count == 2 && gated.Contains(menu) && gated.Contains(doorMenu), "gated objects are the MTC menu and the door menu");
            Check(placing.Count == 2 && placing.Contains(handleGo) && placing.Contains(markersGo), "placement objects are the handle and the markers");
            Check(wrists.Count == wristCount && wrists.All(w => w != null), $"controller hides the MTC controls of all {wristCount} wrist menu(s)");
            Check(menu != null && menu.activeSelf && doorMenu != null && doorMenu.activeSelf, "gated menus authored active (the controller hides them until Confirm)");
            var pso = new SerializedObject(placement);
            Vector2 bmin = pso.FindProperty("cellBoundsMin").vector2Value, bmax = pso.FindProperty("cellBoundsMax").vector2Value;
            Check(bmin == PlacementBoundsMin && bmax == PlacementBoundsMax && bmin.x < DefaultHandlePosition.x && bmax.x > DefaultHandlePosition.x
                  && bmin.y < DefaultHandlePosition.z && bmax.y > DefaultHandlePosition.z, "cell bounds contain the default pose");
            Check(bmax.y + (PedestalZ - DefaultHandlePosition.z) + PedestalSize.z * 0.5f < DoorPlaneZ, "cell bounds keep the pedestal in front of the door plane");
            Check(pso.FindProperty("startInPlacement").boolValue, "scene starts in the placement phase");
            Check(pso.FindProperty("hideReachabilityWhenConfirmed").boolValue, "reachability volume hides once the placement is confirmed");
            Vector2 startOff = pso.FindProperty("startOffset").vector2Value;
            float startYaw = pso.FindProperty("startYawDegrees").floatValue;
            Vector3 startHandle = DefaultHandlePosition + new Vector3(startOff.x, 0f, startOff.y);
            Check(startOff == PlacementStartOffset && Mathf.Abs(startYaw - PlacementStartYaw) < 1e-3f, $"start pose is the authored offset {PlacementStartOffset} / yaw {PlacementStartYaw}");
            Check(startOff.magnitude > 0.6f && startOff.magnitude < 2f, $"start pose is {startOff.magnitude:F2} m from the ideal spot (0.6..2 m)");
            Check(startHandle.x > bmin.x && startHandle.x < bmax.x && startHandle.z > bmin.y && startHandle.z < bmax.y, "start pose lies inside the cell bounds");
            // Pedestal footprint at the start pose (rotated box on the floor) must not overlap the props.
            Quaternion sr = Quaternion.Euler(0f, startYaw, 0f);
            Vector3 pedCentre = startHandle + sr * new Vector3(0f, 0f, PedestalZ - DefaultHandlePosition.z);
            var corners = new List<Vector3>();
            foreach (float sx in new[] { -1f, 1f }) foreach (float sz in new[] { -1f, 1f })
                corners.Add(pedCentre + sr * new Vector3(sx * PedestalSize.x * 0.5f, 0f, sz * PedestalSize.z * 0.5f));
            Bounds pedAtStart = new Bounds(corners[0], Vector3.zero);
            foreach (Vector3 cnr in corners) pedAtStart.Encapsulate(cnr);
            pedAtStart.Expand(new Vector3(0.1f, 10f, 0.1f));
            foreach (string n in new[] { "RawStockStand", "Conveyor", "MachineFixture", "PartsTable" })
            {
                Transform t = station.transform.Find(n);
                if (t == null) continue;
                Bounds b = RendererBounds(t);
                Check(!pedAtStart.Intersects(b), $"pedestal at the start pose clears '{n}'");
            }
            Check(machineBounds.size != Vector3.zero && pedAtStart.max.z < DoorPlaneZ, "pedestal at the start pose stays in front of the door plane");
            var keys = placementRoot.GetComponent<Factory.RobotPlacementDesktopInput>();
            Check(keys != null && RefIs(keys, "controller", placement) && keys.editorOnly && keys.onlyWithoutHeadset, "editor keyboard input for the placement phase wired");
        }
        var placementPanel = placementMenu != null ? placementMenu.GetComponentInChildren<Factory.RobotPlacementPanel>(true) : null;
        Check(placementPanel != null && placement != null && placementPanel.Controller == placement, "RobotPlacementPanel targets the controller");
        var placementDoc = placementMenu != null ? placementMenu.GetComponentInChildren<UIDocument>(true) : null;
        Check(placementDoc != null && placementDoc.visualTreeAsset != null && placementDoc.visualTreeAsset.name == "RobotPlacementPanel", "placement menu document shows RobotPlacementPanel.uxml");
        Check(placementMenu != null && placementMenu.GetComponentsInChildren<MoveItPlanningRequestMenuUI>(true).All(u => !u.enabled), "placement menu's inherited planning UI is disabled");
        Check(placementMenu != null && placementMenu.transform.position.z < DoorPlaneZ && placementMenu.transform.position.x < 0f, "placement menu on the visitor's left, in front of the machine");
        var desktopRig = R(DesktopRigName);
        Check(desktopRig != null && desktopRig.GetComponent<DesktopMouseInteractor>() is DesktopMouseInteractor dm && dm.editorOnly && dm.onlyWithoutHeadset, "desktop mouse rig present and editor-only");

        Debug.Log(Log.ToString());
        return failures;
    }

    // World-space vertices of the machine's readable collision meshes (body and doors).
    private static List<Vector3> MachineCollisionVertices(Transform machine)
    {
        var points = new List<Vector3>();
        if (machine == null) return points;
        foreach (CollisionObjectPublisher pub in machine.GetComponentsInChildren<CollisionObjectPublisher>(true))
        {
            var mf = pub.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
            Matrix4x4 m = mf.transform.localToWorldMatrix;
            foreach (Vector3 v in mf.sharedMesh.vertices) points.Add(m.MultiplyPoint3x4(v));
        }
        return points;
    }

    private static bool RefSet(Component c, string field) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue != null;
    private static bool RefIs(Component c, string field, UnityEngine.Object value) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue == value;
    private static string RefName(Component c, string field) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue?.name;

    // ------------------------------------------------------------------ factory check renders

    private static void RenderFactoryShots(string dir)
    {
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("FACTORY_SHOT_DIR not set");
        Scene scene = EditorSceneManager.OpenScene(FactoryScenePath, OpenSceneMode.Single);
        System.IO.Directory.CreateDirectory(dir);
        var camGo = new GameObject("ShotCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = 70f;
        var rt = new RenderTexture(1600, 1000, 24);
        var closedShots = new (string, Vector3, Vector3)[]
        {
            ("overview", new Vector3(0f, 5f, -5f), new Vector3(0f, 0.8f, 2f)),
            ("user_view", new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.0f, 2f)),
            ("cell_right", new Vector3(2.2f, 1.7f, -0.8f), new Vector3(0f, 0.9f, 1.4f)),
            ("cell_left", new Vector3(-2.2f, 1.7f, -0.8f), new Vector3(0f, 0.9f, 1.4f)),
            ("doors_closed", new Vector3(0f, 1.3f, -0.4f), new Vector3(0f, 1.0f, 1.6f)),
            ("stock", new Vector3(1.3f, 1.3f, 0.1f), new Vector3(0.6f, 0.8f, 0.9f)),
            ("conveyor", new Vector3(-1.4f, 1.6f, -1.4f), new Vector3(-1.8f, 0.7f, 0.9f)),
            ("parts_table", new Vector3(-2.6f, 1.6f, -1.2f), new Vector3(-3.7f, 0.7f, 0.8f)),
            ("door_menu", new Vector3(0.5f, 1.5f, -0.6f), new Vector3(1.4f, 1.2f, 1.0f)),
            ("menus", new Vector3(0.35f, 1.6f, -1.6f), new Vector3(0.35f, 1.0f, 0f)),
            ("top", new Vector3(0f, 7f, 1.5f), new Vector3(0f, 0f, 1.6f)),
            ("machine_front_high", new Vector3(0f, 3.2f, -1.6f), new Vector3(0f, 1.2f, 2.2f)),
            ("machine_back", new Vector3(0.5f, 2.2f, 5.6f), new Vector3(0f, 1.2f, 2.5f)),
            ("machine_left", new Vector3(-4.5f, 2.0f, 2.3f), new Vector3(-0.5f, 1.2f, 2.3f)),
            ("machine_right", new Vector3(4.0f, 2.0f, 2.3f), new Vector3(0.5f, 1.2f, 2.3f)),
            ("machine_top", new Vector3(0f, 6.5f, 2.3f), new Vector3(0f, 0f, 2.31f)),
            ("machine_low", new Vector3(-1.5f, 0.3f, -0.5f), new Vector3(0f, 0.8f, 2f)),
            ("placement", new Vector3(0f, 2.4f, -1.6f), new Vector3(0f, 0.3f, 1.0f)),
            ("placement_menu", new Vector3(-0.3f, 1.5f, -0.9f), new Vector3(-1.3f, 1.3f, 0.3f)),
        };
        var openShots = new (string, Vector3, Vector3)[]
        {
            ("doors_open", new Vector3(0f, 1.3f, -0.4f), new Vector3(0f, 1.0f, 1.6f)),
            ("machine_inside", new Vector3(0.3f, 1.25f, 0.55f), new Vector3(0f, 0.9f, 1.75f)),
            ("fixture", new Vector3(0.4f, 1.1f, 1.1f), new Vector3(0f, 0.9f, 1.65f)),
            ("inside_out", new Vector3(0f, 1.2f, 2.4f), new Vector3(0f, 0.9f, 0.5f)),
            ("inside_up", new Vector3(0.3f, 0.95f, 1.5f), new Vector3(0f, 2.0f, 2.2f)),
            ("inside_left", new Vector3(0.6f, 1.1f, 1.55f), new Vector3(-1.2f, 0.9f, 2.0f)),
            ("inside_right", new Vector3(-0.6f, 1.1f, 1.55f), new Vector3(1.2f, 0.9f, 2.0f)),
        };
        try
        {
            Shoot(cam, rt, dir, closedShots);
            // Pose the doors open in memory (never saved) for the inside views.
            foreach (Factory.MachineDoors doors in FindAllInScene<Factory.MachineDoors>(scene))
                foreach (Factory.SlidingPart d in doors.doors ?? new Factory.SlidingPart[0])
                    if (d != null) d.Set(1f);
            Shoot(cam, rt, dir, openShots);
        }
        finally
        {
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }

    private static void Shoot(Camera cam, RenderTexture rt, string dir, (string, Vector3, Vector3)[] shots)
    {
        foreach (var (name, pos, look) in shots)
        {
            cam.transform.position = pos;
            cam.transform.LookAt(look);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(tex);
            Line("shot " + name);
        }
    }

    // ------------------------------------------------------------------ XR origin for the Meta rig

    // XRI's InteractionAttachController (the Near-Far Interactor's far-grab anchor) skips its
    // whole per-frame update when no active XROrigin exists, so a far grab selects the object
    // but never moves it. The AR scenes keep the XRI rig -- and its XROrigin -- inactive and
    // track through the Meta building-block rig, which has none. Give the Meta rig a passive
    // one (PersistentXRInfrastructure does the same at runtime for scenes built by hand).
    private static void EnsureMetaRigXROrigin(Scene ar)
    {
        GameObject rig = ar.GetRootGameObjects().FirstOrDefault(r => r.name == MetaCameraRigName);
        if (rig == null)
        {
            Line($"No '{MetaCameraRigName}' root; Meta rig XROrigin skipped");
            return;
        }

        XROrigin active = FindAllInScene<XROrigin>(ar).FirstOrDefault(o => o.gameObject.activeInHierarchy);
        if (active != null)
        {
            Line($"Active XROrigin already present at '{Path(active.transform)}'; none added");
            return;
        }

        XROrigin origin = PersistentXRInfrastructure.CreateMetaRigXROrigin(rig);
        if (origin == null)
            throw new InvalidOperationException($"Could not create an XROrigin under '{rig.name}'");
        EditorUtility.SetDirty(origin);
        Line($"Added XROrigin '{Path(origin.transform)}' (origin='{rig.name}', " +
             $"camera='{(origin.Camera != null ? origin.Camera.name : "none")}')");
    }

    // ------------------------------------------------------------------ hand interaction groups

    // The AR scenes track through the Meta building-block camera rig. A hand anchor there
    // carries the XRI Poke, Near-Far and Ray interactor prefabs but, unlike the XRI rig's
    // controllers, no XRInteractionGroup. Without one a single grip press lets the near-far
    // AND the (select-bound) ray interactor grab the same object; wrist-menu shapes use
    // selectMode Multiple, so both succeed and XRI runs a degenerate two-handed grab from one
    // controller. Mirror the rig: one group per hand, poke first, then near-far, then the rest.
    private static void AddHandInteractionGroups(Scene ar, XRInteractionManager manager)
    {
        GameObject rig = ar.GetRootGameObjects().FirstOrDefault(r => r.name == MetaCameraRigName);
        if (rig == null)
        {
            Line($"No '{MetaCameraRigName}' root; hand interaction groups skipped");
            return;
        }

        foreach (string anchorName in new[] { "LeftHandAnchor", "RightHandAnchor" })
        {
            Transform anchor = rig.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == anchorName);
            if (anchor == null)
            {
                Line($"'{MetaCameraRigName}' has no '{anchorName}'; group skipped");
                continue;
            }

            List<XRBaseInteractor> members = anchor.GetComponentsInChildren<XRBaseInteractor>(true)
                .OrderBy(GroupOrder)
                .ToList();
            if (members.Count == 0)
            {
                Line($"'{anchorName}' has no XRI interactors; group skipped");
                continue;
            }

            XRInteractionGroup group = anchor.GetComponent<XRInteractionGroup>();
            if (group == null)
                group = anchor.gameObject.AddComponent<XRInteractionGroup>();
            group.interactionManager = manager;
            group.startingGroupMembers = members.Cast<UnityEngine.Object>().ToList();
            EditorUtility.SetDirty(group);
            Line($"{Path(anchor)}: XRInteractionGroup [{string.Join(", ", members.Select(m => m.name))}]");
        }
    }

    // Same priority order as the XRI rig's controller groups (poke, then near-far).
    private static int GroupOrder(XRBaseInteractor interactor)
    {
        if (interactor is XRPokeInteractor) return 0;
        if (interactor is NearFarInteractor) return 1;
        return 2;
    }

    // ------------------------------------------------------------------ loading scene

    private static void BuildLoadingScene()
    {
        Scene loading = EditorSceneManager.OpenScene(ArLoadingScenePath, OpenSceneMode.Single);
        if (!EditorSceneManager.SaveScene(loading, OutLoadingScenePath))
            throw new InvalidOperationException($"Could not save {OutLoadingScenePath}");

        var prewarmer = FindInScene<SystemPrewarmer>(loading);
        if (prewarmer == null)
            throw new InvalidOperationException("ARLoading has no SystemPrewarmer");
        prewarmer.mainSceneName = OutSceneName;
        EditorUtility.SetDirty(prewarmer);

        EditorSceneManager.MarkSceneDirty(loading);
        if (!EditorSceneManager.SaveScene(loading))
            throw new InvalidOperationException($"Could not save {OutLoadingScenePath}");
        Line($"Saved {OutLoadingScenePath} (loads '{OutSceneName}')");
    }

    private static void AddToBuildSettings(string path)
    {
        List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == path))
            return;
        scenes.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Line($"Added {path} to build settings");
    }

    // ------------------------------------------------------------------ rewiring

    // Walks every serialized object reference under the given roots. A reference that still
    // points at a KitchenFR3 object is replaced by the object of the same name under the AR
    // counterpart of its KitchenFR3 root (same component type when the reference is a
    // component). Anything without a counterpart is cleared and logged.
    private static void RewireKitchenReferences(
        IEnumerable<GameObject> roots, Scene kitchen, Dictionary<string, GameObject> counterparts)
    {
        foreach (Component component in roots.SelectMany(r => r.GetComponentsInChildren<Component>(true)))
        {
            if (component == null) continue; // missing script

            var so = new SerializedObject(component);
            SerializedProperty it = so.GetIterator();
            bool changed = false;
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                UnityEngine.Object value = it.objectReferenceValue;
                if (value == null || !IsInScene(value, kitchen)) continue;

                UnityEngine.Object mapped = MapToCounterpart(value, counterparts);
                Line($"  {Path(component.transform)}.{component.GetType().Name}.{it.propertyPath}: " +
                     $"'{Describe(value)}' -> {(mapped != null ? "'" + Describe(mapped) + "'" : "NULL (no counterpart)")}");
                it.objectReferenceValue = mapped;
                changed = true;
            }

            if (changed)
                so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private static UnityEngine.Object MapToCounterpart(UnityEngine.Object value, Dictionary<string, GameObject> counterparts)
    {
        GameObject go = value as GameObject ?? (value as Component)?.gameObject;
        if (go == null) return null;

        if (!counterparts.TryGetValue(go.transform.root.name, out GameObject counterpartRoot) || counterpartRoot == null)
            return null;

        GameObject target = go.transform == go.transform.root
            ? counterpartRoot
            : counterpartRoot.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t != counterpartRoot.transform && t.name == go.name)?.gameObject;
        if (target == null) return null;

        if (value is GameObject) return target;
        return target.GetComponent(value.GetType());
    }

    private static bool IsInScene(UnityEngine.Object obj, Scene scene)
    {
        if (obj is GameObject go) return go.scene == scene;
        if (obj is Component c) return c.gameObject.scene == scene;
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static GameObject Root(Scene scene, string name)
    {
        GameObject go = scene.GetRootGameObjects().FirstOrDefault(r => r.name == name);
        if (go == null)
            throw new InvalidOperationException($"Scene '{scene.name}' has no root object named '{name}'");
        return go;
    }

    private static GameObject FindByName(Scene scene, string name)
    {
        return scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .FirstOrDefault(t => t.name == name)?.gameObject;
    }

    private static T FindInScene<T>(Scene scene) where T : Component
    {
        return FindAllInScene<T>(scene).FirstOrDefault();
    }

    private static IEnumerable<T> FindAllInScene<T>(Scene scene) where T : Component
    {
        return scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true));
    }

    private static T Require<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        if (c == null)
            throw new InvalidOperationException($"'{go.name}' has no {typeof(T).Name}");
        return c;
    }

    // Inspector "Copy Component" + "Paste Component As New": brings the KitchenFR3 values
    // (topics, prefixes, timeouts) over without touching hidden serialized fields.
    private static T PasteAsNew<T>(T source, GameObject destination) where T : Component
    {
        T[] before = destination.GetComponents<T>();
        if (!ComponentUtility.CopyComponent(source) || !ComponentUtility.PasteComponentAsNew(destination))
            throw new InvalidOperationException($"Could not paste {typeof(T).Name} onto '{destination.name}'");
        T pasted = destination.GetComponents<T>().Except(before).FirstOrDefault();
        if (pasted == null)
            throw new InvalidOperationException($"Pasted {typeof(T).Name} not found on '{destination.name}'");
        Line($"Added {typeof(T).Name} to '{destination.name}' (values from KitchenFR3)");
        return pasted;
    }

    private static SerializedProperty Prop(SerializedObject so, string field)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p == null)
            throw new InvalidOperationException($"{so.targetObject.GetType().Name} has no serialized field '{field}'");
        return p;
    }

    private static void SetRef(Component target, string field, UnityEngine.Object value)
    {
        var so = new SerializedObject(target);
        Prop(so, field).objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        Line($"{Path(target.transform)}.{target.GetType().Name}.{field} = '{Describe(value)}'");
    }

    private static void SetString(Component target, string field, string value)
    {
        var so = new SerializedObject(target);
        Prop(so, field).stringValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        Line($"{Path(target.transform)}.{target.GetType().Name}.{field} = \"{value}\"");
    }

    private static void SetBool(Component target, string field, bool value)
    {
        var so = new SerializedObject(target);
        Prop(so, field).boolValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void CopyStrings(Component from, Component to, params string[] fields)
    {
        var src = new SerializedObject(from);
        var dst = new SerializedObject(to);
        foreach (string field in fields)
        {
            string value = Prop(src, field).stringValue;
            Prop(dst, field).stringValue = value;
            Line($"{Path(to.transform)}.{to.GetType().Name}.{field} = \"{value}\"");
        }
        dst.ApplyModifiedPropertiesWithoutUndo();
    }

    private static string Describe(UnityEngine.Object obj)
    {
        if (obj == null) return "null";
        if (obj is Component c) return $"{c.GetType().Name}@{Path(c.transform)}";
        if (obj is GameObject go) return Path(go.transform);
        return obj.name;
    }

    private static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
    }

    private static void Line(string s) => Log.AppendLine(s);
}
