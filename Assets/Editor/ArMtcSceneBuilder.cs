using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// Builds "ARMTC": the AR demo scene with the MTC pick/place stack from KitchenFR3 and the
// fr3 robot in place of the ur5e. Run once from the menu, review the result, commit it.
//
// What it does, in order:
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
public static class ArMtcSceneBuilder
{
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
    public static void Build()
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
            Debug.Log("ArMtcSceneBuilder: done.\n" + Log);
        }
        catch (Exception e)
        {
            Debug.LogError("ArMtcSceneBuilder: FAILED.\n" + Log + "\n" + e);
            throw;
        }
    }

    // ------------------------------------------------------------------ main scene

    private static void BuildMainScene()
    {
        Scene ar = EditorSceneManager.OpenScene(ArScenePath, OpenSceneMode.Single);
        if (!EditorSceneManager.SaveScene(ar, OutScenePath))
            throw new InvalidOperationException($"Could not save {OutScenePath}");
        Line($"Saved {ArScenePath} as {OutScenePath}");

        Scene kitchen = EditorSceneManager.OpenScene(KitchenScenePath, OpenSceneMode.Additive);

        // --- AR side --------------------------------------------------------------
        GameObject arSelectionGo = Root(ar, "SelectionManager");
        GameObject arIkManagerGo = Root(ar, "Robot IK Manager");
        GameObject arListenerGo = Root(ar, "CollisionObjectListener");
        GameObject arGhostsGo = Root(ar, "SpawnGhosts");
        GameObject arCameraGo = Root(ar, "Camera");
        GameObject arTagReachGo = Root(ar, "Tag Reachability");
        GameObject arPlanningMenuGo = Root(ar, "MoveItPlanningRequestMenu");
        GameObject ur5e = Root(ar, "ur5e_robot");

        var arSelection = Require<SelectionManager>(arSelectionGo);
        var arIk = Require<DirectArticulationIKController>(arIkManagerGo);
        var arRobotInteraction = Require<Quest3RobotInteractionController>(arIkManagerGo);
        var arListener = Require<CollisionObjectsListenerSimple>(arListenerGo);
        var arGhosts = Require<SpawnGhosts>(arGhostsGo);
        var arReplay = Require<TrajectoryReplay>(arGhostsGo);
        var arMarkerPlacement = Require<MarkerRobotPlacement>(arCameraGo);
        var arBaseTf = Require<RobotBaseTFPublisher>(arCameraGo);
        var arTagReach = Require<TagReachabilityIndicator>(arTagReachGo);
        var arPlanningMenuUi = arPlanningMenuGo.GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
        if (arPlanningMenuUi == null)
            throw new InvalidOperationException("AR MoveItPlanningRequestMenu has no MoveItPlanningRequestMenuUI");

        GameObject baseTransform = arListener.worldOrigin != null
            ? arListener.worldOrigin
            : FindByName(ar, "BaseTransform");
        if (baseTransform == null)
            throw new InvalidOperationException("AR scene has no BaseTransform world origin");
        Line($"World origin: '{baseTransform.name}'");

        // --- Kitchen side ---------------------------------------------------------
        GameObject fr3 = Root(kitchen, "fr3");
        GameObject fr3Ghost = Root(kitchen, "fr3 (1)");
        GameObject mtcManager = Root(kitchen, "MTCManager");
        GameObject mtcMenu = Root(kitchen, "MTCMenu");
        var kitchenAttached = Require<AttachedCollisionObjectListener>(Root(kitchen, "CollisionObjectListener"));
        var kitchenRecorder = FindInScene<PickPlaceTaskRecorder>(kitchen);
        if (kitchenRecorder == null)
            throw new InvalidOperationException("KitchenFR3 has no PickPlaceTaskRecorder");
        var kitchenPlanningMenuUi = Root(kitchen, "MoveItPlanningRequestMenu")
            .GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
        var fr3Player = Require<MTCTrajectoryPlayer>(fr3);
        var pickPlaceClient = Require<PickPlaceClient>(mtcManager);

        Transform fr3Tcp = fr3.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == Fr3EndEffectorLink);
        if (fr3Tcp == null)
            throw new InvalidOperationException($"fr3 has no '{Fr3EndEffectorLink}' link");

        // --- Move the MTC stack and the robot into the new scene --------------------
        var moved = new[] { fr3, fr3Ghost, mtcManager, mtcMenu };
        foreach (GameObject go in moved)
        {
            go.transform.SetParent(null, true);
            SceneManager.MoveGameObjectToScene(go, ar);
            Line($"Moved '{go.name}' into {OutSceneName}");
        }

        // --- Robot swap: fr3 takes the ur5e's place ---------------------------------
        fr3.transform.SetPositionAndRotation(ur5e.transform.position, ur5e.transform.rotation);
        fr3.transform.localScale = ur5e.transform.localScale;
        fr3.SetActive(true);

        // TagReachabilityIndicator reads planning names from a profile on the robot root.
        var reachProfile = fr3.GetComponent<RobotReachProfile>() ?? fr3.AddComponent<RobotReachProfile>();
        reachProfile.planningGroupName = Fr3PlanningGroup;
        reachProfile.ikLinkName = "";
        reachProfile.planningFrameId = Fr3PlanningFrame;
        reachProfile.baseAnchor = baseTransform.transform;
        reachProfile.unityJointNamePrefix = Fr3UnityJointPrefix;
        reachProfile.rosJointNamePrefix = Fr3RosJointPrefix;

        SetRef(arIk, "robotRoot", fr3.transform);
        SetRef(arIk, "endEffector", fr3Tcp);
        SetRef(arRobotInteraction, "endEffector", fr3Tcp);
        SetRef(arMarkerPlacement, "leftRobot", fr3);
        SetRef(arTagReach, "robot", fr3);
        SetString(arTagReach, "planningGroupName", Fr3PlanningGroup);
        SetString(arTagReach, "planningFrameId", Fr3PlanningFrame);
        SetString(arTagReach, "unityJointNamePrefix", Fr3UnityJointPrefix);
        SetString(arTagReach, "rosJointNamePrefix", Fr3RosJointPrefix);
        arBaseTf.robotBase = fr3;
        arGhosts.robotPrefab = fr3Ghost;
        arGhosts.realRobot = fr3;
        SetRef(arReplay, "ikController", arIk);
        EditorUtility.SetDirty(arBaseTf);
        EditorUtility.SetDirty(arGhosts);

        // The AR planning menu was configured for the UR; take the fr3 values from the
        // KitchenFR3 menu instead of hard-coding them here.
        CopyStrings(kitchenPlanningMenuUi, arPlanningMenuUi,
            "planningGroupName", "defaultPlannerId", "executeTrajectoryTopic", "planningPipelineId",
            "motionPlanServiceName", "rosJointNamePrefix", "unityJointNamePrefix");

        // The AR world origin ("BaseTransform": the robot base frame with the URDF root's
        // extra yaw removed, the same convention as KitchenFR3's root "origin") lives under
        // the ur5e. Keep it, at the same world pose, under the fr3 -- otherwise every
        // worldOrigin / baseAnchor reference in the scene silently serializes as null once
        // the ur5e is deleted.
        if (baseTransform.transform.IsChildOf(ur5e.transform))
        {
            baseTransform.transform.SetParent(fr3.transform, true);
            Line($"Moved world origin '{baseTransform.name}' under '{fr3.name}' (world pose kept)");
        }
        SetRef(arListener, "worldOrigin", baseTransform);

        UnityEngine.Object.DestroyImmediate(ur5e);
        Line("Deleted 'ur5e_robot'");

        // --- Components that must live on AR-scene objects --------------------------
        var attached = PasteAsNew<AttachedCollisionObjectListener>(kitchenAttached, arListenerGo);
        SetRef(attached, "sceneListener", arListener);
        SetRef(attached, "ikController", arIk);
        SetRef(attached, "worldOrigin", baseTransform);

        var recorder = PasteAsNew<PickPlaceTaskRecorder>(kitchenRecorder, mtcManager);
        SetRef(recorder, "selectionManager", arSelection);
        SetRef(recorder, "worldOrigin", baseTransform);
        SetRef(recorder, "pickPlaceAction", pickPlaceClient);

        var dashboard = mtcMenu.GetComponentInChildren<MTCDashboardPanel>(true);
        if (dashboard == null)
            throw new InvalidOperationException("MTCMenu has no MTCDashboardPanel");
        SetRef(dashboard, "pickPlaceRecorder", recorder);
        SetRef(dashboard, "pickPlaceAction", pickPlaceClient);
        SetRef(dashboard, "trajectoryPlayer", fr3Player);

        SetRef(fr3Player, "ikController", arIk);
        SetRef(fr3Player, "sceneListener", arListener);

        // Park the dashboard beside the AR planning menu rather than at its kitchen spot.
        Transform menuAnchor = arPlanningMenuGo.transform;
        mtcMenu.transform.SetPositionAndRotation(menuAnchor.position + menuAnchor.right * 0.7f, menuAnchor.rotation);

        // Every wrist menu in the scene (one under the Meta hand anchor, one inside the
        // inactive XRI rig) gets the MTC buttons and their targets.
        // The runtime bootstrap only re-binds the wrist menu inside the persistent XRI rig,
        // and that rig is inactive here; the one under the Meta LeftHandAnchor runs with what
        // is serialized, so give both the AR scene's manager, listener and world origin.
        foreach (WristMenuController wrist in FindAllInScene<WristMenuController>(ar))
        {
            SetBool(wrist, "enableMTC", true);
            SetRef(wrist, "pickPlaceRecorder", recorder);
            SetRef(wrist, "mtcDashboardPanel", mtcMenu);
            SetRef(wrist, "selectionManager", arSelection);
            SetRef(wrist, "collisionObjectsListener", arListener);
            SetRef(wrist, "worldOrigin", baseTransform);
            Line($"Wrist menu '{Path(wrist.transform)}': MTC enabled");
        }

        EnsureMetaRigXROrigin(ar);
        AddHandInteractionGroups(ar, Require<XRInteractionManager>(Root(ar, "XR Interaction Manager")));

        // --- Anything still pointing into KitchenFR3 goes to the AR counterpart ------
        var counterparts = new Dictionary<string, GameObject>
        {
            { "SelectionManager", arSelectionGo },
            { "Robot IK Manager", arIkManagerGo },
            { "SpawnGhosts", arGhostsGo },
            { "CollisionObjectListener", arListenerGo },
            { "MoveItPlanningRequestMenu", arPlanningMenuGo },
            { "XR Interaction Manager", Root(ar, "XR Interaction Manager") },
            { "EventSystem", Root(ar, "EventSystem") },
            { "XR UI Toolkit Manager", Root(ar, "XR UI Toolkit Manager") },
            { "origin", baseTransform },
        };
        var rewireRoots = new List<GameObject>(moved) { arListenerGo };
        RewireKitchenReferences(rewireRoots, kitchen, counterparts);

        EditorSceneManager.CloseScene(kitchen, true);
        Line($"Closed {KitchenScenePath} without saving");

        EditorSceneManager.MarkSceneDirty(ar);
        if (!EditorSceneManager.SaveScene(ar))
            throw new InvalidOperationException($"Could not save {OutScenePath}");
        Line($"Saved {OutScenePath}");
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
