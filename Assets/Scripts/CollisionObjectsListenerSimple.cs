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

public class CollisionObjectsListenerSimple : MonoBehaviour
{
    [Header("ROS")]
    public string topic = "/collision_objects_ros";

    [Header("Frame Root (ROS world frame)")]
    public Transform worldOrigin; // If null, uses this.transform

    private ROSConnection ros;
    private readonly Dictionary<string, GameObject> objectsById = new();

    // MoveIt op codes (per message spec)
    const byte OP_ADD = 0;
    const byte OP_REMOVE = 1;
    const byte OP_APPEND = 2;
    const byte OP_MOVE = 3;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<CollisionObjectMsg>(topic, OnCollisionObject);
        if (worldOrigin == null) worldOrigin = transform;
    }

    void OnCollisionObject(CollisionObjectMsg co)
    {
        if (string.IsNullOrEmpty(co.id))
        {
            Debug.LogWarning("[CO Listener] Empty id; ignoring.");
            return;
        }

        if (co.operation == OP_REMOVE)
        {
            if (objectsById.TryGetValue(co.id, out var old) && old) Destroy(old);
            objectsById.Remove(co.id);
            return;
        }

        // ADD / APPEND / MOVE → upsert parent
        if (!objectsById.TryGetValue(co.id, out var parent) || !parent)
        {
            parent = new GameObject(co.id);
            parent.transform.SetParent(worldOrigin, true);
            objectsById[co.id] = parent;
        }

        // ----- Place parent in world using co.pose -----
        var objPose = co.pose ?? new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
        ApplyWorldPose(parent.transform, objPose);

        // Rebuild children fresh for correctness
        for (int i = parent.transform.childCount - 1; i >= 0; i--)
            Destroy(parent.transform.GetChild(i).gameObject);

        int built = 0;

        // ---- PRIMITIVES (local to parent) ----
        var prims = co.primitives ?? System.Array.Empty<SolidPrimitiveMsg>();
        var primPosesLocal = LocalPoseArrayFor(co.primitive_poses, prims.Length); // local, relative to co.pose
        int nPrimUse = Mathf.Min(prims.Length, primPosesLocal.Length);
        for (int i = 0; i < nPrimUse; i++)
        {
            var child = BuildPrimitive($"{co.id}_prim_{i}", prims[i]);
            child.transform.SetParent(parent.transform, false);      // keep local space
            ApplyLocalPose(child.transform, primPosesLocal[i]);      // local to parent
            built++;
        }

        // ---- MESHES (local to parent) ----
        var meshes = co.meshes ?? System.Array.Empty<MeshMsg>();
        var meshPosesLocal = LocalPoseArrayFor(co.mesh_poses, meshes.Length);
        int nMeshUse = Mathf.Min(meshes.Length, meshPosesLocal.Length);
        for (int i = 0; i < nMeshUse; i++)
        {
            var child = BuildMesh($"{co.id}_mesh_{i}", meshes[i]);
            if (child)
            {
                child.transform.SetParent(parent.transform, false);
                ApplyLocalPose(child.transform, meshPosesLocal[i]);
                built++;
            }
        }

        // ---- PLANES (local to parent) ----
        var planes = co.planes ?? System.Array.Empty<PlaneMsg>();
        var planePosesLocal = LocalPoseArrayFor(co.plane_poses, planes.Length);
        int nPlaneUse = Mathf.Min(planes.Length, planePosesLocal.Length);
        for (int i = 0; i < nPlaneUse; i++)
        {
            var child = BuildPlane($"{co.id}_plane_{i}", planes[i]);
            child.transform.SetParent(parent.transform, false);
            ApplyLocalPose(child.transform, planePosesLocal[i]);
            built++;
        }

        // Optional: parent.SetActive(built > 0);
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

    void ApplyWorldPose(Transform t, PoseMsg rosPose)
    {
        // ROS (x,y,z) → Unity (x,z,y)
        Vector3 pos = rosPose.position.From<FLU>();
        // Vector3 pos = new Vector3(
        //     (float)rosPose.position.x,
        //     (float)rosPose.position.z,
        //     (float)rosPose.position.y
        // );
        Quaternion rot = rosPose.orientation.From<FLU>();
        // Quaternion rot = new Quaternion(
        //     (float)rosPose.orientation.x,
        //     (float)rosPose.orientation.z,
        //     (float)rosPose.orientation.y,
        //     (float)rosPose.orientation.w
        // );
        t.position = worldOrigin.TransformPoint(pos);
        t.rotation = worldOrigin.rotation * rot;
    }

    void ApplyLocalPose(Transform t, PoseMsg rosPose)
    {
        // Local pose relative to parent (object frame). Same axis remap.
        t.localPosition = rosPose.position.From<FLU>();
        t.localRotation = rosPose.orientation.From<FLU>();
        // t.localPosition = new Vector3(
        //     (float)rosPose.position.x,
        //     (float)rosPose.position.z,
        //     (float)rosPose.position.y
        // );
        // t.localRotation = new Quaternion(
        //     (float)rosPose.orientation.x,
        //     (float)rosPose.orientation.z,
        //     (float)rosPose.orientation.y,
        //     (float)rosPose.orientation.w
        // );
    }

    // ---------- Builders ----------

    GameObject BuildPrimitive(string name, SolidPrimitiveMsg prim)
    {
        GameObject go;
        switch (prim.type)
        {
            case SolidPrimitiveMsg.BOX:
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.localScale = new Vector3(
                    (float)prim.dimensions[SolidPrimitiveMsg.BOX_X],
                    (float)prim.dimensions[SolidPrimitiveMsg.BOX_Y],
                    (float)prim.dimensions[SolidPrimitiveMsg.BOX_Z]
                );
                return go;

            case SolidPrimitiveMsg.SPHERE:
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                float r = (float)prim.dimensions[SolidPrimitiveMsg.SPHERE_RADIUS];
                go.transform.localScale = Vector3.one * (2f * r);
                return go;

            case SolidPrimitiveMsg.CYLINDER:
                // ROS cylinder aligned with +Z; Unity cylinder aligned with +Y → rotate +90° about X
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = name;
                float h = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_HEIGHT];
                float rr = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_RADIUS];
                go.transform.localScale = new Vector3(2f * rr, h * 0.5f, 2f * rr); // Unity height=2 at scale.y=1
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                return go;

            case SolidPrimitiveMsg.CONE:
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // approximate cone; swap for real cone mesh if needed
                go.name = name + "_approxCone";
                float ch = (float)prim.dimensions[SolidPrimitiveMsg.CONE_HEIGHT];
                float cr = (float)prim.dimensions[SolidPrimitiveMsg.CONE_RADIUS];
                go.transform.localScale = new Vector3(2f * cr, ch * 0.5f, 2f * cr);
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                return go;

            default:
                Debug.LogWarning($"[CO Listener] Unsupported primitive {prim.type}; using cube.");
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name + "_unknown";
                return go;
        }
    }

    GameObject BuildMesh(string name, MeshMsg meshMsg)
    {
        if (meshMsg.vertices == null || meshMsg.vertices.Length == 0 ||
            meshMsg.triangles == null || meshMsg.triangles.Length == 0)
        {
            return null;
        }

        var go = new GameObject(name);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = new Material(Shader.Find("Standard"));

        var uMesh = new Mesh();
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
            tris.Add((int)ids[0]); tris.Add((int)ids[1]); tris.Add((int)ids[2]);
        }

        uMesh.SetVertices(verts);
        uMesh.SetTriangles(tris, 0);
        uMesh.RecalculateNormals();
        uMesh.RecalculateBounds();

        mf.sharedMesh = uMesh;
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



// using System.Collections.Generic;
// using UnityEngine;
// using Unity.Robotics.ROSTCPConnector;

// using RosMessageTypes.Moveit;
// using RosMessageTypes.Shape;
// using PoseMsg = RosMessageTypes.Geometry.PoseMsg;
// using MeshMsg = RosMessageTypes.Shape.MeshMsg;
// using PlaneMsg = RosMessageTypes.Shape.PlaneMsg;
// using SolidPrimitiveMsg = RosMessageTypes.Shape.SolidPrimitiveMsg;

// public class CollisionObjectsListenerSimple : MonoBehaviour
// {
//     [Header("ROS")]
//     public string topic = "/collision_objects_ros";

//     [Header("Frame Root (ROS world frame)")]
//     public Transform worldOrigin; // If null, uses this.transform

//     private ROSConnection ros;
//     private readonly Dictionary<string, GameObject> objectsById = new();

//     // MoveIt op codes
//     const byte OP_ADD = 0;
//     const byte OP_APPEND = 1;
//     const byte OP_REMOVE = 2;
//     const byte OP_MOVE = 3;

//     void Start()
//     {
//         ros = ROSConnection.GetOrCreateInstance();
//         ros.Subscribe<CollisionObjectMsg>(topic, OnCollisionObject);
//         if (worldOrigin == null) worldOrigin = transform;
//     }

//     void OnCollisionObject(CollisionObjectMsg co)
//     {
//         if (string.IsNullOrEmpty(co.id))
//         {
//             Debug.LogWarning("[CO Listener] Empty id; ignoring.");
//             return;
//         }

//         if (co.operation == OP_REMOVE)
//         {
//             if (objectsById.TryGetValue(co.id, out var old) && old) Destroy(old);
//             objectsById.Remove(co.id);
//             return;
//         }

//         // ADD / APPEND / MOVE → upsert parent
//         if (!objectsById.TryGetValue(co.id, out var parent) || !parent)
//         {
//             parent = new GameObject(co.id);
//             parent.transform.SetParent(worldOrigin, true);
//             objectsById[co.id] = parent;
//         }

//         // Rebuild children fresh for correctness
//         for (int i = parent.transform.childCount - 1; i >= 0; i--)
//             Destroy(parent.transform.GetChild(i).gameObject);

//         int built = 0;

//         // ---- PRIMITIVES ----
//         var prims = co.primitives ?? System.Array.Empty<SolidPrimitiveMsg>();
//         var primPoses = RobustPoseArrayFor(co, GeometryKind.Primitive, prims.Length);
//         int nPrimUse = Mathf.Min(prims.Length, primPoses.Length);
//         for (int i = 0; i < nPrimUse; i++)
//         {
//             var go = BuildPrimitive($"{co.id}_prim_{i}", prims[i]);
//             PlaceWorld(go.transform, primPoses[i]);
//             go.transform.SetParent(parent.transform, true);
//             built++;
//         }

//         // ---- MESHES ----
//         var meshes = co.meshes ?? System.Array.Empty<MeshMsg>();
//         var meshPoses = RobustPoseArrayFor(co, GeometryKind.Mesh, meshes.Length);
//         int nMeshUse = Mathf.Min(meshes.Length, meshPoses.Length);
//         for (int i = 0; i < nMeshUse; i++)
//         {
//             var go = BuildMesh($"{co.id}_mesh_{i}", meshes[i]);
//             if (go)
//             {
//                 PlaceWorld(go.transform, meshPoses[i]);
//                 go.transform.SetParent(parent.transform, true);
//                 built++;
//             }
//         }

//         // ---- PLANES (optional) ----
//         var planes = co.planes ?? System.Array.Empty<PlaneMsg>();
//         var planePoses = RobustPoseArrayFor(co, GeometryKind.Plane, planes.Length);
//         int nPlaneUse = Mathf.Min(planes.Length, planePoses.Length);
//         for (int i = 0; i < nPlaneUse; i++)
//         {
//             var go = BuildPlane($"{co.id}_plane_{i}", planes[i]);
//             PlaceWorld(go.transform, planePoses[i]);
//             go.transform.SetParent(parent.transform, true);
//             built++;
//         }

//         // Optional visibility toggle:
//         // parent.SetActive(built > 0);
//     }

//     enum GeometryKind { Primitive, Mesh, Plane }

//     /// <summary>
//     /// Returns the per-geometry pose array if present; otherwise falls back to the object-level co.pose.
//     /// If co.pose is missing (shouldn't be in your dump), falls back to identity.
//     /// </summary>
//     PoseMsg[] RobustPoseArrayFor(CollisionObjectMsg co, GeometryKind kind, int countNeeded)
//     {
//         PoseMsg[] src = null;
//         switch (kind)
//         {
//             case GeometryKind.Primitive: src = co.primitive_poses; break;
//             case GeometryKind.Mesh:      src = co.mesh_poses;      break;
//             case GeometryKind.Plane:     src = co.plane_poses;     break;
//         }

//         // If we have a complete pose array, use it.
//         if (src != null && src.Length == countNeeded && countNeeded > 0)
//             return src;

//         // Fallback: use object-level co.pose for all items.
//         var fallbackPose = co.pose ?? new PoseMsg(
//             new RosMessageTypes.Geometry.PointMsg(0, 0, 0),
//             new RosMessageTypes.Geometry.QuaternionMsg(0, 0, 0, 1)
//         );

//         var arr = new PoseMsg[countNeeded];
//         for (int i = 0; i < countNeeded; i++) arr[i] = fallbackPose;
//         return arr;
//     }

//     // ---------- Builders ----------

//     GameObject BuildPrimitive(string name, SolidPrimitiveMsg prim)
//     {
//         GameObject go;
//         switch (prim.type)
//         {
//             case SolidPrimitiveMsg.BOX:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cube);
//                 go.name = name;
//                 go.transform.localScale = new Vector3(
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_X],
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_Y],
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_Z]
//                 );
//                 return go;

//             case SolidPrimitiveMsg.SPHERE:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//                 go.name = name;
//                 float r = (float)prim.dimensions[SolidPrimitiveMsg.SPHERE_RADIUS];
//                 go.transform.localScale = Vector3.one * (2f * r);
//                 return go;

//             case SolidPrimitiveMsg.CYLINDER:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
//                 go.name = name;
//                 float h = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_HEIGHT];
//                 float rr = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_RADIUS];
//                 go.transform.localScale = new Vector3(2f * rr, h * 0.5f, 2f * rr); // Unity height=2 at scale.y=1
//                 go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);        // ROS Z-axis → Unity Y-axis
//                 return go;

//             case SolidPrimitiveMsg.CONE:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);           // approximate; swap for cone mesh if desired
//                 go.name = name + "_approxCone";
//                 float ch = (float)prim.dimensions[SolidPrimitiveMsg.CONE_HEIGHT];
//                 float cr = (float)prim.dimensions[SolidPrimitiveMsg.CONE_RADIUS];
//                 go.transform.localScale = new Vector3(2f * cr, ch * 0.5f, 2f * cr);
//                 go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
//                 return go;

//             default:
//                 Debug.LogWarning($"[CO Listener] Unsupported primitive {prim.type}; using cube.");
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cube);
//                 go.name = name + "_unknown";
//                 return go;
//         }
//     }

//     GameObject BuildMesh(string name, MeshMsg meshMsg)
//     {
//         if (meshMsg.vertices == null || meshMsg.vertices.Length == 0 ||
//             meshMsg.triangles == null || meshMsg.triangles.Length == 0)
//         {
//             return null;
//         }

//         var go = new GameObject(name);
//         var mf = go.AddComponent<MeshFilter>();
//         var mr = go.AddComponent<MeshRenderer>();
//         mr.sharedMaterial = new Material(Shader.Find("Standard"));

//         var uMesh = new Mesh();
//         var verts = new Vector3[meshMsg.vertices.Length];
//         for (int i = 0; i < verts.Length; i++)
//         {
//             // ROS (x,y,z) → Unity (x,z,y)
//             verts[i] = new Vector3(
//                 (float)meshMsg.vertices[i].x,
//                 (float)meshMsg.vertices[i].z,
//                 (float)meshMsg.vertices[i].y
//             );
//         }

//         var tris = new System.Collections.Generic.List<int>(meshMsg.triangles.Length * 3);
//         for (int t = 0; t < meshMsg.triangles.Length; t++)
//         {
//             var ids = meshMsg.triangles[t].vertex_indices;
//             tris.Add((int)ids[0]); tris.Add((int)ids[1]); tris.Add((int)ids[2]);
//         }

//         uMesh.SetVertices(verts);
//         uMesh.SetTriangles(tris, 0);
//         uMesh.RecalculateNormals();
//         uMesh.RecalculateBounds();

//         mf.sharedMesh = uMesh;
//         return go;
//     }

//     GameObject BuildPlane(string name, PlaneMsg plane)
//     {
//         var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
//         go.name = name;
//         go.transform.localScale = new Vector3(10f, 10f, 1f);
//         return go;
//     }

//     // ---------- Placement: ROS → Unity world ----------

//     void PlaceWorld(Transform t, PoseMsg rosPose)
//     {
//         Vector3 pos = new Vector3(
//             (float)rosPose.position.x,
//             (float)rosPose.position.z,
//             (float)rosPose.position.y
//         );
//         Quaternion rot = new Quaternion(
//             (float)rosPose.orientation.x,
//             (float)rosPose.orientation.z,
//             (float)rosPose.orientation.y,
//             (float)rosPose.orientation.w
//         );
//         // t.position = worldOrigin.TransformPoint(pos);
//         // t.rotation = worldOrigin.rotation * rot;
//         t.position += pos;
//         t.rotation = t.rotation * rot;
//     }
// }




// using System.Collections.Generic;
// using UnityEngine;
// using Unity.Robotics.ROSTCPConnector;

// using RosMessageTypes.Moveit;
// using RosMessageTypes.Shape;
// using PoseMsg = RosMessageTypes.Geometry.PoseMsg;
// using MeshMsg = RosMessageTypes.Shape.MeshMsg;
// using PlaneMsg = RosMessageTypes.Shape.PlaneMsg;
// using SolidPrimitiveMsg = RosMessageTypes.Shape.SolidPrimitiveMsg;

// public class CollisionObjectsListenerSimple : MonoBehaviour
// {
//     [Header("ROS")]
//     public string topic = "/collision_objects_ros";

//     [Header("Frame Root (ROS world frame)")]
//     public Transform worldOrigin; // Optional; if null, uses this.transform

//     private ROSConnection ros;
//     private readonly Dictionary<string, GameObject> objectsById = new();

//     // Fallback op codes (match moveit_msgs/CollisionObject constants)
//     const byte OP_ADD = 0;
//     const byte OP_APPEND = 1;
//     const byte OP_REMOVE = 2;
//     const byte OP_MOVE = 3;

//     void Start()
//     {
//         ros = ROSConnection.GetOrCreateInstance();
//         ros.Subscribe<CollisionObjectMsg>(topic, OnCollisionObject);
//         if (worldOrigin == null) worldOrigin = transform;
//     }

//     void OnCollisionObject(CollisionObjectMsg co)
//     {

//         Debug.Log($"co: {co.ToString()}");
//         Debug.Log($"[CO Listener] Received collision object message - ID: '{co.id}', Operation: {co.operation}");
        
//         if (string.IsNullOrEmpty(co.id))
//         {
//             Debug.LogWarning("[CO Listener] Empty id; ignoring.");
//             return;
//         }

//         if (co.operation == OP_REMOVE)
//         {
//             if (objectsById.TryGetValue(co.id, out var go) && go) Destroy(go);
//             objectsById.Remove(co.id);
//             return;
//         }

//         // ADD / APPEND / MOVE → upsert
//         if (!objectsById.TryGetValue(co.id, out var parent) || !parent)
//         {
//             parent = new GameObject(co.id);
//             parent.transform.SetParent(worldOrigin, true);
//             objectsById[co.id] = parent;
//         }

//         // Rebuild geometry for simplicity/accuracy
//         for (int i = parent.transform.childCount - 1; i >= 0; i--)
//             Destroy(parent.transform.GetChild(i).gameObject);

//         int built = 0;

//         // Primitives
//         Debug.Log($"[CO Listener] Processing primitives - Count: {co.primitives?.Length ?? 0}, Poses: {co.primitive_poses?.Length ?? 0}");
//         int nPrim = co.primitives?.Length ?? 0;
//         int nPrimPos = co.primitive_poses?.Length ?? 0;
//         int nPrimUse = Mathf.Min(nPrim, nPrimPos);
//         if (nPrim != nPrimPos)
//             Debug.LogWarning($"[CO Listener] primitives/poses mismatch for {co.id}: {nPrim} vs {nPrimPos}");
//         for (int i = 0; i < nPrimUse; i++)
//         {

//             var go = BuildPrimitive($"{co.id}_prim_{i}", co.primitives[i]);
//             Place(go.transform, co.primitive_poses[i]);
//             go.transform.SetParent(parent.transform, true);
//             built++;
//         }

//         // Meshes
//         // Debug.Log($"[CO Listener] Processing meshes - Count: {co.meshes?.Length ?? 0}, Poses: {co.mesh_poses?.Length ?? 0}");
//         int nMesh = co.meshes?.Length ?? 0;
//         int nMeshPos = co.mesh_poses?.Length ?? 0;
//         int nMeshUse = Mathf.Min(nMesh, nMeshPos);
//         if (nMesh != nMeshPos)
//             Debug.LogWarning($"[CO Listener] meshes/poses mismatch for {co.id}: {nMesh} vs {nMeshPos}");
//         for (int i = 0; i < nMeshUse; i++)
//         {
//             var go = BuildMesh($"{co.id}_mesh_{i}", co.meshes[i]);
//             if (go)
//             {
//                 Place(go.transform, co.mesh_poses[i]);
//                 go.transform.SetParent(parent.transform, true);
//                 built++;
//             }
//         }

//         // Planes (optional visualization)
//         Debug.Log($"[CO Listener] Processing planes - Count: {co.planes?.Length ?? 0}, Poses: {co.plane_poses?.Length ?? 0}");
//         int nPlane = co.planes?.Length ?? 0;
//         int nPlanePos = co.plane_poses?.Length ?? 0;
//         int nPlaneUse = Mathf.Min(nPlane, nPlanePos);
//         for (int i = 0; i < nPlaneUse; i++)
//         {
//             var go = BuildPlane($"{co.id}_plane_{i}", co.planes[i]);
//             Place(go.transform, co.plane_poses[i]);
//             go.transform.SetParent(parent.transform, true);
//             built++;
//         }

//         // If you want to hide empty parents:
//         // parent.SetActive(built > 0);
//     }

//     // ---------- Builders ----------

//     GameObject BuildPrimitive(string name, SolidPrimitiveMsg prim)
//     {
//         GameObject go;
//         Debug.Log($"[CO Listener] Building primitive {name} of type {prim.type}");
//         switch (prim.type)
//         {
//             case SolidPrimitiveMsg.BOX:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cube);
//                 Debug.Log($"go: {go}");
//                 go.name = name;
//                 go.transform.localScale = new Vector3(
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_X],
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_Y],
//                     (float)prim.dimensions[SolidPrimitiveMsg.BOX_Z]
//                 );
//                 return go;

//             case SolidPrimitiveMsg.SPHERE:
//                 go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//                 go.name = name;
//                 float r = (float)prim.dimensions[SolidPrimitiveMsg.SPHERE_RADIUS];
//                 go.transform.localScale = Vector3.one * (2f * r);
//                 return go;

//             case SolidPrimitiveMsg.CYLINDER:
//                 // ROS cylinder axis is Z; Unity's cylinder axis is Y → rotate +90° about X.
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
//                 go.name = name;
//                 float h = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_HEIGHT];
//                 float rr = (float)prim.dimensions[SolidPrimitiveMsg.CYLINDER_RADIUS];
//                 go.transform.localScale = new Vector3(2f * rr, h * 0.5f, 2f * rr);
//                 go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
//                 return go;

//             case SolidPrimitiveMsg.CONE:
//                 // Approximate with a cylinder (swap with a proper cone mesh if desired)
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
//                 go.name = name + "_approxCone";
//                 float ch = (float)prim.dimensions[SolidPrimitiveMsg.CONE_HEIGHT];
//                 float cr = (float)prim.dimensions[SolidPrimitiveMsg.CONE_RADIUS];
//                 go.transform.localScale = new Vector3(2f * cr, ch * 0.5f, 2f * cr);
//                 go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
//                 return go;

//             default:
//                 Debug.LogWarning($"[CO Listener] Unsupported primitive {prim.type}; using cube.");
//                 go = GameObject.CreatePrimitive(PrimitiveType.Cube);
//                 go.name = name + "_unknown";
//                 return go;
//         }
//     }

//     GameObject BuildMesh(string name, MeshMsg meshMsg)
//     {
//         if (meshMsg.vertices == null || meshMsg.vertices.Length == 0 ||
//             meshMsg.triangles == null || meshMsg.triangles.Length == 0)
//         {
//             Debug.LogWarning("[CO Listener] Mesh has no vertices/triangles; skipping.");
//             return null;
//         }

//         var go = new GameObject(name);
//         var mf = go.AddComponent<MeshFilter>();
//         var mr = go.AddComponent<MeshRenderer>();
//         mr.sharedMaterial = new Material(Shader.Find("Standard"));

//         var uMesh = new Mesh();
//         var verts = new Vector3[meshMsg.vertices.Length];
//         for (int i = 0; i < verts.Length; i++)
//         {
//             // ROS (x,y,z) → Unity (x,z,y)
//             verts[i] = new Vector3(
//                 (float)meshMsg.vertices[i].x,
//                 (float)meshMsg.vertices[i].z,
//                 (float)meshMsg.vertices[i].y
//             );
//         }

//         var tris = new System.Collections.Generic.List<int>(meshMsg.triangles.Length * 3);
//         for (int t = 0; t < meshMsg.triangles.Length; t++)
//         {
//             var ids = meshMsg.triangles[t].vertex_indices;
//             tris.Add((int)ids[0]);
//             tris.Add((int)ids[1]);
//             tris.Add((int)ids[2]);
//         }

//         uMesh.SetVertices(verts);
//         uMesh.SetTriangles(tris, 0);
//         uMesh.RecalculateNormals();
//         uMesh.RecalculateBounds();

//         mf.sharedMesh = uMesh;
//         return go;
//     }

//     GameObject BuildPlane(string name, PlaneMsg plane)
//     {
//         // Simple big quad; not using coef directly beyond orientation from pose.
//         var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
//         go.name = name;
//         go.transform.localScale = new Vector3(10f, 10f, 1f);
//         return go;
//     }

//     // ---------- Placement & ROS→Unity axis remap ----------

//     void Place(Transform child, PoseMsg rosPose)
//     {
//         Vector3 pos = new Vector3(
//             (float)rosPose.position.x,
//             (float)rosPose.position.z,
//             (float)rosPose.position.y
//         );
//         Quaternion rot = new Quaternion(
//             (float)rosPose.orientation.x,
//             (float)rosPose.orientation.z,
//             (float)rosPose.orientation.y,
//             (float)rosPose.orientation.w
//         );
//         child.position = worldOrigin.TransformPoint(pos);
//         child.rotation = worldOrigin.rotation * rot;
//     }
// }
