using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Adds a CollisionObjectPublisher to every environment prop in the Task4 study scenes
// and saves the scenes, so the whole environment is present in MoveIt's planning scene.
// This bakes the components into the .unity files (run once from the menu, commit the
// result) rather than attaching them at runtime.
//
// Also covers the Task2 scenes, where only the surface assembly the robot sits on
// (Kitchen_Set03 in the kitchens, Workbench_Combined_low_1 in the factories) becomes
// collision objects -- the rest of the environment stays out of the planning scene.
//
// Every publisher gets the robot's BaseTransform child as its worldOrigin, so published
// poses are expressed relative to the robot base rather than Unity-world-absolute
// (frameId stays "world", matching the hand-placed publishers in Old Scenes/Kitchen).
public static class Task4CollisionObjectSetup
{
    private const string StudyScenesFolder = "Assets/Scenes/Full Study Scenes";

    // Robot root name, same convention as StudySceneExporter -- the robot and everything
    // under it is never a collision object (MoveIt already knows the robot's own geometry).
    private const string RobotRootName = "ur5e_robot";

    // Child of the robot used as every publisher's worldOrigin, so published poses are
    // expressed relative to the robot base rather than Unity-world-absolute.
    private const string BaseTransformName = "BaseTransform";

    // In the Task2 scenes only the assembly the robot sits on becomes collision objects:
    // the kitchen counter/stove set in the kitchen scenes, the workbench in the factory
    // ones. Each Task2 scene contains exactly one of these.
    private static readonly string[] Task2SurfaceRootNames =
    {
        "Kitchen_Set03", "Workbench_Combined_low_1",
    };

    // Mesh-bearing objects that must NOT become collision objects:
    //  - GoalRegion/StartRegion: task waypoint markers the end effector must enter; making
    //    them obstacles would make every plan into them fail.
    //  - EndEffectorHandle: draggable IK grab handle, positioned by script each frame.
    //  - IndicatorSphere: the draggable red indicator sphere from Task3.
    //  - Walls and the floor ("Ground" in the kitchen scenes, "Floor" in the factory ones):
    //    room-scale structure far outside the arm's reach, not worth publishing.
    // Exclusion covers the named object and all of its descendants.
    private static readonly string[] ExcludedObjectNames =
    {
        "GoalRegion", "StartRegion", "EndEffectorHandle", "IndicatorSphere",
        "Left Wall", "Right Wall", "Front Wall", "Back Wall", "Ground", "Floor",
    };

    // Root-name prefixes whose whole hierarchy is skipped (the VR player rig: controller
    // and hand models have MeshFilters but are not environment content).
    private static readonly string[] ExcludedRootPrefixes = { "XR Origin" };

    // Unity built-in primitive mesh names that CollisionObjectPublisher can publish as
    // SolidPrimitives (isMesh = false); everything else is published as a mesh.
    private static readonly HashSet<string> PrimitiveMeshNames =
        new HashSet<string> { "Cube", "Sphere", "Cylinder", "Capsule", "Plane", "Quad" };

    // Asset path of every built-in Unity mesh. The name check alone is not enough: imported
    // models carry Blender's default node names too (cpu.fbx has a child mesh literally named
    // "Cube"), and publishing those as primitives produces a box sized by the transform scale
    // -- for Blender exports (scale ~100 compensated by tiny baked vertices) that's a giant
    // phantom box in RViz.
    private const string BuiltinResourcesPath = "Library/unity default resources";

    private static bool IsBuiltinPrimitive(Mesh mesh)
    {
        return PrimitiveMeshNames.Contains(mesh.name)
            && AssetDatabase.GetAssetPath(mesh) == BuiltinResourcesPath;
    }

    // Interlude is a between-task rest scene with no robot task; always skipped.
    private static List<string> FindStudyScenes(string pattern)
    {
        return Directory.GetFiles(StudyScenesFolder, pattern)
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("Interlude"))
            .OrderBy(p => p)
            .ToList();
    }

    [MenuItem("Study/Collision Objects/Add To All Task4 Scenes")]
    public static void AddToAllTask4Scenes()
    {
        List<string> scenePaths = FindStudyScenes("Task4_*.unity");

        if (scenePaths.Count == 0)
        {
            Debug.LogError($"Task4CollisionObjectSetup: no Task4_*.unity scenes found in {StudyScenesFolder}.");
            return;
        }

        ProcessScenes(scenePaths);
    }

    [MenuItem("Study/Collision Objects/Add To Current Scene")]
    public static void AddToCurrentScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        int added = ProcessOpenScene(scene);
        if (added > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
    }

    // Task2 gets publishers only for the surface assembly the robot sits on (see
    // Task2SurfaceRootNames): every mesh-bearing object under that root, configured the
    // same way Task4 configures those same objects. Everything else in the scene stays
    // out of the planning scene.
    [MenuItem("Study/Collision Objects/Add Surface To All Task2 Scenes")]
    public static void AddSurfaceToAllTask2Scenes()
    {
        List<string> scenePaths = FindStudyScenes("Task2_*.unity");
        if (scenePaths.Count == 0)
        {
            Debug.LogError($"Task4CollisionObjectSetup: no Task2_*.unity scenes found in {StudyScenesFolder}.");
            return;
        }

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int totalAdded = 0;
        try
        {
            for (int i = 0; i < scenePaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Adding surface collision object publishers",
                    Path.GetFileNameWithoutExtension(scenePaths[i]), (float)i / scenePaths.Count);

                Scene scene = EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Single);

                GameObject robotRootGO = GameObject.Find(RobotRootName);
                GameObject worldOriginGO = FindBaseTransform(robotRootGO != null ? robotRootGO.transform : null);
                if (worldOriginGO == null)
                {
                    Debug.LogError($"Task4CollisionObjectSetup: no '{BaseTransformName}' found in '{scene.name}' -- " +
                                    "skipping this scene so publishers don't get baked with a wrong (null) worldOrigin.");
                    continue;
                }

                GameObject surfaceRoot = Task2SurfaceRootNames
                    .Select(GameObject.Find)
                    .FirstOrDefault(go => go != null);
                if (surfaceRoot == null)
                {
                    Debug.LogWarning($"Task4CollisionObjectSetup: none of [{string.Join(", ", Task2SurfaceRootNames)}] " +
                                      $"found in '{scene.name}' -- skipping this scene.");
                    continue;
                }

                HashSet<string> usedIds = new HashSet<string>(
                    Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .Select(p => p.objectId));

                int added = 0, skippedExisting = 0;
                List<string> addedNames = new List<string>();
                foreach (Transform child in surfaceRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!child.gameObject.activeInHierarchy) continue;
                    switch (TryAddPublisher(child.gameObject, worldOriginGO, scene.name, usedIds, addedNames))
                    {
                        case AddResult.Added: added++; break;
                        case AddResult.SkippedExisting: skippedExisting++; break;
                    }
                }

                totalAdded += added;
                if (added > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                Debug.Log($"Task4CollisionObjectSetup: '{scene.name}' ('{surfaceRoot.name}') -- added {added}, " +
                           $"skipped {skippedExisting} already-configured. Added: {string.Join(", ", addedNames)}");
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ReopenOriginalScene(originalScenePath);
        }

        Debug.Log($"Task4CollisionObjectSetup: done -- added {totalAdded} surface publisher(s) " +
                   $"across {scenePaths.Count} Task2 scene(s).");
    }

    // Non-readable meshes publish as EMPTY collision objects: Unity returns a zero-length
    // vertex array for them (even in the editor -- see StudySceneExporter's matching warning),
    // so CollisionObjectPublisher's createReadableMeshCopy fallback copies nothing and MoveIt
    // silently fails to build a shape from the empty mesh. This flips Read/Write on the source
    // model import for every mesh referenced by a publisher in the Task4 scenes, which fixes
    // it for editor and headset builds alike.
    [MenuItem("Study/Collision Objects/Enable Read-Write On Publisher Meshes")]
    public static void EnableReadWriteOnPublisherMeshes()
    {
        List<string> scenePaths = FindStudyScenes("Task4_*.unity");

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        HashSet<string> fixedPaths = new HashSet<string>();
        HashSet<string> unfixablePaths = new HashSet<string>();

        try
        {
            for (int i = 0; i < scenePaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Enabling Read/Write on publisher meshes",
                    Path.GetFileNameWithoutExtension(scenePaths[i]), (float)i / scenePaths.Count);
                EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Single);

                foreach (CollisionObjectPublisher publisher in
                         Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    MeshFilter meshFilter = publisher.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                    Mesh mesh = meshFilter.sharedMesh;
                    if (mesh.isReadable) continue;

                    string assetPath = AssetDatabase.GetAssetPath(mesh);
                    if (string.IsNullOrEmpty(assetPath) || fixedPaths.Contains(assetPath)) continue;

                    if (AssetImporter.GetAtPath(assetPath) is ModelImporter importer)
                    {
                        importer.isReadable = true;
                        importer.SaveAndReimport();
                        fixedPaths.Add(assetPath);
                    }
                    else
                    {
                        unfixablePaths.Add($"{assetPath} (mesh '{mesh.name}' on '{publisher.gameObject.name}')");
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ReopenOriginalScene(originalScenePath);
        }

        Debug.Log($"Task4CollisionObjectSetup: enabled Read/Write on {fixedPaths.Count} model asset(s):\n" +
                   string.Join("\n", fixedPaths.OrderBy(p => p)));
        foreach (string path in unfixablePaths)
        {
            Debug.LogWarning($"Task4CollisionObjectSetup: could not enable Read/Write on {path} -- " +
                              "not a ModelImporter asset; fix it manually.");
        }
    }

    // Strips every CollisionObjectPublisher from the Task4 scenes, for re-running the
    // setup from a clean slate after changing the rules above.
    [MenuItem("Study/Collision Objects/Remove From All Task4 Scenes")]
    public static void RemoveFromAllTask4Scenes()
    {
        RemoveAllPublishersFromScenes(FindStudyScenes("Task4_*.unity"));
    }

    [MenuItem("Study/Collision Objects/Remove From All Task2 Scenes")]
    public static void RemoveFromAllTask2Scenes()
    {
        RemoveAllPublishersFromScenes(FindStudyScenes("Task2_*.unity"));
    }

    private static void RemoveAllPublishersFromScenes(List<string> scenePaths)
    {
        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        try
        {
            foreach (string scenePath in scenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                CollisionObjectPublisher[] publishers =
                    Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (CollisionObjectPublisher publisher in publishers)
                {
                    Object.DestroyImmediate(publisher, true);
                }
                if (publishers.Length > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                Debug.Log($"Task4CollisionObjectSetup: removed {publishers.Length} publisher(s) from '{scene.name}'.");
            }
        }
        finally
        {
            ReopenOriginalScene(originalScenePath);
        }
    }

    private static void ProcessScenes(List<string> scenePaths)
    {
        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int totalAdded = 0;

        try
        {
            for (int i = 0; i < scenePaths.Count; i++)
            {
                string scenePath = scenePaths[i];
                EditorUtility.DisplayProgressBar("Adding collision object publishers",
                    Path.GetFileNameWithoutExtension(scenePath), (float)i / scenePaths.Count);

                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                int added = ProcessOpenScene(scene);
                totalAdded += added;
                if (added > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ReopenOriginalScene(originalScenePath);
        }

        Debug.Log($"Task4CollisionObjectSetup: done -- added {totalAdded} publisher(s) across {scenePaths.Count} scene(s).");
    }

    private static int ProcessOpenScene(Scene scene)
    {
        GameObject robotRootGO = GameObject.Find(RobotRootName);
        Transform robotRoot = robotRootGO != null ? robotRootGO.transform : null;
        if (robotRoot == null)
        {
            Debug.LogWarning($"Task4CollisionObjectSetup: no '{RobotRootName}' found in '{scene.name}' -- " +
                              "nothing will be excluded as the robot; check this scene.");
        }

        GameObject worldOriginGO = FindBaseTransform(robotRoot);
        if (worldOriginGO == null)
        {
            Debug.LogError($"Task4CollisionObjectSetup: no '{BaseTransformName}' found in '{scene.name}' -- " +
                            "skipping this scene so publishers don't get baked with a wrong (null) worldOrigin.");
            return 0;
        }

        List<Transform> excludedRoots = BuildExcludedRoots(scene, robotRoot);

        // Existing objectIds in the scene (e.g. from a previous run) so new ids stay unique.
        HashSet<string> usedIds = new HashSet<string>(
            Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(p => p.objectId));

        int added = 0, skippedExisting = 0;
        List<string> addedNames = new List<string>();

        foreach (GameObject go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!go.activeInHierarchy) continue;
            if (go.layer == 5) continue; // UI layer
            if (IsExcluded(go, excludedRoots)) continue;

            switch (TryAddPublisher(go, worldOriginGO, scene.name, usedIds, addedNames))
            {
                case AddResult.Added: added++; break;
                case AddResult.SkippedExisting: skippedExisting++; break;
            }
        }

        Debug.Log($"Task4CollisionObjectSetup: '{scene.name}' -- added {added}, " +
                   $"skipped {skippedExisting} already-configured. Added: {string.Join(", ", addedNames)}");
        return added;
    }

    private enum AddResult { Added, SkippedExisting, NotEligible }

    // Adds a configured CollisionObjectPublisher to go if it is publishable environment
    // geometry (has a mesh, isn't a text label, doesn't already have a publisher).
    private static AddResult TryAddPublisher(GameObject go, GameObject worldOriginGO, string sceneName,
        HashSet<string> usedIds, List<string> addedNames)
    {
        MeshFilter meshFilter = go.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
        if (meshFilter == null || meshRenderer == null || meshFilter.sharedMesh == null) return AddResult.NotEligible;

        // World-space TextMeshPro labels render via MeshFilter+MeshRenderer too; they're
        // not environment geometry. Matched by type name to avoid a TMPro assembly reference.
        if (go.GetComponents<Component>().Any(c => c != null && c.GetType().Name.Contains("TextMeshPro"))) return AddResult.NotEligible;
        if (go.GetComponentInParent<Canvas>() != null) return AddResult.NotEligible;

        if (go.GetComponent<CollisionObjectPublisher>() != null) return AddResult.SkippedExisting;

        Mesh mesh = meshFilter.sharedMesh;
        bool isPrimitive = IsBuiltinPrimitive(mesh);

        CollisionObjectPublisher publisher = Undo.AddComponent<CollisionObjectPublisher>(go);
        publisher.objectId = MakeUniqueId(go.name, usedIds);
        publisher.frameId = "world";
        publisher.isMesh = !isPrimitive;
        publisher.autoDetectMeshType = false;
        // Fallback for FBX meshes without Read/Write enabled; works in-editor but not in
        // player builds, hence the warning below to fix the import setting instead.
        publisher.createReadableMeshCopy = !isPrimitive && !mesh.isReadable;
        publisher.publishRateHz = 1f;
        publisher.worldOrigin = worldOriginGO;

        if (!isPrimitive && !mesh.isReadable)
        {
            Debug.LogWarning($"Task4CollisionObjectSetup: mesh '{mesh.name}' on '{go.name}' in '{sceneName}' " +
                              "is not readable -- enable 'Read/Write Enabled' in its import settings so it can " +
                              "publish in headset builds (createReadableMeshCopy only works in the editor).");
        }
        addedNames.Add($"{publisher.objectId}{(publisher.isMesh ? " (mesh)" : " (primitive)")}");
        return AddResult.Added;
    }

    // Re-evaluates isMesh on every baked publisher with the strict built-in check above, for
    // fixing publishers that were added before it existed: imported meshes with Blender default
    // names ("Cube" etc.) got isMesh=false and published as giant transform-scaled boxes.
    [MenuItem("Study/Collision Objects/Fix Primitive Misdetections")]
    public static void FixPrimitiveMisdetections()
    {
        List<string> scenePaths = FindStudyScenes("Task4_*.unity");

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        int totalFixed = 0;
        try
        {
            foreach (string scenePath in scenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                List<string> fixedIds = new List<string>();

                foreach (CollisionObjectPublisher publisher in
                         Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    MeshFilter meshFilter = publisher.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                    Mesh mesh = meshFilter.sharedMesh;

                    bool shouldBeMesh = !IsBuiltinPrimitive(mesh);
                    if (publisher.isMesh == shouldBeMesh) continue;

                    Undo.RecordObject(publisher, "Fix primitive misdetection");
                    publisher.isMesh = shouldBeMesh;
                    publisher.createReadableMeshCopy = shouldBeMesh && !mesh.isReadable;
                    EditorUtility.SetDirty(publisher);
                    fixedIds.Add($"{publisher.objectId} (mesh '{mesh.name}')");

                    if (shouldBeMesh && !mesh.isReadable)
                    {
                        Debug.LogWarning($"Task4CollisionObjectSetup: mesh '{mesh.name}' on '{publisher.gameObject.name}' " +
                                          $"in '{scene.name}' is not readable -- run 'Enable Read-Write On Publisher Meshes' " +
                                          "again or it will publish empty.");
                    }
                }

                if (fixedIds.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                totalFixed += fixedIds.Count;
                Debug.Log($"Task4CollisionObjectSetup: '{scene.name}' -- fixed {fixedIds.Count} " +
                           $"misdetected publisher(s): {string.Join(", ", fixedIds)}");
            }
        }
        finally
        {
            ReopenOriginalScene(originalScenePath);
        }
        Debug.Log($"Task4CollisionObjectSetup: fixed {totalFixed} misdetected publisher(s) total.");
    }

    // Deletes publishers from objects the current exclusion rules would skip -- for cleaning
    // up after a name is added to ExcludedObjectNames (e.g. walls/floor) without a full
    // Remove + Add cycle, which would also wipe any hand-tweaked publisher settings.
    [MenuItem("Study/Collision Objects/Remove Publishers From Excluded Objects")]
    public static void RemovePublishersFromExcludedObjects()
    {
        List<string> scenePaths = FindStudyScenes("Task4_*.unity");

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        try
        {
            foreach (string scenePath in scenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                GameObject robotRootGO = GameObject.Find(RobotRootName);
                List<Transform> excludedRoots = BuildExcludedRoots(
                    scene, robotRootGO != null ? robotRootGO.transform : null);

                List<string> removedIds = new List<string>();
                foreach (CollisionObjectPublisher publisher in
                         Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!IsExcluded(publisher.gameObject, excludedRoots)) continue;
                    removedIds.Add(publisher.objectId);
                    Object.DestroyImmediate(publisher, true);
                }

                if (removedIds.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                Debug.Log($"Task4CollisionObjectSetup: '{scene.name}' -- removed {removedIds.Count} " +
                           $"publisher(s) from excluded objects: {string.Join(", ", removedIds)}");
            }
        }
        finally
        {
            ReopenOriginalScene(originalScenePath);
        }
    }

    private static List<Transform> BuildExcludedRoots(Scene scene, Transform robotRoot)
    {
        List<Transform> excludedRoots = new List<Transform>();
        if (robotRoot != null) excludedRoots.Add(robotRoot);
        foreach (string name in ExcludedObjectNames)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) excludedRoots.Add(go.transform);
        }
        foreach (GameObject rootGO in scene.GetRootGameObjects())
        {
            if (ExcludedRootPrefixes.Any(prefix => rootGO.name.StartsWith(prefix)))
            {
                excludedRoots.Add(rootGO.transform);
            }
        }
        return excludedRoots;
    }

    // Excluded if the object (or an ancestor) is one of the excluded roots, or if its own
    // name is on the exclusion list -- the direct name check also catches duplicate-named
    // objects that GameObject.Find (first match only) misses in BuildExcludedRoots.
    private static bool IsExcluded(GameObject go, List<Transform> excludedRoots)
    {
        if (ExcludedObjectNames.Contains(go.name)) return true;
        return excludedRoots.Any(root => go.transform == root || go.transform.IsChildOf(root));
    }

    // Prefers the BaseTransform under the robot root (it's a child of the ur5e_robot prefab
    // instance in these scenes); falls back to a scene-wide search by name.
    private static GameObject FindBaseTransform(Transform robotRoot)
    {
        if (robotRoot != null)
        {
            foreach (Transform t in robotRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == BaseTransformName) return t.gameObject;
            }
        }
        GameObject found = GameObject.Find(BaseTransformName);
        return found;
    }

    private static string MakeUniqueId(string rawName, HashSet<string> usedIds)
    {
        // Same "unity_" prefix convention as the hand-placed publishers in Old Scenes/Kitchen.
        string sanitized = string.Concat(rawName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        if (sanitized.Length == 0) sanitized = "object";

        string candidate = "unity_" + sanitized;
        int suffix = 1;
        while (!usedIds.Add(candidate))
        {
            candidate = $"unity_{sanitized}_{suffix}";
            suffix++;
        }
        return candidate;
    }

    private static void ReopenOriginalScene(string originalScenePath)
    {
        if (!string.IsNullOrEmpty(originalScenePath) &&
            originalScenePath != EditorSceneManager.GetActiveScene().path)
        {
            EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }
    }
}
