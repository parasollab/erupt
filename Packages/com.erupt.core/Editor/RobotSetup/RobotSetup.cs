using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Erupt.Robot;
using UnityEditor;
using UnityEngine;

namespace Erupt.Editor
{
    /// <summary>What a Robot Setup run produced.</summary>
    public sealed class RobotSetupResult
    {
        public RobotSetupReport Report = new RobotSetupReport();
        /// <summary>The robot was written. False when the run stopped before the import; the report says why.</summary>
        public bool Succeeded;
        public ResolvedRobot Robot;
        public string PrefabPath = "";
        public string GhostPath = "";
        public string ProfilePath = "";
        public string ReportPath = "";
    }

    /// <summary>
    /// Turns a robot description into a robot ERUPT can use: imports the URDF, wires the robot
    /// prefab (<see cref="RobotInstance"/>, IK controller, interaction, trajectory player, ghosts,
    /// planning-frame anchor), makes the ghost prefab and the <see cref="RobotProfile"/>, and
    /// writes a report of what it found and what is wrong. Running it again on the same output
    /// folder updates the assets in place, so their GUIDs, and the scenes that use them, stay valid.
    /// </summary>
    /// <remarks>
    /// The window is <c>ERUPT &gt; Robots &gt; Robot Setup…</c>. From the command line:
    /// <code>
    /// Unity -batchmode -quit -projectPath . -executeMethod Erupt.Editor.RobotSetup.RunFromCommandLine \
    ///   -urdf path/robot.urdf [-srdf path/robot.srdf] [-rosNames path/robot.ros.json] \
    ///   -out Assets/Robots/MyRobot [-name "My Robot"] [-assetName MyRobot] [-endEffectorLink tool0] ...
    /// </code>
    /// or <c>-settings file.json</c> (one <see cref="RobotSetupSettings"/>) or
    /// <c>-settingsList file.json</c> (<c>{"robots": [ ... ]}</c>). See <see cref="SettingsFromArguments"/>.
    /// </remarks>
    public static class RobotSetup
    {
        public const string EndEffectorHandleName = "EndEffectorHandle";
        public const string InteractionObjectName = "Robot IK Manager";
        public const string GhostsObjectName = "RobotGhosts";
        public const string PlanningFrameObjectName = "PlanningFrame";
        private const float HandleDiameter = 0.08f;

        [Serializable]
        private class SettingsList
        {
            public RobotSetupSettings[] robots = Array.Empty<RobotSetupSettings>();
        }

        // ---------------------------------------------------------------------------------
        // Describing (no assets are touched)

        /// <summary>
        /// Read the files the settings name and answer every question about the robot, without
        /// importing or writing anything. Null when the URDF cannot be read; the report says why.
        /// </summary>
        public static ResolvedRobot Describe(RobotSetupSettings settings, RobotSetupReport report) =>
            Describe(settings, report, out _);

        internal static ResolvedRobot Describe(RobotSetupSettings settings, RobotSetupReport report, out UrdfModel urdf)
        {
            urdf = null;
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.urdfPath) || !File.Exists(settings.urdfPath))
            {
                report.Problem($"No URDF file at '{settings.urdfPath}'.");
                return null;
            }

            try { urdf = UrdfModel.Parse(File.ReadAllText(settings.urdfPath)); }
            catch (FormatException e) { report.Problem(e.Message); return null; }

            SrdfModel srdf = null;
            if (!string.IsNullOrWhiteSpace(settings.srdfPath))
            {
                if (!File.Exists(settings.srdfPath)) report.Problem($"No SRDF file at '{settings.srdfPath}'.");
                else
                {
                    try { srdf = SrdfModel.Parse(File.ReadAllText(settings.srdfPath)); }
                    catch (FormatException e) { report.Problem(e.Message); }
                }
            }
            else report.Found("No SRDF: planning groups, named poses and the gripper come from the typed values, or are guessed from the URDF.");

            RosNamesFile ros = null;
            if (!string.IsNullOrWhiteSpace(settings.rosNamesPath))
            {
                if (!File.Exists(settings.rosNamesPath)) report.Problem($"No ROS-names file at '{settings.rosNamesPath}'.");
                else
                {
                    try { ros = RosNamesFile.Parse(File.ReadAllText(settings.rosNamesPath)); }
                    catch (FormatException e) { report.Problem(e.Message); }
                }
            }
            else report.Found("No ROS-names file: topics, links and the initial pose come from the typed values, the SRDF, or defaults.");

            return ResolvedRobot.Resolve(urdf, srdf, ros, settings, report);
        }

        // ---------------------------------------------------------------------------------
        // Running

        /// <summary>
        /// Make (or update) the robot the settings describe. Never throws for a description it
        /// cannot handle: the result's report says what is wrong and whether anything was written.
        /// </summary>
        public static RobotSetupResult Run(RobotSetupSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var result = new RobotSetupResult();
            RobotSetupReport report = result.Report;
            string assetName = settings.ResolvedAssetName;
            report.Title = $"Robot Setup: {(string.IsNullOrWhiteSpace(settings.displayName) ? assetName : settings.displayName.Trim())}";

            string folder = NormaliseFolder(settings.outputFolder);
            if (folder == null)
            {
                report.Problem($"The output folder '{settings.outputFolder}' is not inside this project's Assets or Packages folder.");
                return result;
            }
            UnityEditor.PackageManager.PackageInfo package = folder.StartsWith("Packages/", StringComparison.Ordinal)
                ? UnityEditor.PackageManager.PackageInfo.FindForAssetPath(folder) : null;
            if (folder.StartsWith("Packages/", StringComparison.Ordinal) &&
                (package == null || (package.source != UnityEditor.PackageManager.PackageSource.Embedded && package.source != UnityEditor.PackageManager.PackageSource.Local)))
            {
                report.Problem($"The output folder '{folder}' is not inside a package this project can write to. " +
                               "Use a folder under Assets, or a package that lives in the project's Packages folder (it needs its package.json first).");
                return result;
            }
            result.ReportPath = $"{folder}/{assetName}.setup.md";

            try
            {
                RunInto(settings, folder, assetName, result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Problem($"Robot Setup stopped on an unexpected error: {e.GetType().Name}: {e.Message}");
            }

            if (Directory.Exists(folder))
            {
                File.WriteAllText(result.ReportPath, report.ToMarkdown());
                AssetDatabase.ImportAsset(result.ReportPath);
            }
            Debug.Log($"[RobotSetup] {report.Title}: {(result.Succeeded ? "written to " + folder : "nothing written")}. {report.Summary} Report: {result.ReportPath}");
            return result;
        }

        private static void RunInto(RobotSetupSettings settings, string folder, string assetName, RobotSetupResult result)
        {
            RobotSetupReport report = result.Report;

            // 1. The description, before anything is written.
            var staged = new RobotSetupSettings();
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(settings), staged);
            if (string.IsNullOrWhiteSpace(settings.urdfPath) || !File.Exists(settings.urdfPath))
            {
                report.Problem($"No URDF file at '{settings.urdfPath}'.");
                return;
            }
            UrdfModel urdf;
            try { urdf = UrdfModel.Parse(File.ReadAllText(settings.urdfPath)); }
            catch (FormatException e) { report.Problem(e.Message); return; }
            if (string.IsNullOrEmpty(urdf.RootLink)) { foreach (string p in urdf.Problems) report.Problem("URDF: " + p); return; }

            // 2. The files, in the output folder.
            Directory.CreateDirectory(folder);
            if (!StageFiles(settings, staged, urdf, folder, report)) return;
            AssetDatabase.Refresh();

            // 3. Every question answered.
            ResolvedRobot robot = Describe(staged, report, out urdf);
            result.Robot = robot;
            if (robot == null) return;
            if (robot.joints.Count == 0) return;

            // 4. The profile (so the prefab can point at it), then the import and the prefabs.
            result.ProfilePath = $"{folder}/{assetName}.asset";
            result.PrefabPath = $"{folder}/{assetName}.prefab";
            result.GhostPath = $"{folder}/{assetName}Ghost.prefab";

            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(result.ProfilePath);
            bool updating = profile != null;
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<RobotProfile>();
                robot.ApplyTo(profile);
                AssetDatabase.CreateAsset(profile, result.ProfilePath);
            }
            else
            {
                robot.ApplyTo(profile);
                EditorUtility.SetDirty(profile);
                report.Found("The profile existed: limit overrides and margins, the velocity switch, the ROS name rule and the gripper speed were kept.");
            }

            GameObject imported = RobotImport.Import(staged.urdfPath, report);
            if (imported == null) { AssetDatabase.SaveAssets(); return; }

            try
            {
                imported.name = assetName;
                imported.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                RobotImport.Compare(imported, urdf, report);

                Wire(imported, robot, profile, report);
                PrefabUtility.SaveAsPrefabAsset(imported, result.PrefabPath, out bool savedRobot);
                if (!savedRobot) { report.Problem($"Could not save the robot prefab '{result.PrefabPath}'."); return; }

                StripToGhost(imported);
                imported.name = assetName + "Ghost";
                GameObject ghost = PrefabUtility.SaveAsPrefabAsset(imported, result.GhostPath, out bool savedGhost);
                if (!savedGhost) report.Problem($"Could not save the ghost prefab '{result.GhostPath}'.");
                else profile.ghostPrefab = ghost;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imported);
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            result.Succeeded = true;
            report.Found(updating ? "Updated in place: the prefabs and the profile keep their GUIDs." : "Created the prefabs and the profile.");

            // 5. What is wrong with what was written.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            Check(prefab, profile, report);

            report.Written.Add(result.PrefabPath);
            report.Written.Add(result.GhostPath);
            report.Written.Add(result.ProfilePath);
            report.Written.Add(result.ReportPath);
        }

        // ---------------------------------------------------------------------------------
        // Files

        // "Assets/x" or "Packages/y/z" without a trailing slash; null when the folder is elsewhere.
        private static string NormaliseFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            string path = folder.Replace('\\', '/').TrimEnd('/');
            string project = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
            if (Path.IsPathRooted(path))
            {
                string full = Path.GetFullPath(path).Replace('\\', '/');
                if (!full.StartsWith(project, StringComparison.Ordinal)) return null;
                path = full.Substring(project.Length);
            }
            return path.StartsWith("Assets/", StringComparison.Ordinal) || path == "Assets" ||
                   (path.StartsWith("Packages/", StringComparison.Ordinal) && path.Length > "Packages/".Length)
                ? path : null;
        }

        // Copies the URDF, the SRDF, the ROS-names file and the meshes the URDF names into the
        // output folder (unless they are there already), and points the staged settings at the
        // copies. A file that is already identical is left alone, so nothing is reimported.
        private static bool StageFiles(RobotSetupSettings settings, RobotSetupSettings staged, UrdfModel urdf, string folder, RobotSetupReport report)
        {
            string sourceDir = Path.GetDirectoryName(Path.GetFullPath(settings.urdfPath));
            string targetDir = Path.GetFullPath(folder);
            bool inPlace = SamePath(sourceDir, targetDir);

            var missing = new List<string>();
            int copied = 0;
            foreach (string reference in urdf.MeshFiles)
            {
                if (!TryRelativeMeshPath(reference, out string relative))
                {
                    // An absolute path: the importer copies such a file into the project itself.
                    string absolute = reference.StartsWith("file://", StringComparison.Ordinal) ? reference.Substring("file://".Length) : reference;
                    if (!File.Exists(absolute)) missing.Add($"{reference}");
                    continue;
                }

                string source = Path.Combine(sourceDir, relative);
                if (!File.Exists(source)) { missing.Add($"{reference} (looked for {source})"); continue; }
                if (inPlace) continue;

                if (CopyIfDifferent(source, Path.Combine(targetDir, relative))) copied++;
                foreach (string texture in TexturesOf(source))
                    if (CopyIfDifferent(texture, Path.Combine(Path.GetDirectoryName(Path.Combine(targetDir, relative)), Path.GetFileName(texture)))) copied++;
            }

            if (missing.Count > 0)
            {
                report.Problem($"{missing.Count} mesh file{(missing.Count == 1 ? "" : "s")} the URDF names could not be found. " +
                               "A mesh named package://<package>/<path> is looked for at <package>/<path> next to the URDF. Missing: " +
                               string.Join("; ", missing.Take(8)) + (missing.Count > 8 ? "; …" : ""));
                return false;
            }

            staged.urdfPath = Stage(settings.urdfPath, folder, inPlace, ref copied);
            if (!string.IsNullOrWhiteSpace(settings.srdfPath) && File.Exists(settings.srdfPath))
                staged.srdfPath = Stage(settings.srdfPath, folder, false, ref copied);
            if (!string.IsNullOrWhiteSpace(settings.rosNamesPath) && File.Exists(settings.rosNamesPath))
                staged.rosNamesPath = Stage(settings.rosNamesPath, folder, false, ref copied);

            report.Found(inPlace
                ? $"The URDF is in the output folder; its {urdf.MeshFiles.Count} mesh files were found next to it."
                : $"Copied the description into '{folder}': the URDF{(string.IsNullOrEmpty(staged.srdfPath) ? "" : ", the SRDF")}{(string.IsNullOrEmpty(staged.rosNamesPath) ? "" : ", the ROS-names file")} and the {urdf.MeshFiles.Count} mesh files the URDF names ({copied} file{(copied == 1 ? "" : "s")} new or changed).");
            return true;
        }

        private static string Stage(string file, string folder, bool inPlace, ref int copied)
        {
            string target = $"{folder}/{Path.GetFileName(file)}";
            if (!inPlace && !SamePath(Path.GetFullPath(file), Path.GetFullPath(target)) && CopyIfDifferent(file, Path.GetFullPath(target))) copied++;
            return target;
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);

        // package://pkg/a/b.dae -> pkg/a/b.dae; a relative path stays; false for an absolute one.
        private static bool TryRelativeMeshPath(string reference, out string relative)
        {
            relative = null;
            foreach (string scheme in new[] { "package://", "model://" })
                if (reference.StartsWith(scheme, StringComparison.Ordinal))
                {
                    relative = reference.Substring(scheme.Length);
                    return true;
                }
            if (reference.StartsWith("file://", StringComparison.Ordinal) || Path.IsPathRooted(reference)) return false;
            relative = reference.StartsWith("../", StringComparison.Ordinal) ? reference.Substring(3) : reference;
            return true;
        }

        private static bool CopyIfDifferent(string source, string target)
        {
            if (File.Exists(target))
            {
                var a = new FileInfo(source);
                var b = new FileInfo(target);
                if (a.Length == b.Length && File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target))) return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, true);
            return true;
        }

        // Image files a Collada mesh names next to itself.
        private static IEnumerable<string> TexturesOf(string meshFile)
        {
            if (!meshFile.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)) yield break;
            string text;
            try { text = File.ReadAllText(meshFile); }
            catch (IOException) { yield break; }
            foreach (Match match in Regex.Matches(text, @"<init_from>\s*([^<]+?\.(png|jpg|jpeg|tga|bmp|tif|tiff))\s*</init_from>", RegexOptions.IgnoreCase))
            {
                string name = Uri.UnescapeDataString(match.Groups[1].Value);
                if (name.StartsWith("file://", StringComparison.Ordinal)) name = name.Substring("file://".Length);
                string path = Path.Combine(Path.GetDirectoryName(meshFile), name);
                if (File.Exists(path)) yield return path;
            }
        }

        // ---------------------------------------------------------------------------------
        // Wiring

        /// <summary>
        /// Add ERUPT's components to an imported robot: what makes a prefab of it a robot that
        /// works when dropped into a scene with the ERUPT root.
        /// </summary>
        internal static void Wire(GameObject root, ResolvedRobot robot, RobotProfile profile, RobotSetupReport report)
        {
            RobotImport.PrepareForErupt(root, report);
            NameUniquely(root);

            Transform endEffector = FindLink(root.transform, robot.endEffectorLink);
            Transform frameLink = FindLink(root.transform, robot.planningFrameLink);
            if (endEffector == null)
                report.Problem($"The imported robot has no link named '{robot.endEffectorLink}': the IK controller and the end-effector handle have nothing to follow.");

            // The planning frame: where this robot's ROS side has the origin of the frame it
            // plans in, with ERUPT's axes (ROS x, y, z = Unity x, z, y). A link lies along the
            // importer's axes (ROS x = Unity z), hence the quarter turn.
            var frame = new GameObject(PlanningFrameObjectName);
            frame.transform.SetParent(root.transform, false);
            Transform at = frameLink != null ? frameLink : root.transform;
            frame.transform.SetPositionAndRotation(at.position, at.rotation * Quaternion.Euler(0f, -90f, 0f));

            // Interaction: the IK controller, the interaction controller with its handle, the router binding.
            var interactionObject = new GameObject(InteractionObjectName);
            interactionObject.transform.SetParent(root.transform, false);
            var controller = interactionObject.AddComponent<DirectArticulationIKController>();
            var interaction = interactionObject.AddComponent<RobotInteractionController>();
            var binding = interactionObject.AddComponent<RobotInteractionRouterBinding>();

            GameObject handle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            handle.name = EndEffectorHandleName;
            handle.transform.SetParent(interactionObject.transform, false);
            handle.transform.localScale = Vector3.one * HandleDiameter;
            if (endEffector != null) handle.transform.position = endEffector.position;
            handle.GetComponent<SphereCollider>().isTrigger = true;

            // Previews: the ghosts and the trajectory player.
            var ghostsObject = new GameObject(GhostsObjectName);
            ghostsObject.transform.SetParent(root.transform, false);
            var ghosts = ghostsObject.AddComponent<RobotGhosts>();
            var player = ghostsObject.AddComponent<JointTrajectoryPlayer>();
            ghosts.realRobot = root;

            var instance = root.GetComponent<RobotInstance>();
            if (instance == null) instance = root.AddComponent<RobotInstance>();

            Set(controller, ("robotRoot", root.transform), ("endEffector", endEffector));
            Set(interaction, ("ikController", controller), ("endEffector", endEffector), ("handle", handle.transform),
                ("handleRenderer", handle.GetComponent<Renderer>()));
            Set(binding, ("robotInteraction", interaction));
            Set(player, ("ikController", controller));
            Set(instance, ("profile", profile), ("controller", controller), ("interaction", interaction),
                ("trajectoryPlayer", player), ("ghosts", ghosts), ("planningFrame", frame.transform));
        }

        // The importer names the objects that hold a link's geometry alike on every link
        // ("Visuals", "Collisions", "unnamed"). When a prefab is saved over an existing one,
        // Unity keeps the file IDs of the objects it can match by name, so every object gets a
        // name of its own: the link it belongs to in front. Running Robot Setup again then
        // rewrites the prefab without changing what scenes and variants refer to.
        private static void NameUniquely(GameObject root)
        {
            var used = new HashSet<string> { root.name };
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t != root.transform && RobotImport.IsLink(t)) used.Add(t.name);

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform || RobotImport.IsLink(t)) continue;
                // Inside a mesh's own prefab only its root is an object of this prefab.
                if (PrefabUtility.IsPartOfPrefabInstance(t) && PrefabUtility.GetNearestPrefabInstanceRoot(t) != t.gameObject) continue;

                Transform link = t.parent;
                while (link != null && link != root.transform && !RobotImport.IsLink(link)) link = link.parent;
                string owner = link != null && link != root.transform ? link.name + " " : "";
                string wanted = owner + t.name, unique = wanted;
                for (int n = 2; !used.Add(unique); n++) unique = $"{wanted} {n}";
                t.name = unique;
            }
        }

        private static void Set(UnityEngine.Object target, params (string property, UnityEngine.Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            foreach ((string property, UnityEngine.Object value) in references)
            {
                SerializedProperty field = serialized.FindProperty(property);
                if (field == null) throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{property}'.");
                field.objectReferenceValue = value;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Turns the wired robot into its ghost: the bare articulated model, shown translucent
        // as the start and goal previews. No scripts, no colliders, none of ERUPT's objects.
        private static void StripToGhost(GameObject root)
        {
            foreach (string name in new[] { InteractionObjectName, GhostsObjectName, PlanningFrameObjectName })
            {
                Transform child = root.transform.Find(name);
                if (child != null) UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            RobotImport.RemoveCollisionObjects(root);
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(collider);

            // A component another one requires cannot go first; a few passes take them all.
            for (int pass = 0; pass < 4; pass++)
            {
                MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
                if (behaviours.Length == 0) break;
                foreach (MonoBehaviour behaviour in behaviours)
                    if (behaviour != null && CanRemove(behaviour)) UnityEngine.Object.DestroyImmediate(behaviour);
            }
        }

        // False while another component on the object declares that it requires this one.
        private static bool CanRemove(Component component)
        {
            Type type = component.GetType();
            foreach (Component other in component.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (RequireComponent require in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    foreach (Type required in new[] { require.m_Type0, require.m_Type1, require.m_Type2 })
                        if (required != null && required.IsAssignableFrom(type)) return false;
            }
            return true;
        }

        /// <summary>The link object of that name under an imported robot: an articulated one before any other object of the same name.</summary>
        internal static Transform FindLink(Transform root, string linkName)
        {
            if (string.IsNullOrEmpty(linkName)) return null;
            Transform fallback = null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != linkName) continue;
                if (RobotImport.IsLink(t)) return t;
                fallback ??= t;
            }
            return fallback;
        }

        // ---------------------------------------------------------------------------------
        // Checks on what was written

        private static void Check(GameObject prefab, RobotProfile profile, RobotSetupReport report)
        {
            if (prefab == null) { report.Problem("The robot prefab could not be loaded after it was saved."); return; }

            var instance = prefab.GetComponent<RobotInstance>();
            if (instance == null) { report.Problem("The robot prefab has no RobotInstance."); return; }

            // RobotInstance.Validate covers the profile's own problems and the joints and links it names.
            foreach (string problem in instance.Validate()) report.Problem(problem);

            // References the wiring needs.
            foreach (Component component in new Component[]
                     {
                         instance, instance.Controller, instance.Interaction, instance.TrajectoryPlayer, instance.Ghosts,
                         prefab.GetComponentInChildren<RobotInteractionRouterBinding>(true),
                     })
            {
                if (component == null) { report.Problem("A component of the robot is missing from the prefab."); continue; }
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                while (property.NextVisible(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (property.objectReferenceValue != null) continue;
                    // Found in the scene when the robot is used; empty in a prefab on purpose.
                    if (property.name == "m_Script" || property.name == "router" || property.name == "selectionService" || property.name == "robotPrefab") continue;
                    report.Problem($"{component.GetType().Name}.{property.name} is not assigned in the prefab.");
                }
            }
            if (profile.ghostPrefab == null) report.Problem("The profile has no ghost prefab: start and goal previews will not show.");

            // Meshes and materials the import could not resolve.
            int noMesh = prefab.GetComponentsInChildren<MeshFilter>(true).Count(f => f.sharedMesh == null);
            int noCollisionMesh = prefab.GetComponentsInChildren<MeshCollider>(true).Count(c => c.sharedMesh == null);
            int noMaterial = prefab.GetComponentsInChildren<Renderer>(true).Count(r => r.sharedMaterials.Length == 0 || r.sharedMaterials.Any(m => m == null));
            if (noMesh > 0) report.Problem($"{noMesh} visual{(noMesh == 1 ? " has" : "s have")} no mesh in the prefab.");
            if (noCollisionMesh > 0) report.Problem($"{noCollisionMesh} collider{(noCollisionMesh == 1 ? " has" : "s have")} no mesh in the prefab.");
            if (noMaterial > 0) report.Warn($"{noMaterial} renderer{(noMaterial == 1 ? " has" : "s have")} a missing material in the prefab.");
            if (prefab.GetComponentsInChildren<Renderer>(true).Length <= 1)
                report.Warn("The robot has no visual geometry besides the end-effector handle.");
            if (prefab.GetComponentsInChildren<Collider>(true).Length <= 1)
                report.Warn("The robot has no collision geometry: its links cannot be selected or grabbed, only the handles.");
        }

        // ---------------------------------------------------------------------------------
        // Command line

        /// <summary>The batch entry point; see the class remarks. Exits with an error when a robot could not be written.</summary>
        public static void RunFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            var all = new List<RobotSetupSettings>();

            string listFile = Argument(args, "-settingsList");
            string oneFile = Argument(args, "-settings");
            if (listFile != null)
            {
                var list = new SettingsList();
                EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(listFile), list);
                all.AddRange(list.robots);
            }
            else if (oneFile != null)
            {
                var one = new RobotSetupSettings();
                EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(oneFile), one);
                all.Add(SettingsFromArguments(args, one));
            }
            else all.Add(SettingsFromArguments(args, new RobotSetupSettings()));

            string only = Argument(args, "-only");
            var failed = new List<string>();
            foreach (RobotSetupSettings settings in all)
            {
                if (only != null && !only.Split(',').Contains(settings.ResolvedAssetName)) continue;
                RobotSetupResult result = Run(settings);
                foreach (string problem in result.Report.Texts(RobotSetupLevel.Problem))
                    Debug.LogWarning($"[RobotSetup] {settings.ResolvedAssetName}: {problem}");
                if (!result.Succeeded) failed.Add(settings.ResolvedAssetName);
            }
            if (failed.Count > 0)
                throw new InvalidOperationException("Robot Setup could not write: " + string.Join(", ", failed) + ". See the reports.");
        }

        /// <summary>
        /// Settings from command-line arguments, on top of <paramref name="settings"/>:
        /// <c>-urdf -srdf -rosNames -out -name -assetName -planningFrame -baseLink -endEffectorLink
        /// -armGroup -armJoints a,b -gripperGroup -gripperJoints a,b -gripperDriver -gripperOpen
        /// -gripperClosed -gripperOpenWidth -noGripper -initialPose -initialPoseValues j=v,j=v
        /// -jointStatesTopic -trajectoryTopic</c>.
        /// </summary>
        public static RobotSetupSettings SettingsFromArguments(string[] args, RobotSetupSettings settings)
        {
            settings.urdfPath = Argument(args, "-urdf") ?? settings.urdfPath;
            settings.srdfPath = Argument(args, "-srdf") ?? settings.srdfPath;
            settings.rosNamesPath = Argument(args, "-rosNames") ?? settings.rosNamesPath;
            settings.outputFolder = Argument(args, "-out") ?? settings.outputFolder;
            settings.displayName = Argument(args, "-name") ?? settings.displayName;
            settings.assetName = Argument(args, "-assetName") ?? settings.assetName;
            settings.planningFrame = Argument(args, "-planningFrame") ?? settings.planningFrame;
            settings.baseLink = Argument(args, "-baseLink") ?? settings.baseLink;
            settings.endEffectorLink = Argument(args, "-endEffectorLink") ?? settings.endEffectorLink;
            settings.armGroup = Argument(args, "-armGroup") ?? settings.armGroup;
            settings.gripperGroup = Argument(args, "-gripperGroup") ?? settings.gripperGroup;
            settings.gripperDriverJoint = Argument(args, "-gripperDriver") ?? settings.gripperDriverJoint;
            settings.initialPose = Argument(args, "-initialPose") ?? settings.initialPose;
            settings.jointStatesTopic = Argument(args, "-jointStatesTopic") ?? settings.jointStatesTopic;
            settings.trajectoryCommandTopic = Argument(args, "-trajectoryTopic") ?? settings.trajectoryCommandTopic;
            if (Argument(args, "-armJoints") is string arm) settings.armJoints = Names(arm);
            if (Argument(args, "-gripperJoints") is string gripper) settings.gripperJoints = Names(gripper);
            if (Array.IndexOf(args, "-noGripper") >= 0) settings.noGripper = true;

            string open = Argument(args, "-gripperOpen"), closed = Argument(args, "-gripperClosed");
            if (open != null || closed != null)
            {
                if (open == null || closed == null) throw new ArgumentException("-gripperOpen and -gripperClosed go together.");
                settings.setGripperPositions = true;
                settings.gripperOpen = Number(open, "-gripperOpen");
                settings.gripperClosed = Number(closed, "-gripperClosed");
            }
            if (Argument(args, "-gripperOpenWidth") is string width) settings.gripperOpenWidth = Number(width, "-gripperOpenWidth");
            if (Argument(args, "-initialPoseValues") is string values)
                settings.initialPoseValues = Names(values).Select(pair =>
                {
                    string[] parts = pair.Split('=');
                    if (parts.Length != 2) throw new ArgumentException($"-initialPoseValues: '{pair}' is not joint=value.");
                    return new JointValue { joint = parts[0].Trim(), value = Number(parts[1], "-initialPoseValues") };
                }).ToArray();
            return settings;
        }

        private static string[] Names(string list) =>
            list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();

        private static float Number(string text, string flag) =>
            float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value : throw new ArgumentException($"{flag}: '{text}' is not a number.");

        private static string Argument(string[] args, string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
