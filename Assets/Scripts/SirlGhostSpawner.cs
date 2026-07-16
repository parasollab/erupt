using System.Collections.Generic;
using UnityEngine;

// A translucent robot copy that can play back its own joint trajectory.
public class SirlGhost
{
    public GameObject Root;
    public DirectArticulationIKController Ik;
    public SirlTrajectoryPlayer Replay;
    public Color Color;

    // Toggles the robot body only; the end effector path line (drawn by SirlTrajectoryPlayer)
    // stays visible so trajectories remain comparable even while a single one is isolated.
    public void SetVisible(bool visible)
    {
        if (Root == null) return;
        foreach (var renderer in Root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is LineRenderer) continue;
            renderer.enabled = visible;
        }
    }
}

// Spawns N translucent robot copies with bases at the reference robot's pose.
// Unlike SpawnGhosts, the copies keep their Urdf* components so a
// DirectArticulationIKController + SirlTrajectoryPlayer can animate them.
public class SirlGhostSpawner : MonoBehaviour
{
    public GameObject robotPrefab;
    public GameObject referenceRobot;
    public string endEffectorName = "tool0";

    private readonly List<SirlGhost> ghosts = new List<SirlGhost>();

    public IReadOnlyList<SirlGhost> Ghosts => ghosts;

    public SirlGhost[] Spawn(int count, IReadOnlyList<Color> colors)
    {
        Clear();
        for (int i = 0; i < count; i++)
        {
            Color color = colors != null && i < colors.Count
                ? colors[i]
                : Color.HSVToRGB((float)i / Mathf.Max(count, 1), 0.8f, 1f);
            ghosts.Add(SpawnGhost($"SirlGhost{i}", color));
        }
        return ghosts.ToArray();
    }

    public void Clear()
    {
        foreach (var ghost in ghosts)
            if (ghost.Root != null) Destroy(ghost.Root);
        ghosts.Clear();
    }

    private SirlGhost SpawnGhost(string ghostName, Color color)
    {
        // Instantiate under an inactive parent so Awake is deferred until after cleanup.
        var deferParent = new GameObject();
        deferParent.SetActive(false);

        GameObject root = Instantiate(robotPrefab, deferParent.transform);
        root.name = ghostName;

        // Keep the passive Urdf* components (the IK controller discovers joints
        // through UrdfJoint), strip everything else (Controller, FKRobot, ...).
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            if (!IsPassiveUrdfComponent(mb))
                DestroyImmediate(mb);

        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        foreach (var ab in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            ab.useGravity = false;
            ab.linearVelocity = Vector3.zero;
            ab.angularVelocity = Vector3.zero;
            if (ab.isRoot) ab.immovable = true;
        }

        var overlay = root.AddComponent<TranslucentOverride>();
        overlay.overlayColor = color;
        overlay.SetTranslucent(true);  // Apply immediately; don't wait for Start()

        // Set world position before activating so ArticulationBody physics registers at the right location.
        root.transform.SetPositionAndRotation(referenceRobot.transform.position, referenceRobot.transform.rotation);
        root.transform.localScale = referenceRobot.transform.lossyScale;

        root.transform.SetParent(null);
        root.SetActive(true);
        Destroy(deferParent);

        var ik = root.AddComponent<DirectArticulationIKController>();
        Transform endEffector = FindDeepChild(root.transform, endEffectorName);
        if (endEffector == null)
            Debug.LogWarning($"[SirlGhostSpawner] End effector '{endEffectorName}' not found on ghost '{ghostName}'.");
        ik.Configure(root.transform, endEffector);

        var replay = root.AddComponent<SirlTrajectoryPlayer>();
        replay.Configure(ik);

        return new SirlGhost { Root = root, Ik = ik, Replay = replay, Color = color };
    }

    // True for the URDF importer's passive data components (UrdfJoint, UrdfLink, ...),
    // false for its active scripts in the .Control sub-namespace and everything else.
    private static bool IsPassiveUrdfComponent(MonoBehaviour mb)
    {
        if (mb == null) return false;
        string ns = mb.GetType().Namespace;
        return ns == "Unity.Robotics.UrdfImporter";
    }

    private static Transform FindDeepChild(Transform parent, string childName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            if (child.name == childName)
                return child;
        return null;
    }
}
