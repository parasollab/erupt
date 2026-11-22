using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
#if ERUPT_URDF_IMPORTER
using Unity.Robotics.UrdfImporter;
#endif

namespace Erupt.Editor
{
    /// <summary>
    /// The part of Robot Setup that talks to the URDF Importer package: running the import,
    /// taking the importer's own control scripts off the result, and comparing what was
    /// imported with what the URDF says. Without the package the import reports that it is
    /// missing and everything else does nothing.
    /// </summary>
    internal static class RobotImport
    {
#if ERUPT_URDF_IMPORTER
        public const bool Available = true;

        /// <summary>Import a URDF that is inside the project. The robot is created in the open scene; the caller destroys it.</summary>
        public static GameObject Import(string urdfAssetPath, RobotSetupReport report)
        {
            // Convex colliders made by Unity from the collision meshes: the robot's colliders
            // are only raycast (selection, grabbing), never in contact, so a hull per mesh is enough.
            var settings = new ImportSettings
            {
                chosenAxis = ImportSettings.axisType.yAxis,
                convexMethod = ImportSettings.convexDecomposer.unity,
                rootMotion = ImportSettings.rootMotionType.fixedToWorld,
            };

            PrepareStlMeshes(urdfAssetPath, report);

            // The importer parents the robot under whatever is selected.
            Object[] selection = Selection.objects;
            Selection.activeObject = null;
            GameObject robot = null;
            try
            {
                IEnumerator<GameObject> create = UrdfRobotExtensions.Create(Path.GetFullPath(urdfAssetPath), settings);
                while (create.MoveNext())
                    if (create.Current != null) robot = create.Current;
            }
            finally
            {
                Selection.objects = selection;
            }

            if (robot == null)
            {
                report.Problem($"The URDF Importer could not import '{urdfAssetPath}'; see the Console for what it reported.");
                return null;
            }
            Undo.ClearUndo(robot);
            report.Found($"Imported '{urdfAssetPath}' with the URDF Importer: {robot.GetComponentsInChildren<UrdfLink>(true).Length} links, " +
                         $"{robot.GetComponentsInChildren<ArticulationBody>(true).Length} articulation bodies.");
            return robot;
        }

        // The importer turns each STL mesh into a prefab the first time it meets it, but only
        // under Assets: it leaves an STL inside a package alone and then cannot find its prefab.
        // A package in the project's Packages folder is as writable as Assets, so the same
        // prefab is made here, with the importer's own STL reader and under the names it expects.
        private static void PrepareStlMeshes(string urdfAssetPath, RobotSetupReport report)
        {
            string folder = Path.GetDirectoryName(urdfAssetPath).Replace('\\', '/');
            if (!folder.StartsWith("Packages/")) return;

            UrdfModel urdf = UrdfModel.Parse(File.ReadAllText(urdfAssetPath));
            int made = 0;
            foreach (string reference in urdf.MeshFiles)
            {
                if (!reference.EndsWith(".stl", System.StringComparison.OrdinalIgnoreCase)) continue;
                string relative = reference;
                foreach (string scheme in new[] { "package://", "model://" })
                    if (reference.StartsWith(scheme)) relative = reference.Substring(scheme.Length);
                if (relative.StartsWith("file://") || Path.IsPathRooted(relative)) continue;
                if (relative.StartsWith("../")) relative = relative.Substring(3);

                string stl = $"{folder}/{relative}";
                string prefabPath = StlAssetPostProcessor.GetPrefabAssetPath(stl);
                if (!File.Exists(stl) || File.Exists(prefabPath)) continue;

                Mesh[] meshes = StlImporter.ImportMesh(stl);
                if (meshes == null) { report.Warn($"The STL mesh '{stl}' could not be read."); continue; }

                // As the importer does: the built-in pipeline's default material, none otherwise
                // (a visual gets the URDF's material when the robot is built).
                Material material = Unity.Robotics.MaterialExtensions.GetRenderPipelineType() == Unity.Robotics.MaterialExtensions.RenderPipelineType.Standard
                    ? AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat") : null;
                var parent = new GameObject(Path.GetFileNameWithoutExtension(stl));
                for (int i = 0; i < meshes.Length; i++)
                {
                    string meshPath = StlAssetPostProcessor.GetMeshAssetPath(stl, i);
                    AssetDatabase.CreateAsset(meshes[i], meshPath);
                    var part = new GameObject(Path.GetFileNameWithoutExtension(meshPath));
                    part.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    part.AddComponent<MeshRenderer>().sharedMaterial = material;
                    part.transform.SetParent(parent.transform, false);
                }
                PrefabUtility.SaveAsPrefabAsset(parent, prefabPath);
                Object.DestroyImmediate(parent);
                made++;
            }
            if (made > 0) report.Found($"Made the mesh prefabs of {made} STL files (the importer does not do that inside a package).");
        }

        /// <summary>
        /// Take off what the importer adds to drive the robot itself: its keyboard controller
        /// and the components that move mimic joints. ERUPT's controller drives every joint,
        /// followers included, from the profile.
        /// </summary>
        public static void PrepareForErupt(GameObject root, RobotSetupReport report)
        {
            int removed = 0;
            foreach (UrdfMimicJoint mimic in root.GetComponentsInChildren<UrdfMimicJoint>(true)) { Object.DestroyImmediate(mimic); removed++; }
            foreach (global::JointControl control in root.GetComponentsInChildren<global::JointControl>(true)) { Object.DestroyImmediate(control); removed++; }
            foreach (Unity.Robotics.UrdfImporter.Control.Controller controller in root.GetComponentsInChildren<Unity.Robotics.UrdfImporter.Control.Controller>(true)) { Object.DestroyImmediate(controller); removed++; }
            foreach (Unity.Robotics.UrdfImporter.Control.FKRobot fk in root.GetComponentsInChildren<Unity.Robotics.UrdfImporter.Control.FKRobot>(true)) { Object.DestroyImmediate(fk); removed++; }
            foreach (Unity.Robotics.UrdfImporter.Control.UrdfBaseController mobile in root.GetComponentsInChildren<Unity.Robotics.UrdfImporter.Control.UrdfBaseController>(true)) { Object.DestroyImmediate(mobile); removed++; }
            if (removed > 0)
                report.Found($"Removed {removed} of the importer's own control components (keyboard controller, mimic drivers): ERUPT's controller drives the joints.");
        }

        /// <summary>Destroy the objects that hold the links' collision geometry.</summary>
        public static void RemoveCollisionObjects(GameObject root)
        {
            foreach (UrdfCollisions collisions in root.GetComponentsInChildren<UrdfCollisions>(true))
                if (collisions != null) Object.DestroyImmediate(collisions.gameObject);
        }

        public static bool IsLink(Transform t) => t.GetComponent<UrdfLink>() != null;

        /// <summary>What the import lacks or has differently from the URDF: joints, joint kinds, limits, link frames.</summary>
        public static void Compare(GameObject imported, UrdfModel urdf, RobotSetupReport report)
        {
            var byName = new Dictionary<string, UrdfJoint>();
            foreach (UrdfJoint joint in imported.GetComponentsInChildren<UrdfJoint>(true))
                if (!string.IsNullOrEmpty(joint.jointName) && !byName.ContainsKey(joint.jointName)) byName[joint.jointName] = joint;

            int limitDifferences = 0;
            foreach (UrdfJointInfo joint in urdf.Joints)
            {
                if (!joint.IsMovable) continue;
                if (!byName.TryGetValue(joint.name, out UrdfJoint component))
                {
                    report.Problem($"The import lacks the joint '{joint.name}' ({joint.typeName}, {joint.parent} -> {joint.child}) of the URDF.");
                    continue;
                }

                ArticulationBody body = component.GetComponent<ArticulationBody>();
                bool prismatic = joint.type == UrdfJointType.Prismatic;
                ArticulationJointType expected = prismatic ? ArticulationJointType.PrismaticJoint : ArticulationJointType.RevoluteJoint;
                if (body == null || body.jointType != expected)
                {
                    report.Problem($"Joint '{joint.name}' is {joint.typeName} in the URDF but was imported as {(body == null ? "nothing" : body.jointType.ToString())}.");
                    continue;
                }
                if (component.name != joint.child)
                    report.Problem($"Joint '{joint.name}' moves the link '{joint.child}' in the URDF but sits on '{component.name}' in the import.");

                if (joint.type == UrdfJointType.Continuous || !joint.hasRange) continue;
                ArticulationDrive drive = body.xDrive;
                float scale = prismatic ? 1f : Mathf.Deg2Rad;
                float tolerance = prismatic ? 1e-5f : 1e-3f;
                float lower = drive.lowerLimit * scale, upper = drive.upperLimit * scale;
                if (Mathf.Abs(lower - joint.lower) > tolerance || Mathf.Abs(upper - joint.upper) > tolerance)
                {
                    limitDifferences++;
                    report.Warn($"Joint '{joint.name}': the import has the limits [{Num(lower)}, {Num(upper)}], the URDF [{Num(joint.lower)}, {Num(joint.upper)}]. " +
                                "The profile has the URDF's, and those are the ones ERUPT enforces.");
                }
            }
            if (limitDifferences == 0) report.Found("Joint limits in the import agree with the URDF.");

            // Link frames, with every joint at zero. The importer lays a link out as
            // Unity (x, y, z) = ROS (-y, z, x).
            int moved = 0;
            Matrix4x4 toRoot = imported.transform.worldToLocalMatrix;
            foreach (string link in urdf.Links)
            {
                Transform t = RobotSetup.FindLink(imported.transform, link);
                if (t == null)
                {
                    report.Problem($"The import lacks the link '{link}' of the URDF.");
                    continue;
                }
                Vector3 ros = urdf.LinkPosition(link);
                var expected = new Vector3(-ros.y, ros.z, ros.x);
                Vector3 actual = toRoot.MultiplyPoint3x4(t.position);
                if (Vector3.Distance(expected, actual) > 1e-3f)
                {
                    moved++;
                    report.Warn($"Link '{link}' stands {Vector3.Distance(expected, actual) * 1000f:F1} mm from where the URDF puts it.");
                }
            }
            if (moved == 0) report.Found("Link frames in the import agree with the URDF.");

            // A link's visual and collision geometry describe the same part, so they overlap.
            // When they do not, one of the two meshes was read with the wrong axes or scale.
            int apart = 0, compared = 0;
            foreach (UrdfLink link in imported.GetComponentsInChildren<UrdfLink>(true))
            {
                UrdfVisuals visuals = null;
                UrdfCollisions collisions = null;
                foreach (Transform child in link.transform)
                {
                    if (visuals == null) visuals = child.GetComponent<UrdfVisuals>();
                    if (collisions == null) collisions = child.GetComponent<UrdfCollisions>();
                }
                if (visuals == null || collisions == null) continue;
                if (!TryBounds(visuals.transform, out Bounds seen) || !TryBounds(collisions.transform, out Bounds solid)) continue;

                compared++;
                float size = Mathf.Max(seen.size.magnitude, solid.size.magnitude);
                bool overlap = seen.Intersects(solid) && Vector3.Distance(seen.center, solid.center) < 0.5f * size;
                float ratio = solid.size.magnitude > 1e-6f ? seen.size.magnitude / solid.size.magnitude : 0f;
                if (!overlap || ratio < 0.4f || ratio > 2.5f)
                {
                    apart++;
                    report.Warn($"Link '{link.name}': its visual geometry (centre {Vec(seen.center)}, size {Vec(seen.size)}) and its collision geometry " +
                                $"(centre {Vec(solid.center)}, size {Vec(solid.size)}) do not match. One of the two meshes may be read with the wrong axes or units.");
                }
            }
            if (compared > 0 && apart == 0) report.Found($"Visual and collision geometry overlap on all {compared} links that have both.");

            ReportPartialHulls(imported, report);
        }

        // A convex collider holds at most 256 polygons. For a collision mesh whose hull has
        // more, Unity keeps part of the hull and says so in the log whenever the mesh is cooked.
        private static void ReportPartialHulls(GameObject imported, RobotSetupReport report)
        {
            var partial = new List<string>();
            var seen = new HashSet<Mesh>();
            bool failed = false;
            void Listen(string condition, string stackTrace, LogType type)
            {
                if (condition.Contains("Couldn't create a Convex Mesh")) failed = true;
            }

            Application.logMessageReceived += Listen;
            try
            {
                foreach (MeshCollider collider in imported.GetComponentsInChildren<MeshCollider>(true))
                {
                    Mesh mesh = collider.sharedMesh;
                    if (mesh == null || !collider.convex || !seen.Add(mesh)) continue;
                    failed = false;
                    Physics.BakeMesh(mesh.GetInstanceID(), true);
                    if (failed) partial.Add(mesh.name);
                }
            }
            finally
            {
                Application.logMessageReceived -= Listen;
            }

            if (partial.Count > 0)
                report.Warn($"{partial.Count} collision mesh{(partial.Count == 1 ? " is" : "es are")} too detailed for one convex collider (Unity's limit is 256 polygons): " +
                            $"{string.Join(", ", partial)}. Unity uses part of the hull and logs \"Couldn't create a Convex Mesh\" when the robot is first created. " +
                            "The colliders only serve to select and grab the links, so the robot works; simpler collision meshes in the description make the message go away.");
        }

        // World bounds of the geometry under a transform, from the meshes and primitive
        // colliders themselves (renderer and collider bounds are not up to date for an object
        // that was just created).
        private static bool TryBounds(Transform root, out Bounds bounds)
        {
            var points = new List<Vector3>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) AddCorners(points, filter.sharedMesh.bounds, filter.transform);
            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                if (collider.sharedMesh != null) AddCorners(points, collider.sharedMesh.bounds, collider.transform);
            foreach (BoxCollider box in root.GetComponentsInChildren<BoxCollider>(true))
                AddCorners(points, new Bounds(box.center, box.size), box.transform);
            foreach (SphereCollider sphere in root.GetComponentsInChildren<SphereCollider>(true))
                AddCorners(points, new Bounds(sphere.center, Vector3.one * (2f * sphere.radius)), sphere.transform);

            bounds = points.Count > 0 ? new Bounds(points[0], Vector3.zero) : default;
            foreach (Vector3 point in points) bounds.Encapsulate(point);
            return points.Count > 0;
        }

        private static void AddCorners(List<Vector3> points, Bounds local, Transform t)
        {
            for (int i = 0; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                points.Add(t.TransformPoint(local.center + Vector3.Scale(local.extents, sign)));
            }
        }

        private static string Vec(Vector3 v) => $"({Num(v.x)}, {Num(v.y)}, {Num(v.z)})";

        private static string Num(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
#else
        public const bool Available = false;

        public static GameObject Import(string urdfAssetPath, RobotSetupReport report)
        {
            report.Problem("The URDF Importer package (com.unity.robotics.urdf-importer) is not installed, so the robot cannot be imported. " +
                           "ERUPT > Project Setup offers to add it.");
            return null;
        }

        public static void PrepareForErupt(GameObject root, RobotSetupReport report) { }
        public static void RemoveCollisionObjects(GameObject root) { }
        public static bool IsLink(Transform t) => t.GetComponent<ArticulationBody>() != null;
        public static void Compare(GameObject imported, UrdfModel urdf, RobotSetupReport report) { }
#endif
    }
}
