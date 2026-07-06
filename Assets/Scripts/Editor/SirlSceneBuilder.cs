#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// One-shot builder for the SIRL query scene and its menu prefab.
// Safe to re-run: existing outputs are deleted and rebuilt.
public static class SirlSceneBuilder
{
    private const string SourceMenuPrefabPath = "Assets/Prefabs/MoveItPlanningRequestMenu.prefab";
    private const string MenuPrefabPath = "Assets/Prefabs/SirlQueryMenu.prefab";
    private const string MenuUxmlPath = "Assets/UI Toolkit/SirlQuery.uxml";
    private const string SourceScenePath = "Assets/Scenes/MoveIt.unity";
    private const string ScenePath = "Assets/Scenes/SIRL.unity";
    private const string RobotPrefabPath = "Assets/Prefabs/ur5e_robot.prefab";
    private const string IkManagerPrefabPath = "Assets/Prefabs/Robot IK Manager.prefab";
    private const string EndEffectorName = "tool0";

    // Layout mirrors Desktop/sirl sirl/envs/scene_builder.py (MuJoCo, z-up, robot base
    // at origin ON the table, table top at z=0, floor at z=-0.49). Converted to Unity
    // (y-up: unity = (-ros.y, ros.z, ros.x)) and raised so the floor sits at y=0.
    private const float TableTopY = 0.49f;                                  // leg 0.45 + slab 0.04
    private static readonly Vector3 RobotBasePos = new Vector3(0f, TableTopY, 0f);
    private static readonly Vector3 RigSpawnPos = new Vector3(0f, 0f, -1.5f);
    private static readonly Vector3 MenuPos = new Vector3(0.8f, 1.15f, -0.5f);

    [MenuItem("Tools/SIRL/Build Menu Prefab + Scene")]
    public static void BuildAll()
    {
        BuildMenuPrefab();
        BuildScene();
        Debug.Log("[SirlSceneBuilder] Done.");
    }

    [MenuItem("Tools/SIRL/Build Menu Prefab")]
    public static void BuildMenuPrefab()
    {
        AssetDatabase.DeleteAsset(MenuPrefabPath);
        if (!AssetDatabase.CopyAsset(SourceMenuPrefabPath, MenuPrefabPath))
        {
            Debug.LogError($"[SirlSceneBuilder] Failed to copy {SourceMenuPrefabPath}.");
            return;
        }

        GameObject contents = PrefabUtility.LoadPrefabContents(MenuPrefabPath);
        try
        {
            contents.name = "SirlQueryMenu";

            var uiDoc = contents.GetComponentInChildren<UIDocument>(true);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(MenuUxmlPath);
            if (uiDoc == null || uxml == null)
            {
                Debug.LogError($"[SirlSceneBuilder] Missing UIDocument ({uiDoc}) or UXML ({uxml}).");
                return;
            }
            uiDoc.visualTreeAsset = uxml;

            var oldUi = contents.GetComponentInChildren<MoveItPlanningRequestMenuUI>(true);
            GameObject panelHost = oldUi != null ? oldUi.gameObject : uiDoc.gameObject;
            if (oldUi != null) Object.DestroyImmediate(oldUi);

            var panel = panelHost.AddComponent<SirlQueryPanel>();
            SetReference(panel, "uiDocument", uiDoc);

            PrefabUtility.SaveAsPrefabAsset(contents, MenuPrefabPath);
            Debug.Log($"[SirlSceneBuilder] Built {MenuPrefabPath}.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    [MenuItem("Tools/SIRL/Build SIRL Scene")]
    public static void BuildScene()
    {
        AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
        {
            Debug.LogError($"[SirlSceneBuilder] Failed to copy {SourceScenePath}.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // Strip the MoveIt-specific objects; keep all XR / UI Toolkit input plumbing.
        string[] removeRoots = { "mug", "origin", "MoveItPlanningRequestMenu" };
        foreach (var root in scene.GetRootGameObjects())
            if (removeRoots.Contains(root.name))
                Object.DestroyImmediate(root);

        // MoveIt.unity keeps its ground 1 m below the rig; here the table legs and
        // the rig's floor both live at y=0, so the ground must too.
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == "Ground")
                root.transform.position = new Vector3(root.transform.position.x, 0f, root.transform.position.z);
            if (root.name == "XR Origin (XR Rig)")
                root.transform.position = RigSpawnPos;  // stand clear of the table, facing the robot
        }

        // Hidden reference robot: provides base pose, joint names, and home pose.
        var robotPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RobotPrefabPath);
        var robot = (GameObject)PrefabUtility.InstantiatePrefab(robotPrefab, scene);
        robot.name = "UR5e Reference Robot";
        robot.transform.SetPositionAndRotation(RobotBasePos, Quaternion.identity);
        foreach (var renderer in robot.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (var mb in robot.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().FullName == "Unity.Robotics.UrdfImporter.Control.Controller")
                mb.enabled = false;

        Transform endEffector = FindDeepChild(robot.transform, EndEffectorName);
        if (endEffector == null)
            Debug.LogError($"[SirlSceneBuilder] '{EndEffectorName}' not found in {RobotPrefabPath}.");

        // Robot IK Manager drives the reference robot (joint names / home pose source).
        var ikManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(IkManagerPrefabPath);
        var ikManager = (GameObject)PrefabUtility.InstantiatePrefab(ikManagerPrefab, scene);
        foreach (var mb in ikManager.GetComponentsInChildren<MonoBehaviour>(true))
        {
            SetReferenceIfPresent(mb, "robotRoot", robot.transform);
            SetReferenceIfPresent(mb, "endEffector", endEffector);
        }

        // The grabbable end-effector handle is for interactive IK posing, which this
        // scene never uses — the robot is hidden and driven only by trajectories.
        Transform handle = FindDeepChild(ikManager.transform, "EndEffectorHandle");
        if (handle != null) handle.gameObject.SetActive(false);
        var referenceIk = ikManager.GetComponentInChildren<DirectArticulationIKController>(true);

        BuildTabletop(scene);

        // SIRL manager + ghost spawner.
        var managerGo = new GameObject("SIRL Manager");
        SceneManager.MoveGameObjectToScene(managerGo, scene);
        var spawner = managerGo.AddComponent<SirlGhostSpawner>();
        spawner.robotPrefab = robotPrefab;
        spawner.referenceRobot = robot;
        spawner.endEffectorName = EndEffectorName;
        var manager = managerGo.AddComponent<SirlQueryManager>();
        SetReference(manager, "ghostSpawner", spawner);
        SetReference(manager, "referenceIk", referenceIk);

        // Menu.
        var menuPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MenuPrefabPath);
        if (menuPrefab == null)
        {
            Debug.LogError($"[SirlSceneBuilder] {MenuPrefabPath} missing — run Build Menu Prefab first.");
        }
        else
        {
            var menu = (GameObject)PrefabUtility.InstantiatePrefab(menuPrefab, scene);
            menu.transform.position = MenuPos;
            var panel = menu.GetComponentInChildren<SirlQueryPanel>(true);
            SetReference(panel, "manager", manager);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        if (!EditorBuildSettings.scenes.Any(s => s.path == ScenePath))
            EditorBuildSettings.scenes = EditorBuildSettings.scenes
                .Append(new EditorBuildSettingsScene(ScenePath, true))
                .ToArray();

        Debug.Log($"[SirlSceneBuilder] Built {ScenePath}.");
    }

    // Table + laptop from the MuJoCo scene, converted to Unity axes.
    private static void BuildTabletop(Scene scene)
    {
        Material wood = GetOrCreateMaterial("Sirl_Wood", new Color(0.65f, 0.42f, 0.22f));
        Material legWood = GetOrCreateMaterial("Sirl_LegWood", new Color(0.50f, 0.32f, 0.16f));
        Material darkGray = GetOrCreateMaterial("Sirl_DarkGray", new Color(0.13f, 0.13f, 0.13f));
        Material screenBlue = GetOrCreateMaterial("Sirl_ScreenBlue", new Color(0.08f, 0.08f, 0.25f));

        var root = new GameObject("Tabletop");
        SceneManager.MoveGameObjectToScene(root, scene);

        // Table top slab: mujoco centre (0.30, 0, -0.02), half (0.45, 0.35, 0.02)
        AddBox(root.transform, "TableTop", new Vector3(0f, TableTopY - 0.02f, 0.30f),
            new Vector3(0.70f, 0.04f, 0.90f), wood);

        // Legs: mujoco (±0.425, ±0.325) relative to table centre x=0.30, half (0.025, 0.025, 0.225)
        int leg = 0;
        foreach (var sx in new[] { -1f, 1f })
            foreach (var sy in new[] { -1f, 1f })
                AddBox(root.transform, $"TableLeg{leg++}",
                    new Vector3(-sy * 0.325f, 0.225f, 0.30f + sx * 0.425f),
                    new Vector3(0.05f, 0.45f, 0.05f), legWood);

        // Laptop body centre: mujoco (0.45, 0, 0.01) on the table top.
        var laptop = new GameObject("Laptop");
        laptop.transform.SetParent(root.transform);
        laptop.transform.localPosition = new Vector3(0f, TableTopY + 0.01f, 0.45f);

        // Base slab: half (0.148, 0.110, 0.010)
        AddBox(laptop.transform, "LaptopBase", Vector3.zero,
            new Vector3(0.22f, 0.02f, 0.296f), darkGray, local: true);

        // Screen at the +y edge: mujoco rel pos (0, 0.102, 0.100), half (0.148, 0.008, 0.090)
        AddBox(laptop.transform, "LaptopScreen", new Vector3(-0.102f, 0.100f, 0f),
            new Vector3(0.016f, 0.18f, 0.296f), darkGray, local: true);

        // Screen face: mujoco rel pos (0, 0.111, 0.100), half (0.136, 0.002, 0.078)
        AddBox(laptop.transform, "LaptopScreenFace", new Vector3(-0.111f, 0.100f, 0f),
            new Vector3(0.004f, 0.156f, 0.272f), screenBlue, local: true);
    }

    private static void AddBox(Transform parent, string name, Vector3 position, Vector3 size,
        Material material, bool local = false)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent);
        if (local) box.transform.localPosition = position;
        else box.transform.position = position;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static Material GetOrCreateMaterial(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");

        string path = $"Assets/Materials/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var material = new Material(shader) { color = color };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static void SetReference(Component component, string fieldName, Object value)
    {
        var so = new SerializedObject(component);
        var prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"[SirlSceneBuilder] {component.GetType().Name} has no field '{fieldName}'.");
            return;
        }
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetReferenceIfPresent(Component component, string fieldName, Object value)
    {
        if (component == null) return;
        var so = new SerializedObject(component);
        var prop = so.FindProperty(fieldName);
        if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) return;
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Transform FindDeepChild(Transform parent, string childName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            if (child.name == childName)
                return child;
        return null;
    }
}
#endif
