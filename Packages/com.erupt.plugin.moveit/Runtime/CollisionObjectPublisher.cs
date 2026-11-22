using Erupt.Ros;
using UnityEngine;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using System;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>What a <see cref="CollisionObjectPublisher"/> reports its object's changes to.</summary>
    public interface ICollisionObjectSink
    {
        /// <summary>
        /// The object appeared, moved or was resized. <paramref name="geometryChanged"/> is true
        /// for a first appearance and for a resize. False = not delivered; the publisher keeps
        /// the change and reports it again.
        /// </summary>
        bool ObjectChanged(CollisionObjectPublisher source, bool geometryChanged);
        /// <summary>The object was destroyed in Unity (not on ROS's command).</summary>
        void ObjectDestroyed(CollisionObjectPublisher source);
    }

    /// <summary>
    /// Makes one Unity object part of the planning scene: it notices that the object appeared,
    /// moved, was resized or was destroyed, builds its geometry as MoveIt shapes, and reports
    /// the change. It does not talk to ROS. The MoveIt plugin's <see cref="PlanningSceneFanout"/>
    /// takes the reports and sends each one to every robot's own planning scene, in that robot's
    /// planning frame; until a fan-out exists the publisher is idle and keeps its first
    /// appearance pending.
    /// </summary>
    public class CollisionObjectPublisher : MonoBehaviour
    {
        /// <summary>Relative name of the topic collision objects go out on, in each robot's namespace.</summary>
        public const string Topic = "collision_object";
        // Outgoing queue for the topic. A scene start sends one ADD per object in a burst and the
        // connector drops the oldest message when the queue overflows. The connector keeps the
        // first registration's size, so every registrant passes this value.
        public const int QueueSize = 512;

        public string objectId = "unity_object";
        [Tooltip("Not used for publishing: every robot's planning scene gets the object in that robot's own planning frame.")]
        public string frameId = "world";
        public bool isMesh = false;
        [Tooltip("Decide mesh vs primitive from the MeshFilter instead of the isMesh flag.")]
        public bool autoDetectMeshType = false;
        [Tooltip("Copy a mesh that is not Read/Write enabled so it can still be published.")]
        public bool createReadableMeshCopy = false;
        [Tooltip("Compound object: every Unity primitive (cube, sphere, cylinder) on this object and " +
                 "under these transforms is published as one primitive of this collision object, posed " +
                 "relative to this transform. Lets a single planning-scene object carry a table and " +
                 "everything fixed to it.")]
        public Transform[] compoundRoots;
        public float publishRateHz = 1f;
        public bool pausePublishing = false;
        // Set before Destroy() when the destruction was commanded by ROS itself (inbound REMOVE):
        // reporting it would send a REMOVE back and delete the object from MoveIt's live scene.
        public bool suppressRemoveOnDestroy = false;
        // True while a robot's MoveIt has this object attached (set by that robot's
        // AttachedCollisionObjectListener). Nothing is sent for the id to that robot's planning
        // scene meanwhile: a world object with the same id as an attached body corrupts it. The
        // other robots' planning scenes keep being told where it is.
        [NonSerialized] public bool attachedToRobot = false;
        // Namespace of the robot carrying the object while attachedToRobot is set; null = not
        // known, and then no planning scene is told anything about it.
        [NonSerialized] public string attachedNamespace;
        [Tooltip("Not used for publishing (each robot's planning frame is); kept as the origin handed to objects built from this one.")]
        public GameObject worldOrigin;
        public bool hasBeenPublished = false;

        // Every publisher that has started, so a fan-out created later finds the ones already there.
        private static readonly List<CollisionObjectPublisher> live = new();
        /// <summary>The publishers that have started and are not destroyed.</summary>
        public static IReadOnlyList<CollisionObjectPublisher> Live => live;
        /// <summary>A publisher started (its fields are set by then).</summary>
        public static event Action<CollisionObjectPublisher> Started;

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            live.Clear();
            Started = null;
        }

        private ICollisionObjectSink sink;
        private float lastPublishTime = 0f;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        private Vector3 lastScale;
        private Mesh readableMeshCopy;
        // The ROS mesh of the last ADD, reused while the mesh and the world scale are unchanged:
        // a re-ADD then costs only the serialization.
        private MeshMsg cachedRosMesh;
        private Mesh cachedRosMeshSource;
        private Vector3 cachedRosMeshScale;

        /// <summary>Where changes are reported; null while no fan-out exists.</summary>
        public ICollisionObjectSink Sink => sink;

        /// <summary>Report changes to this sink from now on (null = stay idle).</summary>
        public void Bind(ICollisionObjectSink target) => sink = target;

        void Start()
        {
            UpdateLastTransform();
            live.Add(this);
            Started?.Invoke(this);
        }

        void Update()
        {
            if (pausePublishing || sink == null) return;
            if (Time.time - lastPublishTime < 1.0f / publishRateHz) return;
            if (hasBeenPublished && !HasTransformChanged()) return;

            if (!sink.ObjectChanged(this, !hasBeenPublished || HasScaleChanged())) return;
            UpdateLastTransform();
            hasBeenPublished = true;
            lastPublishTime = Time.time;
        }

        /// <summary>
        /// Treat the current pose as already known to the planning scenes. Called after an
        /// inbound pose has been applied so the change is not echoed straight back as a MOVE.
        /// </summary>
        public void MarkTransformAsPublished()
        {
            lastPosition = transform.position;
            lastRotation = transform.rotation;
        }

        /// <summary>Send a full ADD (with geometry) on the next update.</summary>
        public void ForceRepublish()
        {
            hasBeenPublished = false;
            lastPublishTime = 0f;
        }

        /// <summary>
        /// Fill <paramref name="msg"/> with this object's shapes (a mesh when asked for, else a
        /// compound of primitives, else one primitive). False when the object has no geometry
        /// that can be sent; the reason is logged.
        /// </summary>
        public bool TryBuildGeometry(CollisionObjectMsg msg, out bool sendsMesh) => TryFillGeometry(msg, out sendsMesh);

        // Geometry for an ADD: a mesh when asked for, else a compound of primitives, else one
        // primitive, falling back to the mesh for shapes MoveIt has no primitive for.
        bool TryFillGeometry(CollisionObjectMsg msg, out bool sendsMesh)
        {
            sendsMesh = false;
            if (!ShouldTreatAsMesh())
            {
                if (compoundRoots != null && compoundRoots.Length > 0)
                {
                    if (TryBuildCompoundPrimitives(out SolidPrimitiveMsg[] primitives, out PoseMsg[] poses))
                    {
                        msg.primitives = primitives;
                        msg.primitive_poses = poses;
                        return true;
                    }
                    Debug.LogError($"No Unity primitives on '{name}' or under its compoundRoots; cannot publish collision object '{objectId}'.");
                    return false;
                }

                SolidPrimitiveMsg primitive = CreatePrimitiveFromUnityShape();
                if (primitive != null)
                {
                    msg.primitives = new[] { primitive };
                    return true;
                }
            }

            Mesh mesh = GetReadableMesh();
            if (mesh == null)
            {
                Debug.LogError($"No readable mesh on '{name}'; cannot publish collision object '{objectId}'. " +
                               "Enable Read/Write on the mesh import settings or set createReadableMeshCopy.");
                return false;
            }
            msg.meshes = new[] { RosMeshFor(mesh) };
            sendsMesh = true;
            return true;
        }

        SolidPrimitiveMsg CreatePrimitiveFromUnityShape()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return null;
            // lossyScale, not localScale: objects nested under scaled parents need the full
            // hierarchy scale.
            return PrimitiveFromUnityMesh(meshFilter.sharedMesh.name, transform.lossyScale);
        }

        // Every active Unity primitive on this object and under compoundRoots, each posed relative
        // to this transform. The relative pose is computed from rotation and position only: this
        // object may itself be a scaled cube, and InverseTransformPoint would divide the offsets by
        // that scale.
        bool TryBuildCompoundPrimitives(out SolidPrimitiveMsg[] primitives, out PoseMsg[] poses)
        {
            var shapes = new List<SolidPrimitiveMsg>();
            var shapePoses = new List<PoseMsg>();
            Quaternion inverse = Quaternion.Inverse(transform.rotation);

            void Add(MeshFilter mf)
            {
                if (mf == null || mf.sharedMesh == null) return;
                SolidPrimitiveMsg shape = PrimitiveFromUnityMesh(mf.sharedMesh.name, mf.transform.lossyScale);
                if (shape == null)
                {
                    Debug.LogWarning($"[{name}] '{mf.name}' is not a Unity primitive; left out of compound collision object '{objectId}'.");
                    return;
                }
                Vector3 localPosition = inverse * (mf.transform.position - transform.position);
                Quaternion localRotation = inverse * mf.transform.rotation;
                shapes.Add(shape);
                shapePoses.Add(new PoseMsg(
                    RosUnityConversion.UnityToRosPosition(localPosition),
                    RosUnityConversion.UnityToRosQuaternion(localRotation)));
            }

            Add(GetComponent<MeshFilter>());
            foreach (Transform root in compoundRoots)
            {
                if (root == null) continue;
                foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(false))
                    Add(mf);
            }

            primitives = shapes.ToArray();
            poses = shapePoses.ToArray();
            return primitives.Length > 0;
        }

        // Maps a Unity primitive mesh (by name) at the given world scale to a MoveIt solid
        // primitive; null for anything that is not a Unity primitive.
        static SolidPrimitiveMsg PrimitiveFromUnityMesh(string meshName, Vector3 scale)
        {
            if (string.IsNullOrEmpty(meshName)) return null;

            // Match Unity primitive types based on mesh name
            if (meshName.Contains("Cube"))
            {
                return new SolidPrimitiveMsg
                {
                    type = 1, // BOX
                    dimensions = new double[] { scale.x, scale.z, scale.y } // ROS: x, y, z → Unity: x, z, y
                };
            }
            else if (meshName.Contains("Sphere"))
            {
                // For sphere, ROS expects a single radius dimension
                float radius = Mathf.Max(scale.x, scale.y, scale.z) * 0.5f; // Unity sphere has diameter of 1, so radius is 0.5 * scale
                return new SolidPrimitiveMsg
                {
                    type = 2, // SPHERE
                    dimensions = new double[] { radius }
                };
            }
            else if (meshName.Contains("Cylinder"))
            {
                // For cylinder, ROS expects height and radius. Unity's cylinder mesh spans -1..1
                // along Y, so a Y scale of 1 is 2 m tall (CollisionObjectsListenerSimple halves
                // the height on the way in; this is the inverse).
                float height = scale.y * 2f;
                float radius = Mathf.Max(scale.x, scale.z) * 0.5f; // Unity cylinder has diameter of 1, so radius is 0.5 * scale
                return new SolidPrimitiveMsg
                {
                    type = 3, // CYLINDER
                    dimensions = new double[] { height, radius }
                };
            }
            else if (meshName.Contains("Capsule"))
            {
                // For capsule, ROS expects height and radius. Unity's capsule mesh is also 2 m
                // tall at a Y scale of 1.
                float height = scale.y * 2f;
                float radius = Mathf.Max(scale.x, scale.z) * 0.5f; // Unity capsule has diameter of 1, so radius is 0.5 * scale
                return new SolidPrimitiveMsg
                {
                    type = 3, // Use CYLINDER as closest match for capsule, or 4 if CONE is available for capsules
                    dimensions = new double[] { height, radius }
                };
            }
            else if (meshName.Contains("Plane") || meshName.Contains("Quad"))
            {
                // Planes don't have a direct ROS primitive equivalent, so we'll use a very thin box
                return new SolidPrimitiveMsg
                {
                    type = 1, // BOX
                    dimensions = new double[] { scale.x, scale.z, 0.001 } // Very thin box to represent plane
                };
            }

            // Unknown primitive type, return null to fall back to mesh
            return null;
        }

        static bool IsUnityPrimitiveName(string meshName)
        {
            string n = meshName.ToLowerInvariant();
            return n.Contains("cube") || n.Contains("sphere") || n.Contains("cylinder") ||
                   n.Contains("capsule") || n.Contains("plane") || n.Contains("quad");
        }

        bool ShouldTreatAsMesh()
        {
            if (!autoDetectMeshType) return isMesh;

            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return false;
            Mesh mesh = meshFilter.sharedMesh;

            if (!IsUnityPrimitiveName(mesh.name)) return true;
            // A custom mesh that merely has a primitive-like name: Unity's own primitives have
            // few vertices (the sphere, the largest, has 515).
            return mesh.vertexCount > 600;
        }

        bool HasTransformChanged()
        {
            const float epsilon = 0.001f;
            return Vector3.Distance(transform.position, lastPosition) > epsilon ||
                   Quaternion.Angle(transform.rotation, lastRotation) > epsilon ||
                   HasScaleChanged();
        }

        bool HasScaleChanged() => Vector3.Distance(transform.lossyScale, lastScale) > 0.001f;

        void UpdateLastTransform()
        {
            lastPosition = transform.position;
            lastRotation = transform.rotation;
            lastScale = transform.lossyScale;
        }

        void OnDestroy()
        {
            live.Remove(this);
            if (sink != null && !suppressRemoveOnDestroy) sink.ObjectDestroyed(this);
            if (readableMeshCopy != null)
            {
                if (Application.isPlaying) Destroy(readableMeshCopy);
                else DestroyImmediate(readableMeshCopy);
                readableMeshCopy = null;
            }
        }

        MeshMsg RosMeshFor(Mesh mesh)
        {
            Vector3 scale = transform.lossyScale;
            if (cachedRosMesh != null && cachedRosMeshSource == mesh && cachedRosMeshScale == scale)
                return cachedRosMesh;
            cachedRosMesh = UnityMeshToRosMesh(mesh, scale);
            cachedRosMeshSource = mesh;
            cachedRosMeshScale = scale;
            return cachedRosMesh;
        }

        static MeshMsg UnityMeshToRosMesh(Mesh unityMesh, Vector3 scale)
        {
            if (unityMesh == null || unityMesh.vertexCount == 0)
            {
                Debug.LogWarning("Attempting to convert a null or empty mesh to a ROS mesh.");
                return new MeshMsg();
            }
            if (!unityMesh.isReadable)
            {
                Debug.LogError($"Mesh '{unityMesh.name}' is not readable. Enable 'Read/Write' in the mesh import settings.");
                return new MeshMsg();
            }

            // Mesh.vertices allocates a copy of the whole array on every access, so it is read
            // once: indexing it inside the loop makes the conversion quadratic (seconds of freeze
            // for large meshes).
            Vector3[] vertices = unityMesh.vertices;
            var rosVertices = new PointMsg[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = Vector3.Scale(vertices[i], scale);
                rosVertices[i] = new PointMsg(v.x, v.z, v.y); // Unity Y-up -> ROS Z-up
            }

            int[] triangles = unityMesh.triangles;
            if (triangles.Length == 0 || triangles.Length % 3 != 0)
            {
                Debug.LogWarning($"Mesh '{unityMesh.name}' has invalid triangle data.");
                return new MeshMsg { vertices = rosVertices, triangles = new MeshTriangleMsg[0] };
            }

            var rosTriangles = new MeshTriangleMsg[triangles.Length / 3];
            for (int i = 0; i < rosTriangles.Length; i++)
            {
                int t = i * 3;
                rosTriangles[i] = new MeshTriangleMsg
                {
                    vertex_indices = new[] { (uint)triangles[t], (uint)triangles[t + 1], (uint)triangles[t + 2] }
                };
            }
            return new MeshMsg { vertices = rosVertices, triangles = rosTriangles };
        }

        // The mesh to publish: the shared mesh when readable, else a readable copy when allowed.
        Mesh GetReadableMesh()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return null;

            Mesh original = meshFilter.sharedMesh;
            if (original.isReadable) return original;
            if (!createReadableMeshCopy) return null;
            if (readableMeshCopy == null) readableMeshCopy = CreateReadableMeshCopy(original);
            return readableMeshCopy;
        }

        static Mesh CreateReadableMeshCopy(Mesh originalMesh)
        {
            if (originalMesh == null)
            {
                Debug.LogError("Cannot create readable copy of null mesh");
                return null;
            }

            if (originalMesh.isReadable)
            {
                Debug.Log("Mesh is already readable, returning original");
                return originalMesh;
            }

            Debug.Log($"Creating readable copy of mesh '{originalMesh.name}'");

            try
            {
                // Create a new mesh
                Mesh readableMesh = new Mesh();
                readableMesh.name = originalMesh.name + "_ReadableCopy";

                // Copy basic properties that are always accessible
                readableMesh.vertices = originalMesh.vertices;
                readableMesh.normals = originalMesh.normals;
                readableMesh.uv = originalMesh.uv;
                readableMesh.uv2 = originalMesh.uv2;
                readableMesh.uv3 = originalMesh.uv3;
                readableMesh.uv4 = originalMesh.uv4;
                readableMesh.tangents = originalMesh.tangents;
                readableMesh.colors = originalMesh.colors;
                readableMesh.bounds = originalMesh.bounds;

                // Copy triangles (this is the main issue with non-readable meshes)
                readableMesh.triangles = originalMesh.triangles;

                // Set submesh count and copy submeshes if available
                readableMesh.subMeshCount = originalMesh.subMeshCount;
                for (int i = 0; i < originalMesh.subMeshCount; i++)
                {
                    try
                    {
                        readableMesh.SetTriangles(originalMesh.GetTriangles(i), i);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"Could not copy submesh {i} from '{originalMesh.name}': {e.Message}");
                    }
                }

                Debug.Log($"Successfully created readable copy of mesh '{originalMesh.name}' with {readableMesh.vertexCount} vertices and {readableMesh.triangles.Length / 3} triangles");
                return readableMesh;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to create readable copy of mesh '{originalMesh.name}': {e.Message}");
                return null;
            }
        }

        internal static TimeMsg GetRosTimestamp()
        {
            long ticks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            return new TimeMsg
            {
                sec = (int)(ticks / TimeSpan.TicksPerSecond),
                nanosec = (uint)((ticks % TimeSpan.TicksPerSecond) * 100L) // 1 tick = 100 ns
            };
        }
    }
}
