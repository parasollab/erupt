using System;
using System.Collections.Generic;
using UnityEngine;

public class SpawnGhosts : MonoBehaviour
{
    public GameObject robotPrefab;

    public Color startGhostColor = new Color(0, 1, 0, 0.5f);
    public Color goalGhostColor = new Color(1, 0.5f, 0, 0.5f);

    public GameObject realRobot;

    [Header("Ghost Selection")]
    [SerializeField] private GameObject ghostControlPanelPrefab;
    [SerializeField] private Vector3 controlPanelLocalOffset = new Vector3(0f, 0.5f, 0f);
    [SerializeField] private float ghostSelectionRadius = 0.35f;

    private GameObject startGhost;
    private GameObject goalGhost;
    private bool startGhostHidden;
    private bool goalGhostHidden;
    private TrajectoryReplay replayController;
    private bool replayActive;

    // All ghosts share this one control panel instance instead of each getting its own, so
    // selecting a different ghost moves the same panel over rather than opening a duplicate.
    private GameObject sharedPanelInstance;
    private GhostControlPanel sharedPanel;
    private GhostSelectable activePanelGhost;

    public event Action ControlStateChanged;

    public bool StartGhostHidden => startGhostHidden;
    public bool GoalGhostHidden => goalGhostHidden;
    public bool ReplayActive => replayActive && replayController != null;
    public float ReplaySpeed => replayController != null ? replayController.PlaybackSpeed : 1f;

    // Remembers where the user manually dragged the shared control panel to, applied regardless of
    // which ghost it's currently attached to, so it reopens in the same relative spot every time
    // (rather than only the specific ghost it was dragged on remembering a custom placement).
    // Not persisted to disk.
    private (Vector3 localPosition, Quaternion localRotation)? savedPanelOffset;

    public void SavePanelOffset(Vector3 localPosition, Quaternion localRotation)
    {
        savedPanelOffset = (localPosition, localRotation);
    }

    public void SpawnStartGhost()
    {
        ClearStartGhost();
        startGhost = SpawnGhost("StartGhost", startGhostColor);
        CopyPoseToGhost(realRobot, startGhost);
        ApplyGhostVisibility(startGhost, startGhostHidden);
    }

    public void SpawnGoalGhost()
    {
        ClearGoalGhost();
        goalGhost = SpawnGhost("GoalGhost", goalGhostColor);
        CopyPoseToGhost(realRobot, goalGhost);
        ApplyGhostVisibility(goalGhost, goalGhostHidden);
    }

    public void UpdateStartGhost()
    {
        if (startGhost == null) return;
        CopyPoseToGhost(realRobot, startGhost);
    }

    public void UpdateGoalGhost()
    {
        if (goalGhost == null) return;
        CopyPoseToGhost(realRobot, goalGhost);
    }

    public void SpawnStartGhostFromPose(DirectArticulationIKController referenceController, string[] jointNames, double[] positions)
    {
        ClearStartGhost();
        startGhost = SpawnGhost("StartGhost", startGhostColor);
        ApplyPoseToGhost(startGhost, referenceController, jointNames, positions);
        ApplyGhostVisibility(startGhost, startGhostHidden);
    }

    public void SpawnGoalGhostFromPose(DirectArticulationIKController referenceController, string[] jointNames, double[] positions)
    {
        ClearGoalGhost();
        goalGhost = SpawnGhost("GoalGhost", goalGhostColor);
        ApplyPoseToGhost(goalGhost, referenceController, jointNames, positions);
        ApplyGhostVisibility(goalGhost, goalGhostHidden);
    }

    public void UpdateStartGhostFromPose(DirectArticulationIKController referenceController, string[] jointNames, double[] positions)
    {
        if (startGhost == null) return;
        ApplyPoseToGhost(startGhost, referenceController, jointNames, positions);
    }

    public void UpdateGoalGhostFromPose(DirectArticulationIKController referenceController, string[] jointNames, double[] positions)
    {
        if (goalGhost == null) return;
        ApplyPoseToGhost(goalGhost, referenceController, jointNames, positions);
    }

    public void ClearStartGhost()
    {
        DetachPanelBeforeDestroy(startGhost);
        if (startGhost != null) Destroy(startGhost);
        startGhost = null;
    }

    public void ClearGoalGhost()
    {
        DetachPanelBeforeDestroy(goalGhost);
        if (goalGhost != null) Destroy(goalGhost);
        goalGhost = null;
    }

    public void ClearGhosts()
    {
        ClearStartGhost();
        ClearGoalGhost();
    }

    public void SetStartGhostHidden(bool hidden)
    {
        startGhostHidden = hidden;
        ApplyGhostVisibility(startGhost, hidden);
        ControlStateChanged?.Invoke();
    }

    public void SetGoalGhostHidden(bool hidden)
    {
        goalGhostHidden = hidden;
        ApplyGhostVisibility(goalGhost, hidden);
        ControlStateChanged?.Invoke();
    }

    public void RegisterReplayController(TrajectoryReplay controller)
    {
        if (controller == null) return;
        replayController = controller;
        ControlStateChanged?.Invoke();
    }

    public void UnregisterReplayController(TrajectoryReplay controller)
    {
        if (replayController != controller) return;
        replayController = null;
        replayActive = false;
        ControlStateChanged?.Invoke();
    }

    public void SetReplayActive(TrajectoryReplay controller, bool active)
    {
        if (controller == null) return;
        if (replayController != controller)
            replayController = controller;
        replayActive = active;
        ControlStateChanged?.Invoke();
    }

    public void SetReplaySpeed(float speed)
    {
        if (replayController == null) return;
        replayController.PlaybackSpeed = speed;
        ControlStateChanged?.Invoke();
    }

    public GameObject SpawnGhost(string ghostName, Color color, TrajectoryReplay replaySource = null)
    {
        // Instantiate under an inactive parent so Awake is deferred until after cleanup.
        var deferParent = new GameObject();
        deferParent.SetActive(false);

        GameObject ghost = Instantiate(robotPrefab, deferParent.transform);
        ghost.name = ghostName;

        // Strip all behavior scripts before Awake can run — ghost is purely visual.
        foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
            DestroyImmediate(mb);

        Collider[] ghostColliders = ghost.GetComponentsInChildren<Collider>(true);
        foreach (var col in ghostColliders)
            col.enabled = false;

        foreach (var ab in ghost.GetComponentsInChildren<ArticulationBody>(true))
        {
            ab.useGravity = false;
            ab.linearVelocity = Vector3.zero;
            ab.angularVelocity = Vector3.zero;
            if (ab.isRoot) ab.immovable = true;
        }

        var overlay = ghost.AddComponent<TranslucentOverride>();
        overlay.overlayColor = color;
        overlay.SetTranslucent(true);  // Apply immediately; don't wait for Start()

        SetupGhostSelection(ghost, ghostName, replaySource, ghostColliders);

        // Set world position before activating so ArticulationBody physics registers at the right location.
        ghost.transform.SetPositionAndRotation(realRobot.transform.position, realRobot.transform.rotation);

        ghost.transform.SetParent(null);
        ghost.SetActive(true);
        Destroy(deferParent);

        return ghost;
    }

    // Reuses the robot's link colliders as raycast-only triggers so the whole posed ghost can be
    // selected. A root sphere remains only as a fallback for prefabs without usable link colliders.
    private void SetupGhostSelection(
        GameObject ghost,
        string ghostKind,
        TrajectoryReplay replaySource,
        Collider[] ghostColliders)
    {
        var selectable = ghost.AddComponent<GhostSelectable>();
        selectable.GhostKind = ghostKind;
        selectable.ReplaySource = replaySource;
        selectable.Spawner = this;

        int enabledColliderCount = 0;
        foreach (var col in ghostColliders)
        {
            if (col == null) continue;
            if (col is MeshCollider meshCollider && !meshCollider.convex)
                continue;

            col.isTrigger = true;
            col.enabled = true;
            enabledColliderCount++;
        }

        if (enabledColliderCount == 0)
        {
            var fallback = ghost.AddComponent<SphereCollider>();
            fallback.isTrigger = true;
            fallback.radius = ghostSelectionRadius;
        }
    }

    private static void ApplyGhostVisibility(GameObject target, bool hidden)
    {
        if (target == null) return;
        target.GetComponent<GhostSelectable>()?.SetHidden(hidden);
    }

    private void EnsureSharedPanel()
    {
        if (sharedPanelInstance != null || ghostControlPanelPrefab == null) return;

        // The prefab root is a shell (BoxCollider only); the UIDocument/GhostControlPanel live
        // on a child of it (see GhostControlPanel.Shell). Parented here (not under any ghost) so
        // it survives ghost respawns; ToggleControlPanelForGhost reparents it on demand.
        sharedPanelInstance = Instantiate(ghostControlPanelPrefab, transform);
        sharedPanel = sharedPanelInstance.GetComponentInChildren<GhostControlPanel>(true);
        sharedPanelInstance.SetActive(false);
    }

    // All ghosts share this one panel instead of each spawning its own, so selecting a different
    // ghost moves the same panel over (applying the one shared saved offset, if any) instead of
    // opening a second, independent copy alongside the first.
    public void ToggleControlPanelForGhost(GhostSelectable selectable)
    {
        if (selectable == null) return;
        EnsureSharedPanel();
        if (sharedPanel == null) return;

        if (activePanelGhost == selectable && sharedPanelInstance.activeSelf)
        {
            sharedPanelInstance.SetActive(false);
            activePanelGhost = null;
            return;
        }

        sharedPanelInstance.transform.SetParent(selectable.transform, false);
        bool hasSavedOffset = savedPanelOffset.HasValue;
        sharedPanelInstance.transform.localPosition = hasSavedOffset ? savedPanelOffset.Value.localPosition : controlPanelLocalOffset;
        sharedPanelInstance.transform.localRotation = hasSavedOffset ? savedPanelOffset.Value.localRotation : Quaternion.identity;

        sharedPanel.Init(this);
        sharedPanel.SetBillboardEnabled(!hasSavedOffset);
        if (Camera.main != null) sharedPanel.SetCameraTransform(Camera.main.transform);

        activePanelGhost = selectable;
        sharedPanelInstance.SetActive(true);
    }

    // Called just before a ghost is destroyed so, if it's currently carrying the shared panel,
    // the panel is rescued (reparented back onto the spawner and hidden) instead of being
    // destroyed along with it.
    public void DetachPanelBeforeDestroy(GameObject ghost)
    {
        if (sharedPanelInstance == null || ghost == null) return;
        if (activePanelGhost == null || activePanelGhost.gameObject != ghost) return;

        sharedPanelInstance.transform.SetParent(transform, false);
        sharedPanelInstance.SetActive(false);
        activePanelGhost = null;
    }

    // Copies joint positions from each source ArticulationBody to the matching ghost ArticulationBody by name.
    // Both hierarchies come from the same URDF so names match.
    private static void CopyPoseToGhost(GameObject source, GameObject ghost)
    {
        var ghostAbByName = new Dictionary<string, ArticulationBody>();
        foreach (var ab in ghost.GetComponentsInChildren<ArticulationBody>(true))
            ghostAbByName[ab.name] = ab;

        foreach (var sourceAb in source.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!ghostAbByName.TryGetValue(sourceAb.name, out ArticulationBody ghostAb)) continue;
            if (sourceAb.dofCount == 0) continue;

            ghostAb.jointPosition = sourceAb.jointPosition;

            ArticulationDrive drive = ghostAb.xDrive;
            drive.target = sourceAb.xDrive.target;
            ghostAb.xDrive = drive;

            ghostAb.PublishTransform();
        }

        Physics.SyncTransforms();
    }

    // Poses a ghost from an explicit (jointNames, positions) pair rather than copying a live robot's
    // pose. jointNames are resolved to the ghost's ArticulationBody GameObjects via referenceController's
    // own JointNames/Joints (same Unity-native naming the real robot's IK controller already uses),
    // since ghost and real robot share the same prefab hierarchy and GameObject names.
    private static void ApplyPoseToGhost(GameObject ghost, DirectArticulationIKController referenceController, string[] jointNames, double[] positions)
    {
        if (ghost == null || referenceController == null || jointNames == null || positions == null) return;

        var ghostAbByGameObjectName = new Dictionary<string, ArticulationBody>();
        foreach (var ab in ghost.GetComponentsInChildren<ArticulationBody>(true))
            ghostAbByGameObjectName[ab.name] = ab;

        IReadOnlyList<string> refNames = referenceController.JointNames;
        IReadOnlyList<ArticulationBody> refJoints = referenceController.Joints;
        var gameObjectNameByJointName = new Dictionary<string, string>();
        for (int i = 0; i < refNames.Count && i < refJoints.Count; i++)
            gameObjectNameByJointName[refNames[i]] = refJoints[i].name;

        for (int i = 0; i < jointNames.Length && i < positions.Length; i++)
        {
            if (!gameObjectNameByJointName.TryGetValue(jointNames[i], out string goName)) continue;
            if (!ghostAbByGameObjectName.TryGetValue(goName, out ArticulationBody ghostAb)) continue;
            if (ghostAb.dofCount == 0) continue;

            float positionRadians = (float)positions[i];
            ghostAb.jointPosition = new ArticulationReducedSpace(positionRadians);

            ArticulationDrive drive = ghostAb.xDrive;
            drive.target = ghostAb.jointType == ArticulationJointType.RevoluteJoint
                ? positionRadians * Mathf.Rad2Deg
                : positionRadians;
            ghostAb.xDrive = drive;

            ghostAb.PublishTransform();
        }

        Physics.SyncTransforms();
    }
}
