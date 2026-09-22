using Erupt.Ros;
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
    private Transform worldOriginTransform => worldOrigin ? worldOrigin.transform : this.transform;

    [Header("Materials")]
    public Material litMaterial;

    private IRosBus ros;
    public Dictionary<string, GameObject> objectsById = new();

    // Ids currently attached to the robot (maintained by AttachedCollisionObjectListener).
    // While an id is in here, inbound REMOVE/MOVE/ADD for it are ignored — the object is
    // being carried by the gripper, and /attached_collision_objects_ros owns its pose.
    public readonly HashSet<string> attachedIds = new();

    // Ids just detached from the robot. The ADD that follows carries the authoritative place
    // pose, which is applied even to Unity-owned objects (the robot moved it, not Unity).
    public readonly HashSet<string> awaitingDetachPose = new();

    // Fired when the registry is cleared (scene change / teardown).
    // The argument is true when the listener itself is being destroyed.
    public event System.Action<bool> OnRegistryCleared;

    // id -> signature of the geometry its children were last built from, so a re-ADD with
    // unchanged geometry only moves the object instead of destroying and respawning it.
    private readonly Dictionary<string, string> _geometrySignatureById = new();

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
        ClearRegistry(destroyObjects: false, tearingDown: true);
    }

    /// <summary>
    /// Forget every mirrored object (scene change / teardown). Listeners such as
    /// AttachedCollisionObjectListener drop their attach state via OnRegistryCleared first,
    /// so nothing is left orphaned under a robot link.
    /// </summary>
    public void ClearRegistry(bool destroyObjects = true, bool tearingDown = false)
    {
        OnRegistryCleared?.Invoke(tearingDown);

        if (destroyObjects)
        {
            foreach (var kv in objectsById)
            {
                if (!kv.Value || _publisherOwnedIds.Contains(kv.Key)) continue;
                foreach (var pub in kv.Value.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.suppressRemoveOnDestroy = true;
                Destroy(kv.Value);
            }
        }

        objectsById.Clear();
        _publisherOwnedIds.Clear();
        _geometrySignatureById.Clear();
        attachedIds.Clear();
        awaitingDetachPose.Clear();
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
            objectsById[id] = unityObject;
    }

    public void UnregisterUnityOwnedObject(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _publisherOwnedIds.Remove(id);
        objectsById.Remove(id);
        awaitingDetachPose.Remove(id);
    }

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

        // The robot is carrying this object. The fixed watcher stays quiet for it; this guards
        // against message reordering and older watcher builds.
        if (attachedIds.Contains(co.id))
        {
            Debug.Log($"[CO Listener] Ignoring op={co.operation} for attached object id={co.id}");
            return;
        }

        if (TryGetUnityOwnedObject(co.id, out GameObject unityOwnedObject))
        {
            if (co.operation == OP_REMOVE)
            {
                foreach (var pub in unityOwnedObject.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.suppressRemoveOnDestroy = true;
                Destroy(unityOwnedObject);
                UnregisterUnityOwnedObject(co.id);
                return;
            }

            if (awaitingDetachPose.Remove(co.id) && co.pose != null)
            {
                // Post-detach re-add: take the place pose, keep the Unity-owned geometry.
                ApplyWorldPose(unityOwnedObject.transform, co.pose);
                MarkPoseAsPublished(unityOwnedObject);
                OnObjectUpdated?.Invoke(co.id);
                return;
            }

            Debug.Log($"[CO Listener] Skipping '{co.id}' — already managed by CollisionObjectPublisher.");
            return;
        }

        if (co.operation == OP_REMOVE)
        {
            awaitingDetachPose.Remove(co.id);
            _geometrySignatureById.Remove(co.id);
            Debug.Log($"[CO Listener] Removing object id={co.id}");
            if (objectsById.TryGetValue(co.id, out var old) && old)
            {
                // This destroy was commanded by ROS — don't let the object's own publisher
                // echo a REMOVE back to /collision_object and erase it from MoveIt's scene.
                foreach (var pub in old.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.suppressRemoveOnDestroy = true;
                Destroy(old);
            }
            objectsById.Remove(co.id);
            return;
        }

        Debug.Log($"[CO Listener] Adding/Appending/Moving object id={co.id}");
        awaitingDetachPose.Remove(co.id);
        Upsert(co, applyWorldPose: true);
        OnObjectUpdated?.Invoke(co.id);
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
        return Upsert(co, applyWorldPose: false);
    }

    GameObject Upsert(CollisionObjectMsg co, bool applyWorldPose)
    {
        // ADD / APPEND / MOVE → upsert parent
        if (!objectsById.TryGetValue(co.id, out var parent) || !parent)
        {
            parent = new GameObject(co.id);
            parent.transform.SetParent(worldOriginTransform, true);
            objectsById[co.id] = parent;
        }

        Debug.Log($"[CO Listener] Processing object id={co.id} with {co.primitives?.Length ?? 0} primitives, {co.meshes?.Length ?? 0} meshes, {co.planes?.Length ?? 0} planes");

        // ----- Place parent in world using co.pose -----
        if (applyWorldPose)
        {
            var objPose = co.pose ?? new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));
            ApplyWorldPose(parent.transform, objPose);
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
            child.GetComponent<XRGrabInteractable>().selectMode = InteractableSelectMode.Multiple;
            // Keep the object where it's grabbed instead of snapping it to the controller
            child.GetComponent<XRGrabInteractable>().useDynamicAttach = true;
            // Don't match the ray hit point's position for the attach anchor — keep it at the
            // object's own pivot so joystick rotation spins the object about its own center
            // instead of orbiting around wherever the ray happened to hit its surface.
            child.GetComponent<XRGrabInteractable>().matchAttachPosition = false;
            // Don't apply release velocity, object should stop moving as soon as it's let go
            child.GetComponent<XRGrabInteractable>().throwOnDetach = false;

            // Add component to control grabbing based on selection state
            child.AddComponent<SelectableGrabController>();

            // Add tag for selection
            child.tag = "Selectable";

            child.AddComponent<XRGrabTransformerScaleAxisLock>();
            child.AddComponent<XRGrabTransformerLockPose>();
            // Don't freeze rotation by default — joystick manipulation should be able to
            // spin the object about its own center instead of being locked in place.
            child.GetComponent<XRGrabTransformerLockPose>().freezePose = false;

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
                // Keep the object where it's grabbed instead of snapping it to the controller
                child.GetComponent<XRGrabInteractable>().useDynamicAttach = true;
                // Don't apply release velocity, object should stop moving as soon as it's let go
                child.GetComponent<XRGrabInteractable>().throwOnDetach = false;
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

        return parent;
    }

    static void MarkPoseAsPublished(GameObject go)
    {
        foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
            pub.MarkTransformAsPublished();
    }

    static string GeometrySignature(CollisionObjectMsg co)
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

    /// <summary>
    /// Places a transform at a ROS world pose, with the same conversion inbound collision
    /// objects get. For callers (the solution preview) that show objects at a planned pose.
    /// </summary>
    public void ApplyRosWorldPose(Transform t, PoseMsg rosPose)
    {
        if (t != null && rosPose != null) ApplyWorldPose(t, rosPose);
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
