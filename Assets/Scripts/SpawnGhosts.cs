using System.Collections.Generic;
using UnityEngine;

public class SpawnGhosts : MonoBehaviour
{
    public GameObject robotPrefab;

    public Color startGhostColor = new Color(0, 1, 0, 0.5f);
    public Color goalGhostColor = new Color(1, 0.5f, 0, 0.5f);

    public GameObject realRobot;

    private GameObject startGhost;
    private GameObject goalGhost;

    public void SpawnStartGhost()
    {
        ClearStartGhost();
        startGhost = SpawnGhost("StartGhost", startGhostColor);
        CopyPoseToGhost(realRobot, startGhost);
    }

    public void SpawnGoalGhost()
    {
        ClearGoalGhost();
        goalGhost = SpawnGhost("GoalGhost", goalGhostColor);
        CopyPoseToGhost(realRobot, goalGhost);
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

    public void ClearStartGhost()
    {
        if (startGhost != null) Destroy(startGhost);
        startGhost = null;
    }

    public void ClearGoalGhost()
    {
        if (goalGhost != null) Destroy(goalGhost);
        goalGhost = null;
    }

    public void ClearGhosts()
    {
        ClearStartGhost();
        ClearGoalGhost();
    }

    public GameObject SpawnGhost(string ghostName, Color color)
    {
        // Instantiate under an inactive parent so Awake is deferred until after cleanup.
        var deferParent = new GameObject();
        deferParent.SetActive(false);

        GameObject ghost = Instantiate(robotPrefab, deferParent.transform);
        ghost.name = ghostName;

        // Strip all behavior scripts before Awake can run — ghost is purely visual.
        foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
            DestroyImmediate(mb);

        foreach (var col in ghost.GetComponentsInChildren<Collider>())
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

        // Set world position before activating so ArticulationBody physics registers at the right location.
        ghost.transform.SetPositionAndRotation(realRobot.transform.position, realRobot.transform.rotation);

        ghost.transform.SetParent(null);
        ghost.SetActive(true);
        Destroy(deferParent);

        return ghost;
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
}
