using Erupt.Ros;
using Erupt.Environment;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using PoseMsg = RosMessageTypes.Geometry.PoseMsg;
using MeshMsg = RosMessageTypes.Shape.MeshMsg;
using PlaneMsg = RosMessageTypes.Shape.PlaneMsg;
using SolidPrimitiveMsg = RosMessageTypes.Shape.SolidPrimitiveMsg;
using PointMsg = RosMessageTypes.Geometry.PointMsg;
using QuaternionMsg = RosMessageTypes.Geometry.QuaternionMsg;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// The Unity side of the planning-scene mirror: one GameObject per collision object id,
    /// whichever robot's planning scene it came from. It builds, moves, rebuilds and destroys
    /// those objects and knows which ids originate in Unity. It does not talk to ROS: the MoveIt
    /// plugin's <see cref="PlanningSceneFanout"/> listens to every robot's
    /// <see cref="topic"/> and tells this component what to apply, converting poses from the
    /// planning frame of the robot they arrived from.
    /// </summary>
    public class CollisionObjectsListenerSimple : MonoBehaviour
    {
        [Header("ROS")]
        [Tooltip("Relative to each robot's namespace.")]
        public string topic = "collision_objects_ros";

        [Tooltip("MoveIt service used to fetch objects that existed before this app connected. Relative to each robot's namespace.")]
        public string planningSceneServiceTopic = "get_planning_scene";

        [Tooltip("Request a full world-object snapshot from each robot's planning scene after subscribing to live updates.")]
        public bool requestInitialPlanningScene = true;

        [Header("Frame Root")]
        [Tooltip("Parent of the objects built here, and the frame poses are read in when a planning scene names none of its own. If null, this transform.")]
        public GameObject worldOrigin;

        [Tooltip("Environment mirror every spawned or registered object is recorded in. Found in the scene if null.")]
        [SerializeField] private EnvironmentRegistry registry;
        private Transform worldOriginTransform => worldOrigin ? worldOrigin.transform : this.transform;

        [Header("Materials")]
        public Material litMaterial;

        private bool started;
        public Dictionary<string, GameObject> objectsById = new();

        // Ids currently attached to a robot (maintained by the AttachedCollisionObjectListeners).
        // While an id is in here, inbound REMOVE/MOVE/ADD for it are ignored — the object is
        // being carried by a gripper, and the attach notices own its pose.
        public readonly HashSet<string> attachedIds = new();

        // Ids just detached from a robot. The ADD that follows from that robot's planning scene
        // carries the authoritative place pose, which is applied even to Unity-owned objects
        // (the robot moved it, not Unity).
        public readonly HashSet<string> awaitingDetachPose = new();

        // Fired after an inbound ADD/APPEND/MOVE has been applied to the GameObject for this id.
        public event System.Action<string> OnObjectUpdated;

        // Fired when the mirrored objects are forgotten (ClearRegistry, or this component being
        // destroyed: the argument is then true).
        public event System.Action<bool> OnRegistryCleared;

        /// <summary>Start has run: the Unity-owned objects already in the scene are registered, so inbound objects can be told apart from them.</summary>
        public bool HasStarted => started;
        /// <summary>Raised once, at the end of Start.</summary>
        public event System.Action Ready;

        /// <summary>The fan-out this mirror is applied by; null while the MoveIt plugin has none.</summary>
        public PlanningSceneFanout Fanout { get; internal set; }

        // IDs represented by Unity-owned GameObjects. Echoes from a planning scene must
        // not create a second renderer at the same pose.
        private readonly HashSet<string> _publisherOwnedIds = new();

        // id -> signature of the geometry its children were last built from, so a re-ADD with
        // unchanged geometry only moves the object instead of destroying and respawning it.
        private readonly Dictionary<string, string> _geometrySignatureById = new();

        // Id prefixes whose objects only ever originate in this app. A planning-scene object with
        // such an id that no live Unity object owns was left behind by an earlier session;
        // mirroring it would put a duplicate where this session's object goes.
        private readonly HashSet<string> _claimedIdPrefixes = new();

        void Awake()
        {
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
        }

        void Start()
        {
            foreach (var pub in FindObjectsByType<CollisionObjectPublisher>(FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(pub.objectId))
                    RegisterUnityOwnedObject(pub.objectId, pub.gameObject);

            started = true;
            Ready?.Invoke();
        }

        void OnDestroy()
        {
            Fanout?.MirrorDestroyed(this);
            ClearRegistry(destroyObjects: false, tearingDown: true);
        }

        /// <summary>
        /// Forget every object mirrored from ROS, destroying it unless told otherwise. Objects
        /// that originate in Unity stay registered (they are still in the scene and their echoes
        /// must keep being recognised) until the mirror itself is torn down. Listeners such as
        /// AttachedCollisionObjectListener drop their attach state via
        /// <see cref="OnRegistryCleared"/> first, so nothing is left under a robot link.
        /// </summary>
        public void ClearRegistry(bool destroyObjects = true, bool tearingDown = false)
        {
            OnRegistryCleared?.Invoke(tearingDown);

            var mirrored = new List<string>();
            foreach (var kv in objectsById)
                if (!_publisherOwnedIds.Contains(kv.Key)) mirrored.Add(kv.Key);

            foreach (string id in mirrored)
            {
                GameObject go = objectsById[id];
                if (destroyObjects && go)
                {
                    // Only the mirror is dropped; MoveIt keeps the object, so no REMOVE goes out.
                    Silence(go);
                    if (registry != null) registry.Unregister(id, RemovalOrigin.Remote);
                    Destroy(go);
                }
                objectsById.Remove(id);
            }

            if (tearingDown)
            {
                objectsById.Clear();
                _publisherOwnedIds.Clear();
            }
            _geometrySignatureById.Clear();
            attachedIds.Clear();
            awaitingDetachPose.Clear();
        }

        /// <summary>
        /// Registers a GameObject whose ROS id originated in Unity. The planning scenes will echo
        /// that id back, but the mirror must keep the existing object rather than rebuild
        /// coincident geometry on top of it.
        /// </summary>
        public void RegisterUnityOwnedObject(string id, GameObject unityObject)
        {
            if (string.IsNullOrEmpty(id)) return;

            _publisherOwnedIds.Add(id);
            if (unityObject != null)
            {
                objectsById[id] = unityObject;
                // Mirror into the registry. The owner is marked Unity-owned first, so the
                // planning-scene sync leaves publishing to whoever registered it.
                var pub = unityObject.GetComponent<CollisionObjectPublisher>();
                registry?.Adopt(unityObject, id, EnvironmentOwner.Unity, null, pub != null && pub.isMesh);
            }
        }

        public void UnregisterUnityOwnedObject(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _publisherOwnedIds.Remove(id);
            objectsById.Remove(id);
            awaitingDetachPose.Remove(id);
            registry?.Unregister(id, RemovalOrigin.Local);
        }

        /// <summary>
        /// Declares that every planning-scene object whose id starts with <paramref name="prefix"/>
        /// belongs to this app: ones no live Unity object owns are removed from the planning scene
        /// they were reported by instead of mirrored.
        /// </summary>
        public void ClaimIdPrefix(string prefix)
        {
            if (!string.IsNullOrEmpty(prefix)) _claimedIdPrefixes.Add(prefix);
        }

        internal string ClaimedPrefixes => string.Join("/", _claimedIdPrefixes);

        internal bool IsStaleClaimedId(string id)
        {
            if (_publisherOwnedIds.Contains(id) || attachedIds.Contains(id)) return false;
            foreach (string prefix in _claimedIdPrefixes)
                if (id.StartsWith(prefix)) return true;
            return false;
        }

        /// <summary>True when the id was registered as originating in Unity.</summary>
        public bool IsUnityOwned(string id) => !string.IsNullOrEmpty(id) && _publisherOwnedIds.Contains(id);

        internal bool TryGetUnityOwnedObject(string id, out GameObject unityObject)
        {
            unityObject = null;
            if (!_publisherOwnedIds.Contains(id)) return false;

            if (objectsById.TryGetValue(id, out unityObject) && unityObject != null)
                return true;

            // The owner may have been destroyed locally before its REMOVE was echoed.
            _publisherOwnedIds.Remove(id);
            objectsById.Remove(id);
            unityObject = null;
            return false;
        }

        // ---- what the fan-out applies ----------------------------------------------------

        /// <summary>A planning scene removed a Unity-owned object: destroy it without reporting the destruction.</summary>
        internal void DestroyUnityOwned(string id, GameObject unityObject)
        {
            Silence(unityObject);
            registry?.Unregister(id, RemovalOrigin.Remote);
            Destroy(unityObject);
            UnregisterUnityOwnedObject(id);
        }

        /// <summary>A planning scene removed a mirrored object (or it turned out to be stale): destroy its GameObject, if there is one.</summary>
        internal void RemoveMirrored(string id)
        {
            awaitingDetachPose.Remove(id);
            _geometrySignatureById.Remove(id);
            if (objectsById.TryGetValue(id, out var old) && old)
            {
                // This destroy was commanded by ROS — the object's own publishers must not
                // report it and erase the object from the planning scenes that still hold it.
                Silence(old);
                registry?.Unregister(id, RemovalOrigin.Remote);
                Destroy(old);
            }
            objectsById.Remove(id);
        }

        /// <summary>Put a Unity-owned object at the pose a planning scene reports (the place pose after a robot let go of it), keeping its geometry.</summary>
        internal void ApplyPoseFromRos(GameObject unityObject, PoseMsg pose, Transform frame)
        {
            ApplyWorldPose(unityObject.transform, pose, frame);
            MarkPoseAsPublished(unityObject);
        }

        /// <summary>Build or update the GameObject for an inbound ADD / APPEND / MOVE whose pose is expressed in <paramref name="frame"/>.</summary>
        internal GameObject Apply(CollisionObjectMsg co, Transform frame)
        {
            awaitingDetachPose.Remove(co.id);
            return Upsert(co, frame, applyWorldPose: true);
        }

        internal void RaiseObjectUpdated(string id) => OnObjectUpdated?.Invoke(id);

        static void Silence(GameObject go)
        {
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                pub.suppressRemoveOnDestroy = true;
        }

        /// <summary>
        /// Builds the GameObject for an object first seen while already attached to the robot
        /// (Unity joined mid-carry), through the same path as a world ADD. The message pose is
        /// link-relative, so no world pose is applied — the caller places it under the link.
        /// </summary>
        public GameObject SpawnFromAttachedObject(CollisionObjectMsg co)
        {
            if (co == null || string.IsNullOrEmpty(co.id)) return null;
            if (TryGetObject(co.id, out var existing)) return existing;
            return Upsert(co, null, applyWorldPose: false);
        }

        GameObject Upsert(CollisionObjectMsg co, Transform frame, bool applyWorldPose)
        {
            // ADD / APPEND / MOVE → upsert parent
            if (!objectsById.TryGetValue(co.id, out var parent) || !parent)
            {
                parent = new GameObject(co.id);
                parent.transform.SetParent(worldOriginTransform, true);
                objectsById[co.id] = parent;
                registry?.Adopt(parent, co.id, EnvironmentOwner.Remote, null, (co.meshes?.Length ?? 0) > 0);
            }

            Debug.Log($"[CO Listener] Processing object id={co.id} with {co.primitives?.Length ?? 0} primitives, {co.meshes?.Length ?? 0} meshes, {co.planes?.Length ?? 0} planes");

            // ----- Place parent in world using co.pose -----
            if (applyWorldPose)
            {
                var objPose = co.pose ?? new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
                ApplyWorldPose(parent.transform, objPose, frame);
                // ROS set this pose; the children's publishers must not echo it back as a MOVE.
                MarkPoseAsPublished(parent);

                Debug.Log($"[CO Listener] Applied world pose to '{co.id}': position=({objPose.position.x}, {objPose.position.y}, {objPose.position.z}), orientation=({objPose.orientation.x}, {objPose.orientation.y}, {objPose.orientation.z}, {objPose.orientation.w})");
            }

            // MOVE (and any other geometry-less message) only updates the pose — rebuilding here
            // would destroy all visuals since there is no geometry to rebuild from.
            bool hasGeometry = (co.primitives?.Length ?? 0) > 0
                            || (co.meshes?.Length ?? 0) > 0
                            || (co.planes?.Length ?? 0) > 0;
            if (!hasGeometry)
            {
                Debug.Log($"[CO Listener] No geometry in message for '{co.id}'; pose-only update.");
                return parent;
            }

            // Re-ADD of a known object with the geometry it already shows (e.g. the re-add after
            // the gripper releases it): moving it is enough, a rebuild would only flicker.
            string signature = GeometrySignature(co);
            if (parent.transform.childCount > 0
                && _geometrySignatureById.TryGetValue(co.id, out var builtFrom) && builtFrom == signature)
            {
                Debug.Log($"[CO Listener] Geometry of '{co.id}' unchanged; pose-only update.");
                return parent;
            }
            _geometrySignatureById[co.id] = signature;

            // Rebuild children fresh for correctness. The object itself stays in the planning
            // scene, so the outgoing children's publishers must not send a REMOVE for its id.
            for (int i = parent.transform.childCount - 1; i >= 0; i--)
            {
                GameObject outgoing = parent.transform.GetChild(i).gameObject;
                Silence(outgoing);
                Destroy(outgoing);
            }

            Debug.Log($"[CO Listener] Cleared existing children of '{co.id}' before rebuilding");

            int built = 0;

            // ---- PRIMITIVES (local to parent) ----
            var prims = co.primitives ?? System.Array.Empty<SolidPrimitiveMsg>();
            var primPosesLocal = LocalPoseArrayFor(co.primitive_poses, prims.Length); // local, relative to co.pose
            int nPrimUse = Mathf.Min(prims.Length, primPosesLocal.Length);
            for (int i = 0; i < nPrimUse; i++)
            {
                var name = $"{co.id}";
                var child = BuildPrimitive(name, prims[i]);
                child.transform.SetParent(parent.transform, false);      // keep local space
                ApplyLocalPose(child.transform, primPosesLocal[i]);      // local to parent
                built++;

                // Grab / publish stack. The planning scene already holds this object, so the
                // publisher starts as published.
                GrabbableCollisionObjectFactory.Configure(child, name, worldOrigin, isMesh: false, markAlreadyPublished: true);
            }

            // ---- MESHES (local to parent) ----
            Debug.Log($"[CO Listener] Processing meshes for '{co.id}'");
            var meshes = co.meshes ?? System.Array.Empty<MeshMsg>();
            var meshPosesLocal = LocalPoseArrayFor(co.mesh_poses, meshes.Length);
            int nMeshUse = Mathf.Min(meshes.Length, meshPosesLocal.Length);
            for (int i = 0; i < nMeshUse; i++)
            {
                var name = $"{co.id}";
                Debug.Log($"[CO Listener] Building mesh child '{name}' for '{co.id}'"); 
                var child = BuildMesh(name, meshes[i]);
                Debug.Log($"[CO Listener] Built mesh child '{name}' for '{co.id}': {(child != null ? "success" : "failure")}");
                if (child)
                {
                    child.transform.SetParent(parent.transform, false);
                    ApplyLocalPose(child.transform, meshPosesLocal[i]);
                    built++;

                    GrabbableCollisionObjectFactory.Configure(child, name, worldOrigin, isMesh: true, markAlreadyPublished: true);
                }
            }

            // ---- PLANES (local to parent) ----
            var planes = co.planes ?? System.Array.Empty<PlaneMsg>();
            var planePosesLocal = LocalPoseArrayFor(co.plane_poses, planes.Length);
            int nPlaneUse = Mathf.Min(planes.Length, planePosesLocal.Length);
            for (int i = 0; i < nPlaneUse; i++)
            {
                var child = BuildPlane($"{co.id}", planes[i]);
                child.transform.SetParent(parent.transform, false);
                ApplyLocalPose(child.transform, planePosesLocal[i]);
                built++;
            }

            // Optional: parent.SetActive(built > 0);

            return parent;
        }

        internal static void MarkPoseAsPublished(GameObject go)
        {
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                pub.MarkTransformAsPublished();
        }

        internal static string GeometrySignature(CollisionObjectMsg co)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var prim in co.primitives ?? System.Array.Empty<SolidPrimitiveMsg>())
            {
                sb.Append('P').Append(prim.type);
                foreach (double d in prim.dimensions ?? System.Array.Empty<double>())
                    sb.Append(':').Append(d.ToString("F5", System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var mesh in co.meshes ?? System.Array.Empty<MeshMsg>())
                sb.Append('M').Append(mesh.vertices?.Length ?? 0).Append(':').Append(mesh.triangles?.Length ?? 0);
            sb.Append("PL").Append(co.planes?.Length ?? 0);
            AppendPoses(sb, co.primitive_poses);
            AppendPoses(sb, co.mesh_poses);
            AppendPoses(sb, co.plane_poses);
            return sb.ToString();
        }

        static void AppendPoses(System.Text.StringBuilder sb, PoseMsg[] poses)
        {
            sb.Append('|');
            foreach (var p in poses ?? System.Array.Empty<PoseMsg>())
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F5},{1:F5},{2:F5},{3:F5},{4:F5},{5:F5},{6:F5};",
                    p.position.x, p.position.y, p.position.z,
                    p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w);
        }

        public bool TryGetObject(string id, out GameObject go)
        {
            if (objectsById.TryGetValue(id, out go) && go) return true;
            go = null;
            return false;
        }

        // ---------- Pose helpers ----------

        // If array is null or wrong length, return identity local poses
        PoseMsg[] LocalPoseArrayFor(PoseMsg[] poses, int needed)
        {
            if (poses != null && poses.Length == needed) return poses;
            var arr = new PoseMsg[needed];
            var id = new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
            for (int i = 0; i < needed; i++) arr[i] = id;
            return arr;
        }

        // A pose in a planning frame (the frame of the robot whose planning scene reported it;
        // this mirror's own origin when that robot names none) as a Unity world pose.
        void ApplyWorldPose(Transform t, PoseMsg rosPose, Transform frame)
        {
            Transform origin = frame != null ? frame : worldOriginTransform;
            Vector3 pos = RosUnityConversion.RosToUnityPosition(rosPose.position);
            Quaternion rot = RosUnityConversion.RosToUnityQuaternion(rosPose.orientation);

            t.position = origin.TransformPoint(pos);
            t.rotation = origin.rotation * rot;
        }

        /// <summary>The frame poses are read in for a planning scene that names none of its own.</summary>
        internal Transform DefaultFrame => worldOriginTransform;

        void ApplyLocalPose(Transform t, PoseMsg rosPose)
        {
            // Local pose relative to parent (object frame). Same axis remap.
            t.localPosition = RosUnityConversion.RosToUnityPosition(rosPose.position);
            t.localRotation = RosUnityConversion.RosToUnityQuaternion(rosPose.orientation);
        }

        // ---------- Builders ----------

        GameObject BuildPrimitive(string name, SolidPrimitiveMsg prim)
        {
            GameObject go;
            MeshRenderer meshRenderer;
            switch (prim.type)
            {
                case SolidPrimitiveMsg.BOX:
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = name;
                    go.transform.localScale = new Vector3(
                        (float)prim.dimensions[SolidPrimitiveMsg.BOX_X],
                        (float)prim.dimensions[SolidPrimitiveMsg.BOX_Z],
                        (float)prim.dimensions[SolidPrimitiveMsg.BOX_Y]
                    );

                    // Apply material
                    meshRenderer = go.GetComponent<MeshRenderer>();
                    if (meshRenderer != null && litMaterial != null)
                    {
                        meshRenderer.material = litMaterial;
                    }
                    return go;

                case SolidPrimitiveMsg.SPHERE:
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = name;
                    float r = (float)prim.dimensions[SolidPrimitiveMsg.SPHERE_RADIUS];
                    go.transform.localScale = Vector3.one * (2f * r);

                    meshRenderer = go.GetComponent<MeshRenderer>();
                    if (meshRenderer != null && litMaterial != null)
                    {
                        meshRenderer.material = litMaterial;
                    }
                    return go;

                case SolidPrimitiveMsg.CYLINDER:
                    // ROS cylinder aligned with +Z; Unity cylinder aligned with +Y → rotate +90° about X
                    go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    go.name = name;
                    float h = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_HEIGHT];
                    float rr = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_RADIUS];
                    go.transform.localScale = new Vector3(2f * rr, h * 0.5f, 2f * rr); // Unity height=2 at scale.y=1
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                    meshRenderer = go.GetComponent<MeshRenderer>();
                    if (meshRenderer != null && litMaterial != null)
                    {
                        meshRenderer.material = litMaterial;
                    }
                    return go;

                case SolidPrimitiveMsg.CONE:
                    go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // approximate cone; swap for real cone mesh if needed
                    go.name = name;
                    float ch = (float)prim.dimensions[SolidPrimitiveMsg.CONE_HEIGHT];
                    float cr = (float)prim.dimensions[SolidPrimitiveMsg.CONE_RADIUS];
                    go.transform.localScale = new Vector3(2f * cr, ch * 0.5f, 2f * cr);
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                    meshRenderer = go.GetComponent<MeshRenderer>();
                    if (meshRenderer != null && litMaterial != null)
                    {
                        meshRenderer.material = litMaterial;
                    }
                    return go;

                default:
                    Debug.LogWarning($"[CO Listener] Unsupported primitive {prim.type}; using cube.");
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = name;
                    return go;
            }
        }

        GameObject BuildMesh(string name, MeshMsg meshMsg)
        {
            Debug.Log($"[CO Listener] Building mesh '{name}'");
            if (meshMsg.vertices == null || meshMsg.vertices.Length == 0 ||
                meshMsg.triangles == null || meshMsg.triangles.Length == 0)
            {
                Debug.LogWarning($"[CO Listener] Invalid mesh data for '{name}'");
                return null;
            }

            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = litMaterial;

            // Named so downstream shape sniffing can identify it. An unnamed mesh reports
            // name "" and matches nothing, which left the wrist menu's edit panel empty for
            // every mesh-type collision object.
            var uMesh = new Mesh { name = $"Mesh_{name}" };
            var verts = new Vector3[meshMsg.vertices.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                // ROS (x,y,z) → Unity (x,z,y)
                verts[i] = new Vector3(
                    (float)meshMsg.vertices[i].x,
                    (float)meshMsg.vertices[i].z,
                    (float)meshMsg.vertices[i].y
                );
            }

            var tris = new System.Collections.Generic.List<int>(meshMsg.triangles.Length * 3);
            for (int t = 0; t < meshMsg.triangles.Length; t++)
            {
                var ids = meshMsg.triangles[t].vertex_indices;
                // Reverse winding order to flip normals (faces out)
                tris.Add((int)ids[0]); tris.Add((int)ids[2]); tris.Add((int)ids[1]);
            }

            uMesh.SetVertices(verts);
            uMesh.SetTriangles(tris, 0);
            uMesh.RecalculateNormals();
            uMesh.RecalculateBounds();

            mf.sharedMesh = uMesh;
            Debug.Log($"[CO Listener] Built mesh '{name}' with {verts.Length} vertices and {tris.Count / 3} triangles");
            return go;
        }

        GameObject BuildPlane(string name, PlaneMsg plane)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.localScale = new Vector3(10f, 10f, 1f); // simple visual
            return go;
        }
    }
}
