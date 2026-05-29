using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Publishes real-world objects detected via Meta Quest Space Setup as MoveIt collision
/// objects on /collision_object, and renders a color-coded semi-transparent box for each
/// anchor so you can see the boundaries in AR. Requires an MRUK component in the scene
/// and Space Setup to have been run on the device.
/// </summary>
public class SceneAnchorCollisionBridge : MonoBehaviour
{
    [Tooltip("ROS frame ID for published collision objects.")]
    public string frameId = "world";

    [Tooltip("Thickness (meters) added to plane anchors (walls, floor, ceiling) along their normal.")]
    public float planeDepth = 0.05f;

    [Tooltip("Opacity of plane overlays (floor, walls, ceiling).")]
    [Range(0f, 1f)]
    public float visualAlpha = 0.3f;

    [Tooltip("Opacity of volume overlays (furniture, tables, storage).")]
    [Range(0f, 1f)]
    public float volumeAlpha = 0.6f;

    [Tooltip("Publish plane anchors (floor, ceiling, walls) to MoveIt as collision objects. " +
             "Disable if you don't need them in the robot planning scene.")]
    public bool publishPlanes = false;

    [Tooltip("Assign the CollisionObjectsListenerSimple in the scene so anchor IDs are registered " +
             "and not re-spawned when the planning_scene_watcher bounces them back.")]
    public CollisionObjectsListenerSimple collisionObjectsListener;

    [Tooltip("World-frame reference object — same as worldOrigin on CollisionObjectPublisher. " +
             "If null, Unity world space is used directly.")]
    public GameObject worldOrigin;

    private ROSConnection _ros;
    private Mesh _cubeMesh;
    private Mesh _quadMesh;
    private readonly List<string> _publishedIds = new();
    private readonly List<GameObject> _visualBoxes = new();

    private struct AnchorData
    {
        public MRUKAnchor anchor;
        public Vector3 localCenter;
        public Vector3 size;
        public bool isPlane;
        public GameObject visual;
    }
    private readonly List<AnchorData> _anchorData = new();

    private bool _hasFloor;
    private Vector3 _floorPosition;
    private Vector3 _floorNormal;

    public bool HasFloor => _hasFloor;
    public Vector3 FloorPosition => _floorPosition;
    public Vector3 FloorNormal => _floorNormal;

    // Lightweight FPS log so we can see performance via adb even without a ROS receiver.
    private float _fpsAccum;
    private int _fpsSamples;
    private float _fpsTimer;

    void Update()
    {
        _fpsAccum += 1f / Time.smoothDeltaTime;
        _fpsSamples++;
        _fpsTimer += Time.deltaTime;
        if (_fpsTimer >= 2f)
        {
            Debug.Log($"[FPS] {_fpsAccum / _fpsSamples:F1}");
            _fpsAccum = 0f;
            _fpsSamples = 0;
            _fpsTimer = 0f;
        }
    }

    void Start()
    {
        Debug.Log("[SceneAnchorCollisionBridge] Start() — build is current.");

        _ros = ROSConnection.GetOrCreateInstance();
        _ros.RegisterPublisher<CollisionObjectMsg>("/collision_object");

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        var tmpQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quadMesh = tmpQuad.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmpQuad);

        if (MRUK.Instance == null)
        {
            Debug.LogError("[SceneAnchorCollisionBridge] MRUK.Instance is NULL — no MRUK component in scene.");
            return;
        }

        Debug.Log("[SceneAnchorCollisionBridge] MRUK.Instance found. Subscribing to SceneLoaded + RoomCreated.");

        // Subscribe to both events — SceneLoadedEvent fires when all rooms finish loading,
        // RoomCreatedEvent fires per-room.
        MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
        MRUK.Instance.RoomCreatedEvent.AddListener(OnRoomCreated);

        // Handle the case where MRUK already finished loading before our Start() ran.
        var rooms = MRUK.Instance.GetRooms();
        if (rooms != null && rooms.Count > 0)
        {
            Debug.Log($"[SceneAnchorCollisionBridge] Rooms already loaded ({rooms.Count}). Processing now.");
            foreach (var r in rooms)
                ProcessRoom(r);
        }
        else
        {
            Debug.Log("[SceneAnchorCollisionBridge] No rooms yet — waiting for RoomCreatedEvent.");
        }
    }

    void OnDisable()
    {
        if (MRUK.Instance != null)
        {
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
        }
    }

    void OnSceneLoaded()
    {
        var rooms = MRUK.Instance?.GetRooms();
        int count = rooms?.Count ?? 0;
        Debug.Log($"[SceneAnchorCollisionBridge] SceneLoadedEvent fired — {count} room(s) available.");
        if (count == 0)
            Debug.LogWarning("[SceneAnchorCollisionBridge] Scene loaded but 0 rooms. Has Space Setup been run on this device?");
    }

    void OnRoomCreated(MRUKRoom room)
    {
        Debug.Log($"[SceneAnchorCollisionBridge] RoomCreatedEvent — {room.Anchors.Count} anchor(s).");
        ProcessRoom(room);
    }

    void ProcessRoom(MRUKRoom room)
    {
        foreach (var anchor in room.Anchors)
            ProcessAnchor(anchor);
    }

    void ProcessAnchor(MRUKAnchor anchor)
    {
        Vector3 localCenter;
        Vector3 size;
        bool isPlane;

        if (anchor.VolumeBounds.HasValue)
        {
            Bounds b = anchor.VolumeBounds.Value;
            localCenter = b.center;
            size = b.size;
            isPlane = false;
            Debug.Log($"[SceneAnchorCollisionBridge] Volume anchor: label={anchor.Label} size={size:F2}");
        }
        else if (anchor.PlaneRect.HasValue)
        {
            Rect r = anchor.PlaneRect.Value;
            localCenter = new Vector3(r.center.x, r.center.y, 0f);
            size = new Vector3(r.width, r.height, planeDepth);
            isPlane = true;
            Debug.Log($"[SceneAnchorCollisionBridge] Plane anchor: label={anchor.Label} size={r.width:F2}x{r.height:F2}");
        }
        else
        {
            Debug.Log($"[SceneAnchorCollisionBridge] Anchor with no bounds/rect skipped: label={anchor.Label}");
            return;
        }

        Vector3 worldCenter = anchor.transform.TransformPoint(localCenter);
        Quaternion worldRotation = anchor.transform.rotation;

        if ((anchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0)
        {
            _hasFloor = true;
            _floorPosition = worldCenter;
            _floorNormal = anchor.transform.forward;
        }

        var visual = SpawnVisual(anchor, size, worldCenter, worldRotation, isPlane);
        if (!isPlane || publishPlanes)
            PublishBox(anchor, worldCenter, size, visual);

        _anchorData.Add(new AnchorData
        {
            anchor = anchor,
            localCenter = localCenter,
            size = size,
            isPlane = isPlane,
            visual = visual
        });
    }

    GameObject SpawnVisual(MRUKAnchor anchor, Vector3 size, Vector3 worldCenter, Quaternion worldRotation, bool isPlane)
    {
        var go = new GameObject($"Anchor_{anchor.Label}");
        go.transform.SetParent(this.transform, worldPositionStays: true);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        if (isPlane)
        {
            // MRUK plane normal = anchor.transform.forward (+Z).
            // Offset 2cm inward (into the room) so the quad sits just above the real surface
            // and isn't depth-culled by the actual floor/wall/ceiling geometry in AR passthrough.
            Vector3 offsetPos = worldCenter + anchor.transform.forward * 0.02f;
            go.transform.SetPositionAndRotation(offsetPos, worldRotation);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            mf.sharedMesh = _quadMesh;
            // Double-sided so the quad is visible regardless of normal orientation.
            mr.sharedMaterial = MakePlaneMaterial(LabelColor(anchor.Label, visualAlpha));
        }
        else
        {
            // Volume anchors (furniture, storage): solid box.
            go.transform.SetPositionAndRotation(worldCenter, worldRotation);
            go.transform.localScale = size;
            mf.sharedMesh = _cubeMesh;
            mr.sharedMaterial = MakeTransparentMaterial(LabelColor(anchor.Label, volumeAlpha));
            // BoxCollider default (size 1,1,1 in local space) matches the cube mesh at this scale,
            // enabling downward raycasts for surface-aware robot placement.
            go.AddComponent<BoxCollider>();
        }

        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        _visualBoxes.Add(go);
        return go;
    }

    public void SetVisualsVisible(bool visible)
    {
        foreach (var box in _visualBoxes)
        {
            if (box != null)
                box.SetActive(visible);
        }
    }

    void PublishBox(MRUKAnchor anchor, Vector3 worldCenter, Vector3 size, GameObject visual, sbyte operation = 0)
    {
        string id = $"scene_{anchor.Label}_{anchor.GetInstanceID()}";

        Vector3 publishPos = worldOrigin != null
            ? worldOrigin.transform.InverseTransformPoint(worldCenter)
            : worldCenter;
        Quaternion publishRot = worldOrigin != null
            ? Quaternion.Inverse(worldOrigin.transform.rotation) * anchor.transform.rotation
            : anchor.transform.rotation;

        var msg = new CollisionObjectMsg
        {
            id = id,
            header = new HeaderMsg { frame_id = frameId, stamp = RosTimestamp() },
            operation = operation,
            pose = new PoseMsg
            {
                position = RosUnityConversion.UnityToRosPosition(publishPos),
                orientation = RosUnityConversion.UnityToRosQuaternion(publishRot)
            },
            primitives = new[]
            {
                new SolidPrimitiveMsg
                {
                    type = SolidPrimitiveMsg.BOX,
                    dimensions = new double[] { size.x, size.z, size.y }
                }
            }
        };

        _ros.Publish("/collision_object", msg);
        _publishedIds.Add(id);

        // Register with the listener so it ignores this ID when the planning_scene_watcher
        // bounces it back via /collision_objects_ros — same pattern as WristMenuController.
        if (collisionObjectsListener != null && !collisionObjectsListener.objectsById.ContainsKey(id))
            collisionObjectsListener.objectsById[id] = visual;

        Debug.Log($"[SceneAnchorCollisionBridge] Published {anchor.Label} as '{id}', size={size}");
    }

    public void RepublishAll()
    {
        // Remove all previously published anchors from MoveIt and the listener registry.
        foreach (var id in _publishedIds)
        {
            _ros.Publish("/collision_object", new CollisionObjectMsg
            {
                id = id,
                header = new HeaderMsg { frame_id = frameId, stamp = RosTimestamp() },
                operation = CollisionObjectMsg.REMOVE
            });
            collisionObjectsListener?.objectsById.Remove(id);
        }
        _publishedIds.Clear();

        // Re-publish each anchor with freshly computed poses.
        foreach (var data in _anchorData)
        {
            if (data.isPlane && !publishPlanes) continue;
            Vector3 worldCenter = data.anchor.transform.TransformPoint(data.localCenter);
            PublishBox(data.anchor, worldCenter, data.size, data.visual);
        }
    }

    void OnDestroy()
    {
        if (_ros == null) return;
        foreach (var id in _publishedIds)
        {
            _ros.Publish("/collision_object", new CollisionObjectMsg
            {
                id = id,
                header = new HeaderMsg { frame_id = frameId, stamp = RosTimestamp() },
                operation = CollisionObjectMsg.REMOVE
            });
        }
    }

    static Color LabelColor(MRUKAnchor.SceneLabels label, float alpha)
    {
        Color c = label switch
        {
            MRUKAnchor.SceneLabels.FLOOR        => Color.green,
            MRUKAnchor.SceneLabels.CEILING      => Color.red,
            MRUKAnchor.SceneLabels.WALL_FACE    => Color.white,
            MRUKAnchor.SceneLabels.TABLE        => Color.yellow,
            MRUKAnchor.SceneLabels.COUCH        => Color.blue,
            MRUKAnchor.SceneLabels.STORAGE      => new Color(1f, 0.5f, 0f),
            MRUKAnchor.SceneLabels.BED          => new Color(0.6f, 0f, 0.8f),
            MRUKAnchor.SceneLabels.DOOR_FRAME   => Color.magenta,
            MRUKAnchor.SceneLabels.WINDOW_FRAME => Color.cyan,
            _                                   => Color.grey,
        };
        c.a = alpha;
        return c;
    }

    static Material MakeTransparentMaterial(Color color)
    {
        return BuildTransparentMaterial(color, doubleSided: false);
    }

    // Plane surfaces need double-sided rendering (_Cull Off) so they're visible regardless
    // of which direction the anchor normal faces.
    static Material MakePlaneMaterial(Color color)
    {
        return BuildTransparentMaterial(color, doubleSided: true);
    }

    static Material BuildTransparentMaterial(Color color, bool doubleSided)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader);
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.enableInstancing = true;

        if (shader.name.Contains("Universal Render Pipeline"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_Cull", doubleSided ? 0 : 2); // 0=Off (double-sided), 2=Back
            mat.SetColor("_BaseColor", color);
        }
        else
        {
            mat.SetFloat("_Mode", 2f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_Cull", doubleSided ? 0 : 2);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.SetColor("_Color", color);
        }

        return mat;
    }

    static TimeMsg RosTimestamp()
    {
        long ticks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return new TimeMsg
        {
            sec = (int)(ticks / TimeSpan.TicksPerSecond),
            nanosec = (uint)((ticks % TimeSpan.TicksPerSecond) * 100L)
        };
    }
}
