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

// Bakes the MTC pick/place stack from KitchenFR3 (fr3 robot, PickPlaceClient, recorder,
// dashboard) into a demo scene in place of the ur5e. Two targets share the composition:
//   * "ARMTC": the AR demo scene (Build ARMTC).
//   * "FactoryDemo": the VR factory hall, which additionally gets the FR3 station (table,
//     parts tray fed by the injection moulder's chute, totes, blanks tray, fixture plate)
//     and the MoldedPartSpawner / PickPlaceStationController wiring (Build FactoryDemo MTC
//     station). Batch entry points: BuildFactoryBatch, ValidateFactoryBatch,
//     RenderFactoryShotsBatch (FACTORY_SHOT_DIR). FactoryDemo is rewritten in place; to
//     rebuild, restore the committed ur5e version first (the build aborts if 'fr3' exists).
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

    public static void BuildFactoryBatch() => Batch(BuildFactoryScene);
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
                $"{FactoryScenePath} already contains 'fr3'; restore the committed ur5e version before rebuilding");

        Scene kitchen = EditorSceneManager.OpenScene(KitchenScenePath, OpenSceneMode.Additive);
        Composition c = Compose(factory, kitchen, arExtras: false, label: "FactoryDemo");
        LayoutFactoryStation(factory, c);
        EditorSceneManager.CloseScene(kitchen, true);
        Line($"Closed {KitchenScenePath} without saving");

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

    // ------------------------------------------------------------------ factory station

    // Station geometry (world metres; floor y = 0, visitor at the origin facing +z, fr3 base at
    // (0, 0.75, 1) on RobotPedestal). Every pick/place anchor stays 0.25-0.65 m from the base.
    private static readonly Vector3 TableCentre = new Vector3(0f, 0.72f, 1.1f);
    private static readonly Vector3 TableSize = new Vector3(1.6f, 0.06f, 0.9f);
    private static readonly Vector3 TrayCentre = new Vector3(-0.5f, 0.76f, 1.2f);
    private static readonly Vector3 ToteLeftCentre = new Vector3(0.5f, 0.75f, 0.95f);
    private static readonly Vector3 ToteRightCentre = new Vector3(0.5f, 0.75f, 1.3f);
    private static readonly Vector3 BlanksTrayCentre = new Vector3(-0.25f, 0.76f, 1.45f);
    private static readonly Vector3 FixtureCentre = new Vector3(0.25f, 0.755f, 1.45f);
    private static readonly Vector3 ChuteExit = new Vector3(-0.72f, 0.80f, 1.2f);
    private static readonly Vector3 MolderPosition = new Vector3(-2.6f, 0f, 1.82f);
    private static readonly Vector3 Vf1Position = new Vector3(-5.0f, 0f, 6.4f);
    private const float MolderDropDistance = 0.15f;
    private const float Fr3Reach = 0.855f;

    private static void LayoutFactoryStation(Scene scene, Composition c)
    {
        GameObject machines = Root(scene, "Machines");
        Transform molderT = machines.transform.Find("Injection Molder") ?? throw new InvalidOperationException("Machines has no 'Injection Molder'");
        Transform vf1T = machines.transform.Find("Haas VF-1") ?? throw new InvalidOperationException("Machines has no 'Haas VF-1'");
        var molder = Require<Factory.InjectionMoldingCycle>(molderT.gameObject);

        // Moulder: operator side (prefab local -x) faces the station; the mould parting line
        // lines up with the tray row so the chute runs straight to the tray.
        molderT.SetPositionAndRotation(MolderPosition, Quaternion.Euler(0f, 180f, 0f));
        Transform moldedPart = molderT.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "MoldedPart")
            ?? throw new InvalidOperationException("Injection Molder has no 'MoldedPart'");
        Vector3 parting = moldedPart.position;
        Bounds molderBounds = RendererBounds(molderT);
        Vector3 chuteEntry = new Vector3(molderBounds.max.x, 0.93f, parting.z);
        SetFloat(molder, "dropDistance", MolderDropDistance);
        Line($"Moulder at {MolderPosition}; parting line {parting}; chute entry {chuteEntry}");

        // The mill moves back so the moulder's injection unit has room.
        vf1T.SetPositionAndRotation(Vf1Position, Quaternion.Euler(0f, 180f, 0f));
        Line($"VF-1 moved to {Vf1Position}");

        // Materials
        Material tableMat = MakeMaterial("StationTable", new Color(0.30f, 0.31f, 0.33f), 0.55f, 0.7f);
        Material trayMat = MakeMaterial("StationTray", new Color(0.16f, 0.17f, 0.18f), 0.3f, 0f);
        Material toteMat = MakeMaterial("StationTote", new Color(0.15f, 0.35f, 0.70f), 0.35f, 0f);
        Material blankMat = MakeMaterial("Blank", new Color(0.78f, 0.80f, 0.83f), 0.7f, 0.9f);
        Material fixtureMat = MakeMaterial("FixturePlate", new Color(0.22f, 0.22f, 0.24f), 0.5f, 0.6f);
        Material chuteMat = MakeMaterial("Chute", new Color(0.45f, 0.46f, 0.48f), 0.5f, 0.8f);
        Material partMat = AssetDatabase.LoadAssetAtPath<Material>($"{FactoryMaterialFolder}/MoldedPart.mat")
                           ?? MakeMaterial("MoldedPart", new Color(0.9f, 0.45f, 0.15f), 0.6f, 0f);

        var station = new GameObject(FactoryStationRoot);
        SceneManager.MoveGameObjectToScene(station, scene);
        Transform root = station.transform;

        // Work table: the only published prop. Its id must be the MTC server's surface_link
        // ("table"), the one surface the carried object may touch during lift and place.
        GameObject table = Box("StationTable", root, TableCentre, TableSize, tableMat);
        var tablePub = table.AddComponent<CollisionObjectPublisher>();
        tablePub.objectId = "table";
        tablePub.isMesh = false;
        tablePub.worldOrigin = c.baseTransform;
        tablePub.publishRateHz = 0.5f;

        // Parts tray with four slots (parts rest 5 mm above the floor slab).
        Transform tray = Group("PartsTray", root, TrayCentre);
        Box("Floor", tray, TrayCentre, new Vector3(0.36f, 0.01f, 0.26f), trayMat);
        Lips(tray, TrayCentre, 0.36f, 0.26f, 0.03f, trayMat);
        Transform slots = Group("TraySlots", tray, TrayCentre);
        var slotList = new List<Transform>();
        foreach (float x in new[] { -0.545f, -0.455f })
            foreach (float z in new[] { 1.15f, 1.25f })
                slotList.Add(Group($"Slot{slotList.Count}", slots, new Vector3(x, TrayCentre.y + 0.005f + 0.02f, z)));

        // Totes: the demonstrated place targets.
        Tote("ToteLeft", root, ToteLeftCentre, toteMat);
        Tote("ToteRight", root, ToteRightCentre, toteMat);

        // Blanks tray + fixture plate (same mechanism, mill-kitting props).
        Transform blanks = Group("BlanksTray", root, BlanksTrayCentre);
        Box("Floor", blanks, BlanksTrayCentre, new Vector3(0.30f, 0.01f, 0.20f), trayMat);
        Lips(blanks, BlanksTrayCentre, 0.30f, 0.20f, 0.03f, trayMat);
        var blankSlots = new List<Transform>();
        foreach (float x in new[] { -0.33f, -0.25f, -0.17f })
            blankSlots.Add(Group($"BlankSlot{blankSlots.Count}", blanks, new Vector3(x, BlanksTrayCentre.y + 0.005f + 0.015f, BlanksTrayCentre.z)));
        Transform fixture = Group("FixturePlate", root, FixtureCentre);
        Box("Plate", fixture, FixtureCentre, new Vector3(0.30f, 0.01f, 0.20f), fixtureMat);
        foreach (float x in new[] { -0.09f, 0f, 0.09f })
            Box("Pocket", fixture, FixtureCentre + new Vector3(x, 0.006f, 0f), new Vector3(0.085f, 0.004f, 0.06f), trayMat);

        // Chute from the moulder to the tray.
        Transform chute = Group("Chute", root, (chuteEntry + ChuteExit) * 0.5f);
        Group("ChuteEntry", chute, chuteEntry);
        Group("ChuteExit", chute, ChuteExit);
        Vector3 dir = ChuteExit - chuteEntry;
        Transform bed = Group("Bed", chute, (chuteEntry + ChuteExit) * 0.5f);
        bed.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        Box("Slab", bed, bed.position, new Vector3(0.12f, 0.01f, dir.magnitude), chuteMat).transform.rotation = bed.rotation;
        foreach (float side in new[] { -0.06f, 0.06f })
        {
            GameObject rail = Box("Rail", bed, bed.position + bed.right * side + bed.up * 0.02f, new Vector3(0.01f, 0.05f, dir.magnitude), chuteMat);
            rail.transform.rotation = bed.rotation;
        }

        Transform partsRoot = Group("Parts", root, Vector3.zero);

        // Runtime wiring.
        var spawner = station.AddComponent<Factory.MoldedPartSpawner>();
        spawner.molder = molder;
        spawner.chuteEntry = chute.Find("ChuteEntry");
        spawner.chuteExit = chute.Find("ChuteExit");
        spawner.traySlots = slotList.ToArray();
        spawner.partMaterial = partMat;
        spawner.worldOrigin = c.baseTransform;
        spawner.listener = c.listener;
        spawner.partsRoot = partsRoot;

        var blankSet = station.AddComponent<Factory.GrabbablePartSet>();
        blankSet.slots = blankSlots.ToArray();
        blankSet.partMaterial = blankMat;
        blankSet.worldOrigin = c.baseTransform;
        blankSet.listener = c.listener;
        blankSet.partsRoot = partsRoot;

        var controller = station.AddComponent<Factory.PickPlaceStationController>();
        controller.recorder = c.recorder;
        controller.client = c.client;
        controller.spawner = spawner;
        controller.worldOrigin = c.baseTransform;
        controller.autoSort = false;

        SetFloat(c.client, "placeHeightOffsetMeters", FactoryPlaceHeightOffset);

        // The reachability volume carries the UR5e placeholder map; off until an fr3 map exists.
        GameObject reach = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "Reachability Volume");
        if (reach != null) { reach.SetActive(false); Line("Reachability Volume disabled"); }

        EditorUtility.SetDirty(station);
        Line($"Station built under '{FactoryStationRoot}'");
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

    private static void Lips(Transform parent, Vector3 centre, float width, float depth, float height, Material material)
    {
        float y = centre.y + height * 0.5f;
        Box("LipLeft", parent, new Vector3(centre.x - width * 0.5f, y, centre.z), new Vector3(0.015f, height, depth), material);
        Box("LipRight", parent, new Vector3(centre.x + width * 0.5f, y, centre.z), new Vector3(0.015f, height, depth), material);
        Box("LipFront", parent, new Vector3(centre.x, y, centre.z - depth * 0.5f), new Vector3(width, height, 0.015f), material);
        Box("LipBack", parent, new Vector3(centre.x, y, centre.z + depth * 0.5f), new Vector3(width, height, 0.015f), material);
    }

    private static void Tote(string name, Transform root, Vector3 centre, Material material)
    {
        Transform tote = Group(name, root, centre);
        Box("Floor", tote, centre + new Vector3(0f, 0.005f, 0f), new Vector3(0.28f, 0.01f, 0.20f), material);
        Lips(tote, centre, 0.28f, 0.20f, 0.06f, material);
        Group("DropAnchor", tote, centre + new Vector3(0f, 0.05f, 0f));
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

    // Opens FactoryDemo and checks the wiring the station relies on. Returns the failure count.
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
        Check(fr3 != null && fr3.activeSelf, "root 'fr3' present and active");
        Check(ghost != null && !ghost.activeSelf, "root 'fr3 (1)' present and inactive");
        Check(manager != null, "root 'MTCManager' present");
        Check(menu != null, "root 'MTCMenu' present");
        Check(station != null, $"root '{FactoryStationRoot}' present");
        Check(R("ur5e_robot") == null, "ur5e_robot removed");
        GameObject reach = R("Reachability Volume");
        Check(reach == null || !reach.activeSelf, "Reachability Volume inactive");
        foreach (string n in new[] { "XR Interaction Manager", "EventSystem", "XR UI Toolkit Manager" })
            Check(R(n) != null, $"UI input root '{n}' present");
        Check(FindAllInScene<PanelInputConfiguration>(scene).Any(p => p.gameObject.activeInHierarchy), "an active PanelInputConfiguration exists");
        if (failures > 0 && (fr3 == null || station == null)) { Debug.Log(Log.ToString()); return failures; }

        Transform baseTransform = fr3.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "BaseTransform");
        Check(baseTransform != null, "BaseTransform is a child of fr3");
        if (baseTransform != null)
        {
            Check(Mathf.Abs(Mathf.DeltaAngle(baseTransform.localEulerAngles.y, -90f)) < 0.5f, "BaseTransform local yaw is -90");
            Check(Vector3.Distance(baseTransform.position, new Vector3(0f, 0.75f, 1f)) < 0.01f, "BaseTransform world position is (0, 0.75, 1)");
        }

        var profile = fr3.GetComponent<RobotReachProfile>();
        Check(profile != null && profile.planningGroupName == Fr3PlanningGroup && profile.planningFrameId == Fr3PlanningFrame
              && profile.unityJointNamePrefix == Fr3UnityJointPrefix && profile.rosJointNamePrefix == Fr3RosJointPrefix
              && profile.baseAnchor == baseTransform, "RobotReachProfile on fr3 has the panda planning names and baseAnchor");

        var ik = FindInScene<DirectArticulationIKController>(scene);
        Check(ik != null && RefIs(ik, "robotRoot", fr3.transform) && RefName(ik, "endEffector") == Fr3EndEffectorLink, "IK controller targets fr3 / fr3_hand_tcp");
        var ghosts = FindInScene<SpawnGhosts>(scene);
        Check(ghosts != null && ghosts.realRobot == fr3 && ghosts.robotPrefab == ghost, "SpawnGhosts realRobot/robotPrefab are the fr3 and its ghost");
        var listener = FindInScene<CollisionObjectsListenerSimple>(scene);
        Check(listener != null && listener.worldOrigin == (baseTransform != null ? baseTransform.gameObject : null), "CollisionObjectsListenerSimple.worldOrigin is BaseTransform");
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

        var spawner = station.GetComponent<Factory.MoldedPartSpawner>();
        Check(spawner != null && spawner.molder != null && spawner.chuteEntry != null && spawner.chuteExit != null
              && spawner.traySlots != null && spawner.traySlots.Length == 4 && spawner.worldOrigin != null && spawner.listener != null,
              "MoldedPartSpawner wired (molder, chute, 4 slots, origin, listener)");
        var blanks = station.GetComponent<Factory.GrabbablePartSet>();
        Check(blanks != null && blanks.slots != null && blanks.slots.Length == 3 && blanks.worldOrigin != null, "GrabbablePartSet wired (3 blank slots)");
        var controller = station.GetComponent<Factory.PickPlaceStationController>();
        Check(controller != null && controller.recorder != null && controller.client != null && controller.spawner != null && !controller.autoSort,
              "PickPlaceStationController wired, autoSort off");
        var tablePub = station.GetComponentsInChildren<CollisionObjectPublisher>(true).FirstOrDefault(p => p.objectId == "table");
        Check(tablePub != null && tablePub.worldOrigin != null, "StationTable publishes as 'table'");

        // Reach: every anchor between 0.25 and 0.65 m horizontally from the base.
        Vector3 basePos = baseTransform != null ? baseTransform.position : fr3.transform.position;
        var anchors = new List<Transform>();
        if (spawner != null && spawner.traySlots != null) anchors.AddRange(spawner.traySlots.Where(t => t != null));
        if (blanks != null && blanks.slots != null) anchors.AddRange(blanks.slots.Where(t => t != null));
        anchors.AddRange(station.GetComponentsInChildren<Transform>(true).Where(t => t.name == "DropAnchor"));
        foreach (Transform a in anchors)
        {
            Vector3 d = a.position - basePos; d.y = 0f;
            Check(d.magnitude > 0.25f && d.magnitude < Fr3Reach * 0.76f, $"anchor '{Path(a)}' at {d.magnitude:F2} m from the base");
        }

        // No machine or station footprint overlaps another.
        GameObject machines = R("Machines");
        var footprints = new List<(string, Bounds)>();
        if (machines != null)
            foreach (Transform m in machines.transform) footprints.Add((m.name, RendererBounds(m)));
        // The chute deliberately bridges the moulder and the station, so it is left out.
        Renderer[] stationRenderers = station.GetComponentsInChildren<Renderer>(false)
            .Where(r => !r.transform.IsChildOf(station.transform.Find("Chute"))).ToArray();
        Bounds stationBounds = stationRenderers.Length > 0 ? stationRenderers[0].bounds : new Bounds(station.transform.position, Vector3.zero);
        foreach (Renderer r in stationRenderers) stationBounds.Encapsulate(r.bounds);
        footprints.Add((FactoryStationRoot, stationBounds));
        for (int i = 0; i < footprints.Count; i++)
            for (int j = i + 1; j < footprints.Count; j++)
            {
                Bounds a = footprints[i].Item2, b = footprints[j].Item2;
                bool overlap = a.min.x < b.max.x && b.min.x < a.max.x && a.min.z < b.max.z && b.min.z < a.max.z;
                Check(!overlap, $"footprints '{footprints[i].Item1}' and '{footprints[j].Item1}' do not overlap");
            }

        Debug.Log(Log.ToString());
        return failures;
    }

    private static bool RefSet(Component c, string field) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue != null;
    private static bool RefIs(Component c, string field, UnityEngine.Object value) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue == value;
    private static string RefName(Component c, string field) => new SerializedObject(c).FindProperty(field)?.objectReferenceValue?.name;

    // ------------------------------------------------------------------ factory check renders

    private static void RenderFactoryShots(string dir)
    {
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("FACTORY_SHOT_DIR not set");
        EditorSceneManager.OpenScene(FactoryScenePath, OpenSceneMode.Single);
        System.IO.Directory.CreateDirectory(dir);
        var camGo = new GameObject("ShotCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = 70f;
        var rt = new RenderTexture(1600, 1000, 24);
        var shots = new (string, Vector3, Vector3)[]
        {
            ("overview", new Vector3(0f, 6f, -6f), new Vector3(0f, 0.5f, 4f)),
            ("user_view", new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.0f, 6f)),
            ("station", new Vector3(0f, 1.7f, -0.2f), new Vector3(0f, 0.85f, 1.2f)),
            ("station_side", new Vector3(1.6f, 1.5f, 0.2f), new Vector3(0f, 0.85f, 1.2f)),
            ("chute", new Vector3(-1.0f, 1.6f, -0.3f), new Vector3(-1.8f, 0.9f, 1.2f)),
            ("molder", new Vector3(-0.5f, 2.5f, -1.5f), new Vector3(-2.6f, 1.0f, 1.8f)),
            ("top", new Vector3(0f, 18f, 3f), new Vector3(0f, 0f, 3.1f)),
            ("menus", new Vector3(0.35f, 1.6f, -1.6f), new Vector3(0.35f, 1.0f, 0f)),
        };
        try
        {
            foreach (var (name, pos, look) in shots)
            {
                camGo.transform.position = pos;
                camGo.transform.LookAt(look);
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
        finally
        {
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(rt);
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
