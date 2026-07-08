using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Exports every active, mesh-bearing GameObject in each Unity task scene to a
// staging folder (Assets/StudyExport/<SceneName>/) as a plain .obj per object
// plus a manifest.json describing name/mesh/position/orientation/scale, so
// they can be copied into erupt_ws/src/study_scene_assets and displayed as
// visual-only markers in RViz2. erupt and erupt_ws are separate git repos
// with no automated path between them, so copying the output over is a
// manual step.
public static class StudySceneExporter
{
    private const string ExportRoot = "Assets/StudyExport";

    // Root GameObject name of the UR5e robot prefab instance placed in every task scene
    // (confirmed via the PrefabInstance's m_Name override in the scene YAML, source prefab
    // guid 792b9a32fef2641c3be1678d4de8461c). The robot itself is never exported as a static
    // prop -- it comes from the real UR5e mock-hardware bringup instead -- and every other
    // object's pose is exported relative to this root rather than Unity-world-absolute, so
    // props line up correctly relative to wherever the *real* robot actually sits once
    // published in its ROS TF base frame (see scene_marker_publisher's object_frame_id param).
    private const string RobotRootName = "ur5e_robot";

    // GameObject used by SpawnHuman.cs to place the VR player's camera on scene load --
    // exported (also relative to the robot root) so RViz's initial camera view can match it.
    private const string SpawnPointName = "SpawnPoint";

    // Objects that only make sense as VR interaction gizmos, not real environment content --
    // excluded (with their descendants) the same way the robot itself is. "EndEffectorHandle"
    // is the draggable end-effector grab handle from Assets/Prefabs/Robot IK Manager.prefab
    // (positioned by script each frame, not parented under the robot, so the robot-root
    // exclusion alone doesn't catch it); "IndicatorSphere" is the red sphere the participant
    // drags onto the collision location in Task3. Add more names here if others turn up.
    private static readonly string[] ExcludedObjectNames = { "EndEffectorHandle", "IndicatorSphere" };

    // Shared .mtl filename written once per scene directory, referenced by every object's .obj.
    private const string MtlFileName = "materials.mtl";

    [System.Serializable]
    private class Vec3
    {
        public double x, y, z;
        public Vec3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    }

    [System.Serializable]
    private class Quat
    {
        public double x, y, z, w;
        public Quat(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    [System.Serializable]
    private class ColorInfo
    {
        public float r, g, b, a;
        public ColorInfo(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    [System.Serializable]
    private class ManifestObject
    {
        public string name;
        public string mesh;
        public Vec3 position;
        public Quat orientation;
        public Vec3 scale;
        public ColorInfo color;
    }

    [System.Serializable]
    private class SpawnPointInfo
    {
        public Vec3 position = new Vec3(0, 0, 0);
        public Quat orientation = new Quat(0, 0, 0, 1);
    }

    [System.Serializable]
    private class SceneManifest
    {
        public string scene;

        // If false, no "ur5e_robot" GameObject was found in this scene and every object
        // below fell back to Unity-world-absolute poses instead of robot-relative ones --
        // review this scene manually, since props won't line up with the real robot.
        public bool hasRobotRoot;

        // If false, no "SpawnPoint" GameObject was found; spawnPoint below is meaningless (identity).
        public bool hasSpawnPoint;
        public SpawnPointInfo spawnPoint = new SpawnPointInfo();

        public List<ManifestObject> objects = new List<ManifestObject>();
    }

    [MenuItem("Study/Export Scene Assets/Export All Task Scenes")]
    public static void ExportAllTaskScenes()
    {
        List<string> sceneNames = GetAllTaskSceneNames();
        if (sceneNames.Count == 0)
        {
            Debug.LogError("StudySceneExporter: could not find any task scenes via StartScene's StudyController config.");
            return;
        }
        ExportScenes(sceneNames);
    }

    [MenuItem("Study/Export Scene Assets/Export Current Scene")]
    public static void ExportCurrentScene()
    {
        ExportScenes(new List<string> { SceneManager.GetActiveScene().name });
    }

    // Exports every baked TrajectoryData asset (Assets/Trajectories/<SceneName>.asset,
    // one per Task2/Task3 scene) to JSON, so scene_marker_publisher's trajectory replay
    // node can play the same ghost-preview trajectory in RViz2 via moveit_msgs/DisplayTrajectory
    // that TrajectoryAutoPlayer/TrajectoryReplay already plays as a ghost overlay in Unity.
    // Joint angles are frame-independent scalars, so no Unity->ROS axis conversion applies here.
    [MenuItem("Study/Export Scene Assets/Export Trajectories")]
    public static void ExportTrajectories()
    {
        const string trajectoriesSourceDir = "Assets/Trajectories";
        const string trajectoriesExportDir = ExportRoot + "/Trajectories";

        if (Directory.Exists(trajectoriesExportDir))
        {
            Directory.Delete(trajectoriesExportDir, recursive: true);
        }
        Directory.CreateDirectory(trajectoriesExportDir);

        int count = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:TrajectoryData", new[] { trajectoriesSourceDir }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            TrajectoryData data = AssetDatabase.LoadAssetAtPath<TrajectoryData>(assetPath);
            if (data == null) continue;

            string sceneName = Path.GetFileNameWithoutExtension(assetPath);
            File.WriteAllText($"{trajectoriesExportDir}/{sceneName}.json", JsonUtility.ToJson(data, true));
            count++;
        }

        AssetDatabase.Refresh();
        Debug.Log($"StudySceneExporter: exported {count} trajectory asset(s) to {trajectoriesExportDir}. " +
                   "Copy the output into erupt_ws/src/study_scene_assets/trajectories/.");
    }

    // Reads _task1/_task2/_task3's scenePool (private SerializeField lists) off the
    // StudyController component on StartScene, so the exporter always matches whatever
    // is actually configured rather than assuming the Task{N}_{Factory|Kitchen}{1-5}
    // naming convention holds for every scene on disk.
    private static List<string> GetAllTaskSceneNames()
    {
        string startScenePath = FindScenePath("StartScene");
        if (startScenePath == null)
        {
            Debug.LogError("StudySceneExporter: could not find StartScene.");
            return new List<string>();
        }

        string originalScenePath = EditorSceneManager.GetActiveScene().path;
        List<string> names = new List<string>();

        Scene opened = EditorSceneManager.OpenScene(startScenePath, OpenSceneMode.Single);
        StudyController controller = Object.FindFirstObjectByType<StudyController>();
        if (controller == null)
        {
            Debug.LogError("StudySceneExporter: StartScene has no StudyController component.");
        }
        else
        {
            SerializedObject so = new SerializedObject(controller);
            foreach (string taskField in new[] { "_task1", "_task2", "_task3" })
            {
                SerializedProperty pool = so.FindProperty(taskField + ".scenePool");
                if (pool == null) continue;
                for (int i = 0; i < pool.arraySize; i++)
                {
                    names.Add(pool.GetArrayElementAtIndex(i).stringValue);
                }
            }
        }

        if (!string.IsNullOrEmpty(originalScenePath) && originalScenePath != startScenePath)
        {
            EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }

        return names;
    }

    private static string FindScenePath(string sceneName)
    {
        foreach (string guid in AssetDatabase.FindAssets($"t:Scene {sceneName}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == sceneName)
            {
                return path;
            }
        }
        return null;
    }

    private static void ExportScenes(List<string> sceneNames)
    {
        string originalScenePath = EditorSceneManager.GetActiveScene().path;

        try
        {
            for (int i = 0; i < sceneNames.Count; i++)
            {
                string sceneName = sceneNames[i];
                EditorUtility.DisplayProgressBar("Exporting study scenes", sceneName, (float)i / sceneNames.Count);
                ExportScene(sceneName);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (!string.IsNullOrEmpty(originalScenePath))
            {
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
            }
        }

        Debug.Log($"StudySceneExporter: exported {sceneNames.Count} scene(s) to {ExportRoot}. " +
                   "Copy the output into erupt_ws/src/study_scene_assets/{manifests,meshes}/.");
        AssetDatabase.Refresh();
    }

    private static void ExportScene(string sceneName)
    {
        string scenePath = FindScenePath(sceneName);
        if (scenePath == null)
        {
            Debug.LogError($"StudySceneExporter: could not find scene '{sceneName}'; skipping.");
            return;
        }

        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        string sceneDir = $"{ExportRoot}/{sceneName}";
        // Clear any previous export of this scene first, so objects that are no longer
        // exported (e.g. the robot, now excluded) don't linger as orphaned .obj files.
        if (Directory.Exists(sceneDir))
        {
            Directory.Delete(sceneDir, recursive: true);
        }
        Directory.CreateDirectory(sceneDir);

        GameObject robotRootGO = GameObject.Find(RobotRootName);
        Transform robotRoot = robotRootGO != null ? robotRootGO.transform : null;
        if (robotRoot == null)
        {
            Debug.LogError($"StudySceneExporter: no '{RobotRootName}' GameObject found in '{sceneName}'; " +
                            "falling back to Unity-world-absolute poses for this scene. Props will NOT line up " +
                            "with the real robot -- review this scene.");
        }

        GameObject spawnPointGO = GameObject.Find(SpawnPointName);
        if (spawnPointGO == null)
        {
            Debug.LogWarning($"StudySceneExporter: no '{SpawnPointName}' GameObject found in '{sceneName}'; spawnPoint will be omitted.");
        }

        SceneManifest manifest = new SceneManifest
        {
            scene = sceneName,
            hasRobotRoot = robotRoot != null,
            hasSpawnPoint = spawnPointGO != null,
        };
        if (spawnPointGO != null)
        {
            manifest.spawnPoint = BuildSpawnPointInfo(spawnPointGO.transform, robotRoot);
        }

        List<Transform> excludedRoots = new List<Transform>();
        if (robotRoot != null) excludedRoots.Add(robotRoot);
        foreach (string excludedName in ExcludedObjectNames)
        {
            GameObject excludedGO = GameObject.Find(excludedName);
            if (excludedGO != null) excludedRoots.Add(excludedGO.transform);
        }

        HashSet<string> usedNames = new HashSet<string>();
        MaterialExporter materialExporter = new MaterialExporter(sceneDir);

        foreach (GameObject go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!go.activeInHierarchy) continue;
            if (excludedRoots.Any(root => go.transform == root || go.transform.IsChildOf(root))) continue;

            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshRenderer == null || meshFilter.sharedMesh == null) continue;

            string objectName = MakeUniqueName(go.name, usedNames);
            string meshFileName = objectName + ".obj";

            Material[] materials = meshRenderer.sharedMaterials;
            Mesh mesh = meshFilter.sharedMesh;
            string[] materialNamesBySubmesh = new string[mesh.subMeshCount];
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Material material = materials.Length > 0 ? materials[Mathf.Min(sub, materials.Length - 1)] : null;
                materialNamesBySubmesh[sub] = materialExporter.GetOrAddMaterial(material);
            }

            WriteObj(Path.Combine(sceneDir, meshFileName), mesh, MtlFileName, materialNamesBySubmesh);

            (Vec3 position, Quat orientation) = ComputeRosPose(go.transform.position, go.transform.rotation, robotRoot);
            Vector3 scaleUnity = go.transform.lossyScale;

            manifest.objects.Add(new ManifestObject
            {
                name = objectName,
                mesh = meshFileName,
                position = position,
                orientation = orientation,
                // Same Y/Z axis swap as position/mesh vertices -- no existing
                // RosUnityConversion helper for scale, so applied by hand here.
                scale = new Vec3(scaleUnity.x, scaleUnity.z, scaleUnity.y),
                // Kept as a fallback/tint alongside the real per-material .mtl+texture data
                // below, for markers that can't or don't use mesh_use_embedded_materials.
                color = GetObjectColor(meshRenderer),
            });
        }

        materialExporter.WriteMtlFile(MtlFileName);

        File.WriteAllText($"{sceneDir}/manifest.json", JsonUtility.ToJson(manifest, true));
        Debug.Log($"StudySceneExporter: exported {manifest.objects.Count} object(s) from '{sceneName}' " +
                   $"(hasRobotRoot={manifest.hasRobotRoot}, hasSpawnPoint={manifest.hasSpawnPoint}).");
    }

    private static SpawnPointInfo BuildSpawnPointInfo(Transform spawnPoint, Transform robotRoot)
    {
        (Vec3 position, Quat orientation) = ComputeRosPose(spawnPoint.position, spawnPoint.rotation, robotRoot);
        return new SpawnPointInfo { position = position, orientation = orientation };
    }

    // Converts a world-space Unity pose into the pose to store in the manifest: if a robot
    // root was found, expressed relative to it (so it's meaningful in the real robot's ROS TF
    // base frame regardless of where this Unity scene happened to place the robot); otherwise
    // falls back to Unity-world-absolute. Either way, RosUnityConversion's axis swap is applied
    // last -- it's just a relabeling of axes, so it's valid whether the input is a world pose
    // or one already made relative to a parent transform.
    private static (Vec3, Quat) ComputeRosPose(Vector3 worldPosition, Quaternion worldRotation, Transform robotRoot)
    {
        Vector3 pos = worldPosition;
        Quaternion rot = worldRotation;
        if (robotRoot != null)
        {
            pos = robotRoot.InverseTransformPoint(worldPosition);
            rot = Quaternion.Inverse(robotRoot.rotation) * worldRotation;
        }

        RosMessageTypes.Geometry.PointMsg rosPos = RosUnityConversion.UnityToRosPosition(pos);
        RosMessageTypes.Geometry.QuaternionMsg rosRot = RosUnityConversion.UnityToRosQuaternion(rot);
        return (new Vec3(rosPos.x, rosPos.y, rosPos.z), new Quat(rosRot.x, rosRot.y, rosRot.z, rosRot.w));
    }

    // Best-effort per-object flat color/transparency, since exported meshes carry no
    // texture/material data (geometry-only .obj). Material.color transparently resolves
    // to whichever of "_BaseColor" (URP/HDRP Lit) or "_Color" (Standard shader) the
    // material's shader actually defines. This approximates textured materials with their
    // average/tint color rather than the real texture -- acceptable for a visual-context
    // marker, but don't expect it to look identical to Unity for heavily-textured props.
    // Only the first material is sampled; multi-material submeshes get one flat color.
    private static ColorInfo GetObjectColor(MeshRenderer meshRenderer)
    {
        Material material = meshRenderer.sharedMaterial;
        if (material == null) return new ColorInfo(0.7f, 0.7f, 0.7f, 1f);

        Color c = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
        return new ColorInfo(c.r, c.g, c.b, c.a);
    }

    // Per-scene material/texture dedup + .mtl accumulation, so identical materials shared
    // across many objects (common with these asset-pack scenes) only get one newmtl block
    // and one copied texture file, not one per object.
    private class MaterialExporter
    {
        private const string FallbackMaterialName = "NoMaterial";
        private static readonly string[] RecognizedImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

        private readonly string _sceneDir;
        private readonly Dictionary<Material, string> _materialNames = new Dictionary<Material, string>();
        private readonly HashSet<string> _usedMaterialNames = new HashSet<string>();
        private readonly HashSet<string> _usedTextureFileNames = new HashSet<string>();
        private readonly Dictionary<string, string> _copiedTexturesByKey = new Dictionary<string, string>();
        private readonly StringBuilder _mtl = new StringBuilder();
        private bool _hasFallback;

        public MaterialExporter(string sceneDir) { _sceneDir = sceneDir; }

        public string GetOrAddMaterial(Material material)
        {
            if (material == null) return GetOrAddFallback();
            if (_materialNames.TryGetValue(material, out string existing)) return existing;

            string name = MakeUnique(material.name, "Material", _usedMaterialNames);
            _materialNames[material] = name;

            Color c = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
            string texFileName = ExportTexture(material);

            _mtl.AppendLine($"newmtl {name}");
            _mtl.AppendLine($"Kd {c.r} {c.g} {c.b}");
            _mtl.AppendLine($"d {c.a}");
            if (texFileName != null) _mtl.AppendLine($"map_Kd {texFileName}");
            _mtl.AppendLine();

            return name;
        }

        private string GetOrAddFallback()
        {
            if (!_hasFallback)
            {
                _usedMaterialNames.Add(FallbackMaterialName);
                _mtl.AppendLine($"newmtl {FallbackMaterialName}");
                _mtl.AppendLine("Kd 0.7 0.7 0.7");
                _mtl.AppendLine("d 1");
                _mtl.AppendLine();
                _hasFallback = true;
            }
            return FallbackMaterialName;
        }

        // Prefers copying the material's source texture file directly (works regardless of
        // the texture's "Read/Write Enabled" import setting, since it reads the original file
        // bytes off disk rather than going through Unity's texture API) over re-encoding pixel
        // data, which is only attempted as a fallback for non-asset-backed textures.
        private string ExportTexture(Material material)
        {
            Texture texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.mainTexture;
            if (texture == null) return null;

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(assetPath))
            {
                if (_copiedTexturesByKey.TryGetValue(assetPath, out string cachedByPath)) return cachedByPath;

                string ext = Path.GetExtension(assetPath).ToLowerInvariant();
                if (RecognizedImageExtensions.Contains(ext))
                {
                    string fileName = MakeUnique(Path.GetFileNameWithoutExtension(assetPath), "Texture", _usedTextureFileNames, ext);
                    File.Copy(assetPath, Path.Combine(_sceneDir, fileName), overwrite: true);
                    _copiedTexturesByKey[assetPath] = fileName;
                    return fileName;
                }
                Debug.LogWarning($"StudySceneExporter: texture asset '{assetPath}' has an unrecognized " +
                                  $"extension '{ext}'; falling back to pixel re-encode.");
            }

            if (!(texture is Texture2D tex2D)) return null;

            string cacheKey = "instance:" + texture.GetInstanceID();
            if (_copiedTexturesByKey.TryGetValue(cacheKey, out string cachedByInstance)) return cachedByInstance;

            try
            {
                Texture2D readable = MakeReadableCopy(tex2D);
                byte[] png = ImageConversion.EncodeToPNG(readable);
                Object.DestroyImmediate(readable);
                string fileName = MakeUnique(tex2D.name, "Texture", _usedTextureFileNames, ".png");
                File.WriteAllBytes(Path.Combine(_sceneDir, fileName), png);
                _copiedTexturesByKey[cacheKey] = fileName;
                return fileName;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"StudySceneExporter: failed to export texture '{texture.name}': {e.Message}");
                return null;
            }
        }

        private static Texture2D MakeReadableCopy(Texture2D source)
        {
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readable.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        private static string MakeUnique(string rawName, string fallbackBaseName, HashSet<string> usedNames, string suffix = "")
        {
            string sanitized = string.Concat((rawName ?? fallbackBaseName).Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            if (sanitized.Length == 0) sanitized = fallbackBaseName;

            string candidate = sanitized + suffix;
            int i = 1;
            while (!usedNames.Add(candidate))
            {
                candidate = $"{sanitized}_{i}{suffix}";
                i++;
            }
            return candidate;
        }

        public void WriteMtlFile(string mtlFileName)
        {
            File.WriteAllText(Path.Combine(_sceneDir, mtlFileName), _mtl.ToString());
        }
    }

    private static string MakeUniqueName(string rawName, HashSet<string> usedNames)
    {
        string sanitized = string.Concat(rawName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        if (sanitized.Length == 0) sanitized = "Object";

        string candidate = sanitized;
        int suffix = 1;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{sanitized}_{suffix}";
            suffix++;
        }
        if (candidate != sanitized)
        {
            Debug.LogWarning($"StudySceneExporter: duplicate object name '{rawName}' in scene; renamed to '{candidate}'.");
        }
        return candidate;
    }

    // Exports mesh-local-space vertices (unscaled -- transform.lossyScale is stored
    // separately in the manifest) with the same Unity->ROS Y/Z axis swap used
    // elsewhere in this project (see RosUnityConversion, CollisionObjectPublisher's
    // UnityMeshToRosMesh). Triangle winding is reversed to compensate for the parity
    // flip that axis swap introduces, mirroring CollisionObjectsListenerSimple.BuildMesh
    // -- skipping this step would make every exported face render inside-out in RViz.
    private static void WriteObj(string path, Mesh mesh, string mtlFileName, string[] materialNamesBySubmesh)
    {
        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uvs = mesh.uv;

        if (verts == null || verts.Length == 0)
        {
            Debug.LogError($"StudySceneExporter: mesh '{mesh.name}' returned 0 vertices (isReadable={mesh.isReadable}). " +
                            "If this mesh comes from an FBX, enable 'Read/Write Enabled' on its Model import settings and re-export.");
        }

        bool hasNormals = normals != null && normals.Length == verts.Length;
        bool hasUVs = uvs != null && uvs.Length == verts.Length;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# exported by StudySceneExporter from mesh '{mesh.name}'");
        sb.AppendLine($"mtllib {mtlFileName}");

        foreach (Vector3 v in verts)
        {
            sb.AppendLine($"v {v.x} {v.z} {v.y}");
        }
        if (hasUVs)
        {
            foreach (Vector2 uv in uvs)
            {
                sb.AppendLine($"vt {uv.x} {uv.y}");
            }
        }
        if (hasNormals)
        {
            foreach (Vector3 n in normals)
            {
                sb.AppendLine($"vn {n.x} {n.z} {n.y}");
            }
        }

        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (sub < materialNamesBySubmesh.Length)
            {
                sb.AppendLine($"usemtl {materialNamesBySubmesh[sub]}");
            }

            int[] tris = mesh.GetTriangles(sub);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int i0 = tris[i] + 1;
                int i1 = tris[i + 1] + 1;
                int i2 = tris[i + 2] + 1;

                sb.Append("f ");
                AppendFaceVertex(sb, i0, hasUVs, hasNormals);
                AppendFaceVertex(sb, i2, hasUVs, hasNormals);
                AppendFaceVertex(sb, i1, hasUVs, hasNormals);
                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void AppendFaceVertex(StringBuilder sb, int index, bool hasUV, bool hasNormal)
    {
        if (hasUV && hasNormal) sb.Append($"{index}/{index}/{index} ");
        else if (hasUV) sb.Append($"{index}/{index} ");
        else if (hasNormal) sb.Append($"{index}//{index} ");
        else sb.Append($"{index} ");
    }
}
