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
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

public class CollisionObjectsListenerSimple : MonoBehaviour
{
    [Header("ROS")]
    public string topic = "/collision_objects_ros";

    [Tooltip("MoveIt service used to fetch objects that existed before this subscriber connected.")]
    public string planningSceneServiceTopic = "/get_planning_scene";

    [Tooltip("Request a full world-object snapshot after subscribing to live updates.")]
    public bool requestInitialPlanningScene = true;

    [Header("Frame Root (ROS world frame)")]
    public GameObject worldOrigin; // If null, uses this.transform

    [Tooltip("Environment mirror every spawned or registered object is recorded in. Found in the scene if null.")]
    [SerializeField] private EnvironmentRegistry registry;
    private Transform worldOriginTransform => worldOrigin ? worldOrigin.transform : this.transform;

    [Header("Materials")]
    public Material litMaterial;

    private IRosBus ros;
    public Dictionary<string, GameObject> objectsById = new();

    // Ids currently attached to the robot (maintained by AttachedCollisionObjectListener).
    // While an id is in here, inbound REMOVEs for it are ignored — the object is being
    // carried by the gripper, not deleted.
    public readonly HashSet<string> attachedIds = new();

    // Fired after an inbound ADD/APPEND/MOVE has been applied to the GameObject for this id.
    public event System.Action<string> OnObjectUpdated;

    // IDs represented by Unity-owned GameObjects. Echoes from planning_scene_watcher must
    // not create a second renderer at the same pose.
    private readonly HashSet<string> _publisherOwnedIds = new();

    // MoveIt op codes (per message spec)
    const byte OP_ADD = 0;
    const byte OP_REMOVE = 1;
    const byte OP_APPEND = 2;
    const byte OP_MOVE = 3;

    void Awake()
    {
        if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
    }

    void Start()
    {
        ros = RosBus.Instance;

        foreach (var pub in FindObjectsByType<CollisionObjectPublisher>(FindObjectsSortMode.None))
            if (!string.IsNullOrEmpty(pub.objectId))
                RegisterUnityOwnedObject(pub.objectId, pub.gameObject);

        // Subscribe before requesting the snapshot so an update that occurs while the
        // service request is in flight cannot be missed.
        ros.Subscribe<CollisionObjectMsg>(topic, OnCollisionObject);

        if (requestInitialPlanningScene)
            RequestInitialPlanningScene();
    }

    void OnDestroy()
    {
        if (ros != null)
            ros.Unsubscribe<CollisionObjectMsg>(topic, OnCollisionObject);
    }

    private void RequestInitialPlanningScene()
    {
        try
        {
            ros.RegisterRosService<GetPlanningSceneRequest, GetPlanningSceneResponse>(planningSceneServiceTopic);

            uint components = PlanningSceneComponentsMsg.WORLD_OBJECT_NAMES
                            | PlanningSceneComponentsMsg.WORLD_OBJECT_GEOMETRY;
            var request = new GetPlanningSceneRequest(new PlanningSceneComponentsMsg(components));
            ros.SendServiceMessage<GetPlanningSceneResponse>(
                planningSceneServiceTopic, request, OnInitialPlanningSceneReceived);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[CO Listener] Could not request the initial planning scene: {e.Message}");
        }
    }

    private void OnInitialPlanningSceneReceived(GetPlanningSceneResponse response)
    {
        CollisionObjectMsg[] objects = response?.scene?.world?.collision_objects;
        if (objects == null)
        {
            Debug.LogWarning("[CO Listener] Initial planning-scene response contained no world object list.");
            return;
        }

        Debug.Log($"[CO Listener] Applying initial planning-scene snapshot ({objects.Length} objects).");
        foreach (CollisionObjectMsg collisionObject in objects)
            OnCollisionObject(collisionObject);
    }

    /// <summary>
    /// Registers a GameObject whose ROS id originated in Unity. The watcher will echo
    /// that id back, but the listener must keep the existing object rather than rebuild
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
        registry?.Unregister(id, RemovalOrigin.Local);
    }

    /// <summary>True when the id was registered as originating in Unity.</summary>
    public bool IsUnityOwned(string id) => !string.IsNullOrEmpty(id) && _publisherOwnedIds.Contains(id);

    private bool TryGetUnityOwnedObject(string id, out GameObject unityObject)
    {
        unityObject = null;
        if (!_publisherOwnedIds.Contains(id)) return false;

        if (objectsById.TryGetValue(id, out unityObject) && unityObject != null)
            return true;

        // The owner may have been destroyed locally before the watcher echoed REMOVE.
        _publisherOwnedIds.Remove(id);
        objectsById.Remove(id);
        unityObject = null;
        return false;
    }

    void OnCollisionObject(CollisionObjectMsg co)
    {
        if (string.IsNullOrEmpty(co.id))
        {
            Debug.LogWarning("[CO Listener] Empty id; ignoring.");
            return;
        }

        if (TryGetUnityOwnedObject(co.id, out GameObject unityOwnedObject))
        {
            if (co.operation == OP_REMOVE)
            {
                foreach (var pub in unityOwnedObject.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.suppressRemoveOnDestroy = true;
                registry?.Unregister(co.id, RemovalOrigin.Remote);
                Destroy(unityOwnedObject);
                UnregisterUnityOwnedObject(co.id);
                return;
            }

            Debug.Log($"[CO Listener] Skipping '{co.id}' — already managed by CollisionObjectPublisher.");
            return;
        }

        if (co.operation == OP_REMOVE)
        {
            if (attachedIds.Contains(co.id))
            {
                Debug.Log($"[CO Listener] Ignoring REMOVE for attached object id={co.id}");
                return;
            }
            Debug.Log($"[CO Listener] Removing object id={co.id}");
            if (objectsById.TryGetValue(co.id, out var old) && old)
            {
                // This destroy was commanded by ROS — don't let the object's own publisher
                // echo a REMOVE back to /collision_object and erase it from MoveIt's scene.
                foreach (var pub in old.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.suppressRemoveOnDestroy = true;
                registry?.Unregister(co.id, RemovalOrigin.Remote);
                Destroy(old);
            }
            objectsById.Remove(co.id);
            return;
        }

        Debug.Log($"[CO Listener] Adding/Appending/Moving object id={co.id}");

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
        var objPose = co.pose ?? new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
        ApplyWorldPose(parent.transform, objPose);

        Debug.Log($"[CO Listener] Applied world pose to '{co.id}': position=({objPose.position.x}, {objPose.position.y}, {objPose.position.z}), orientation=({objPose.orientation.x}, {objPose.orientation.y}, {objPose.orientation.z}, {objPose.orientation.w})");

        // MOVE (and any other geometry-less message) only updates the pose — rebuilding here
        // would destroy all visuals since there is no geometry to rebuild from.
        bool hasGeometry = (co.primitives?.Length ?? 0) > 0
                        || (co.meshes?.Length ?? 0) > 0
                        || (co.planes?.Length ?? 0) > 0;
        if (!hasGeometry)
        {
            Debug.Log($"[CO Listener] No geometry in message for '{co.id}'; pose-only update.");
            OnObjectUpdated?.Invoke(co.id);
            return;
        }

        // Rebuild children fresh for correctness
        for (int i = parent.transform.childCount - 1; i >= 0; i--)
            Destroy(parent.transform.GetChild(i).gameObject);

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

            // Add physics components
            Rigidbody rb = child.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;

            // Add XR interaction (will be controlled by SelectableGrabController)
            child.AddComponent<XRGrabInteractable>();
            child.GetComponent<XRGrabInteractable>().selectMode = InteractableSelectMode.Single;

            // Add component to control grabbing based on selection state
            child.AddComponent<SelectableGrabController>();

            // Add tag for selection
            child.tag = "Selectable";

            child.AddComponent<XRGrabTransformerScaleAxisLock>();
            child.AddComponent<XRGrabTransformerLockPose>();

            // Add an XR General Grab Transformer
            child.AddComponent<XRGeneralGrabTransformer>();
            child.GetComponent<XRGeneralGrabTransformer>().allowTwoHandedScaling = false;
            child.GetComponent<XRGeneralGrabTransformer>().clampScaling = false;

            child.AddComponent<XRTwoHandedScaleTransformer>();
            child.AddComponent<XRUIScaleTransformer>();

            child.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(child.GetComponent<XRGeneralGrabTransformer>());

            child.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(child.GetComponent<XRTwoHandedScaleTransformer>());

            child.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(child.GetComponent<XRGrabTransformerScaleAxisLock>());

            child.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(child.GetComponent<XRGrabTransformerLockPose>());

            child.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(child.GetComponent<XRUIScaleTransformer>());

            // Ensure collider is enabled (should already be there from CreatePrimitive)
            Collider collider = child.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = true;
            }

            // Add CollisionObjectPublisher to automatically publish to ROS
            CollisionObjectPublisher publisher = child.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = false;
            // Generate unique ID for each object
            publisher.objectId = name;
            publisher.hasBeenPublished = true;
            publisher.worldOrigin = worldOrigin;
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

                // Add physics components
                Rigidbody rb = child.AddComponent<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = true;

                // Add tag for selection
                child.tag = "Selectable";

                // Ensure collider is enabled (should already be there from CreatePrimitive)
                Collider collider = child.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = true;
                }
                else
                {
                    var c = child.AddComponent<MeshCollider>();
                }

                // Add XR interaction (will be controlled by SelectableGrabController)
                child.AddComponent<XRGrabInteractable>();
                child.AddComponent<XRGeneralGrabTransformer>();

                // Add component to control grabbing based on selection state
                child.AddComponent<SelectableGrabController>();

                // Add CollisionObjectPublisher to automatically publish to ROS
                CollisionObjectPublisher publisher = child.AddComponent<CollisionObjectPublisher>();
                publisher.isMesh = true;
                // Generate unique ID for each object
                publisher.objectId = name;
                publisher.hasBeenPublished = true;
                publisher.worldOrigin = worldOrigin;
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

        OnObjectUpdated?.Invoke(co.id);
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

    void ApplyWorldPose(Transform t, PoseMsg rosPose)
    {
        Vector3 pos = RosUnityConversion.RosToUnityPosition(rosPose.position);
        Quaternion rot = RosUnityConversion.RosToUnityQuaternion(rosPose.orientation);

        t.position = worldOriginTransform.TransformPoint(pos);
        t.rotation = worldOriginTransform.rotation * rot;
    }

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
