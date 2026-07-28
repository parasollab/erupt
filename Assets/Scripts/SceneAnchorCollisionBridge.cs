using System;
using System.Collections.Generic;
#if ERUPT_USE_META_XR && UNITY_ANDROID && !UNITY_VISIONOS
using Meta.XR.MRUtilityKit;
#endif
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Publishes real-world surfaces as MoveIt collision objects.
/// VisionOS and generic OpenXR builds use ARFoundation planes. Quest builds can opt into
/// Meta MRUK by adding ERUPT_USE_META_XR to Android scripting define symbols.
/// </summary>
public class SceneAnchorCollisionBridge : MonoBehaviour
{
    public enum SurfaceProviderMode
    {
        Auto,
        ARFoundationPlanes,
        MetaMRUK
    }

    enum SurfaceKind
    {
        Unknown,
        Floor,
        Ceiling,
        Wall,
        Table,
        Seat,
        Storage,
        Door,
        Window,
        Volume
    }

    [Tooltip("Which runtime surface provider to use. Auto selects ARFoundation on visionOS and Meta MRUK only on Android builds with ERUPT_USE_META_XR.")]
    public SurfaceProviderMode providerMode = SurfaceProviderMode.Auto;

    [Tooltip("ARFoundation plane manager used on visionOS/OpenXR builds. If null, the scene is searched at runtime.")]
    public ARPlaneManager arPlaneManager;

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

    [Tooltip("World-frame reference object - same as worldOrigin on CollisionObjectPublisher. " +
             "If null, Unity world space is used directly.")]
    public GameObject worldOrigin;

    private ROSConnection _ros;
    private Mesh _cubeMesh;
    private readonly List<string> _publishedIds = new();
    private readonly List<GameObject> _visualBoxes = new();
    private readonly Dictionary<string, AnchorData> _anchorDataById = new();

    struct AnchorData
    {
        public string id;
        public string label;
        public SurfaceKind kind;
        public Vector3 worldCenter;
        public Quaternion worldRotation;
        public Vector3 worldNormal;
        public Vector3 size;
        public bool isPlane;
        public GameObject visual;
    }

    private bool _hasFloor;
    private Vector3 _floorPosition;
    private Vector3 _floorNormal = Vector3.up;

    public bool HasFloor => _hasFloor;
    public Vector3 FloorPosition => _floorPosition;
    public Vector3 FloorNormal => _floorNormal;

    private float _fpsAccum;
    private int _fpsSamples;
    private float _fpsTimer;

    void Start()
    {
        Debug.Log($"[SceneAnchorCollisionBridge] Starting with provider {ResolveProviderMode()}.");

        _ros = ROSConnection.GetOrCreateInstance();
        _ros.RegisterPublisher<CollisionObjectMsg>("/collision_object", CollisionObjectPublisher.CollisionObjectQueueSize);

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        switch (ResolveProviderMode())
        {
            case SurfaceProviderMode.MetaMRUK:
                StartMetaMRUK();
                break;
            case SurfaceProviderMode.ARFoundationPlanes:
                StartARFoundationPlanes();
                break;
        }
    }

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

    void OnDisable()
    {
        StopARFoundationPlanes();
        StopMetaMRUK();
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

    SurfaceProviderMode ResolveProviderMode()
    {
        if (providerMode != SurfaceProviderMode.Auto)
            return providerMode;

#if ERUPT_USE_META_XR && UNITY_ANDROID && !UNITY_VISIONOS
        return SurfaceProviderMode.MetaMRUK;
#else
        return SurfaceProviderMode.ARFoundationPlanes;
#endif
    }

    void StartARFoundationPlanes()
    {
        arPlaneManager ??= FindObjectOfType<ARPlaneManager>();
        if (arPlaneManager == null)
        {
            Debug.LogWarning("[SceneAnchorCollisionBridge] No ARPlaneManager found. Add one to the XR Origin for visionOS plane collisions.");
            return;
        }

        arPlaneManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
        arPlaneManager.trackablesChanged.AddListener(OnARPlanesChanged);
        arPlaneManager.enabled = true;

        foreach (var plane in arPlaneManager.trackables)
            ProcessARPlane(plane);

        Debug.Log("[SceneAnchorCollisionBridge] Using ARFoundation planes for real-world collision objects.");
    }

    void StopARFoundationPlanes()
    {
        if (arPlaneManager != null)
            arPlaneManager.trackablesChanged.RemoveListener(OnARPlanesChanged);
    }

    void OnARPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
    {
        foreach (var plane in args.added)
            ProcessARPlane(plane);

        foreach (var plane in args.updated)
            ProcessARPlane(plane);

        foreach (var removed in args.removed)
            RemovePublishedAnchor(ARPlaneId(removed.Key.ToString()));
    }

    void ProcessARPlane(ARPlane plane)
    {
        if (plane == null || plane.subsumedBy != null)
            return;

        var id = ARPlaneId(plane.trackableId.ToString());
        var kind = SurfaceKindForARPlane(plane);
        var label = kind == SurfaceKind.Unknown ? plane.alignment.ToString() : kind.ToString();
        var size = new Vector3(Mathf.Max(plane.size.x, 0.01f), Mathf.Max(planeDepth, 0.001f), Mathf.Max(plane.size.y, 0.01f));
        var rotation = plane.transform.rotation;
        var center = plane.center + plane.normal * 0.01f;

        UpsertAnchor(new AnchorData
        {
            id = id,
            label = label,
            kind = kind,
            worldCenter = center,
            worldRotation = rotation,
            worldNormal = plane.normal,
            size = size,
            isPlane = true
        });
    }

    static string ARPlaneId(string trackableId)
    {
        return $"scene_arfoundation_{trackableId}";
    }

    static SurfaceKind SurfaceKindForARPlane(ARPlane plane)
    {
        if ((plane.classifications & PlaneClassifications.Floor) != 0)
            return SurfaceKind.Floor;
        if ((plane.classifications & PlaneClassifications.Ceiling) != 0)
            return SurfaceKind.Ceiling;
        if ((plane.classifications & PlaneClassifications.WallFace) != 0)
            return SurfaceKind.Wall;
        if ((plane.classifications & PlaneClassifications.Table) != 0)
            return SurfaceKind.Table;
        if ((plane.classifications & PlaneClassifications.Seat) != 0)
            return SurfaceKind.Seat;
        if ((plane.classifications & PlaneClassifications.DoorFrame) != 0)
            return SurfaceKind.Door;
        if ((plane.classifications & PlaneClassifications.WindowFrame) != 0)
            return SurfaceKind.Window;

        return plane.alignment == PlaneAlignment.HorizontalUp ? SurfaceKind.Floor :
            plane.alignment == PlaneAlignment.Vertical ? SurfaceKind.Wall :
            SurfaceKind.Unknown;
    }

#if ERUPT_USE_META_XR && UNITY_ANDROID && !UNITY_VISIONOS
    void StartMetaMRUK()
    {
        if (MRUK.Instance == null)
        {
            Debug.LogError("[SceneAnchorCollisionBridge] MRUK.Instance is null. Add MRUK to the Quest scene or use ARFoundationPlanes.");
            return;
        }

        MRUK.Instance.SceneLoadedEvent.AddListener(OnMetaSceneLoaded);
        MRUK.Instance.RoomCreatedEvent.AddListener(OnMetaRoomCreated);

        var rooms = MRUK.Instance.GetRooms();
        if (rooms != null)
        {
            foreach (var room in rooms)
                ProcessMetaRoom(room);
        }
    }

    void StopMetaMRUK()
    {
        if (MRUK.Instance == null)
            return;

        MRUK.Instance.SceneLoadedEvent.RemoveListener(OnMetaSceneLoaded);
        MRUK.Instance.RoomCreatedEvent.RemoveListener(OnMetaRoomCreated);
    }

    void OnMetaSceneLoaded()
    {
        var rooms = MRUK.Instance?.GetRooms();
        if (rooms == null)
            return;

        foreach (var room in rooms)
            ProcessMetaRoom(room);
    }

    void OnMetaRoomCreated(MRUKRoom room)
    {
        ProcessMetaRoom(room);
    }

    void ProcessMetaRoom(MRUKRoom room)
    {
        foreach (var anchor in room.Anchors)
            ProcessMetaAnchor(anchor);
    }

    void ProcessMetaAnchor(MRUKAnchor anchor)
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
        }
        else if (anchor.PlaneRect.HasValue)
        {
            Rect r = anchor.PlaneRect.Value;
            localCenter = new Vector3(r.center.x, r.center.y, 0f);
            size = new Vector3(r.width, r.height, planeDepth);
            isPlane = true;
        }
        else
        {
            return;
        }

        var kind = SurfaceKindForMetaAnchor(anchor);
        var center = anchor.transform.TransformPoint(localCenter);
        var rotation = anchor.transform.rotation;

        UpsertAnchor(new AnchorData
        {
            id = $"scene_mruk_{anchor.Label}_{anchor.GetInstanceID()}",
            label = anchor.Label.ToString(),
            kind = kind,
            worldCenter = center,
            worldRotation = rotation,
            worldNormal = anchor.transform.forward,
            size = size,
            isPlane = isPlane
        });
    }

    static SurfaceKind SurfaceKindForMetaAnchor(MRUKAnchor anchor)
    {
        var label = anchor.Label;
        if ((label & MRUKAnchor.SceneLabels.FLOOR) != 0) return SurfaceKind.Floor;
        if ((label & MRUKAnchor.SceneLabels.CEILING) != 0) return SurfaceKind.Ceiling;
        if ((label & MRUKAnchor.SceneLabels.WALL_FACE) != 0) return SurfaceKind.Wall;
        if ((label & MRUKAnchor.SceneLabels.TABLE) != 0) return SurfaceKind.Table;
        if ((label & MRUKAnchor.SceneLabels.COUCH) != 0) return SurfaceKind.Seat;
        if ((label & MRUKAnchor.SceneLabels.STORAGE) != 0) return SurfaceKind.Storage;
        if ((label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) return SurfaceKind.Door;
        if ((label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) return SurfaceKind.Window;
        return anchor.VolumeBounds.HasValue ? SurfaceKind.Volume : SurfaceKind.Unknown;
    }
#else
    void StartMetaMRUK()
    {
        Debug.LogWarning("[SceneAnchorCollisionBridge] MetaMRUK provider is unavailable in this build. Use ARFoundationPlanes or add ERUPT_USE_META_XR to an Android Quest build.");
    }

    void StopMetaMRUK()
    {
    }
#endif

    void UpsertAnchor(AnchorData data)
    {
        if (data.kind == SurfaceKind.Floor)
        {
            _hasFloor = true;
            _floorPosition = data.worldCenter;
            _floorNormal = data.worldNormal == Vector3.zero ? Vector3.up : data.worldNormal.normalized;
        }

        if (_anchorDataById.TryGetValue(data.id, out var existing))
        {
            if (existing.visual != null)
            {
                existing.visual.transform.SetPositionAndRotation(data.worldCenter, data.worldRotation);
                existing.visual.transform.localScale = data.size;
                data.visual = existing.visual;
            }

            _anchorDataById[data.id] = data;
            if ((!data.isPlane || publishPlanes) && _publishedIds.Contains(data.id))
                PublishBox(data);
            return;
        }

        data.visual = SpawnVisual(data);
        _anchorDataById[data.id] = data;

        if (!data.isPlane || publishPlanes)
            PublishBox(data);
    }

    GameObject SpawnVisual(AnchorData data)
    {
        var go = new GameObject($"Anchor_{data.label}");
        go.transform.SetParent(transform, worldPositionStays: true);
        go.transform.SetPositionAndRotation(data.worldCenter, data.worldRotation);
        go.transform.localScale = data.size;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = _cubeMesh;
        mr.sharedMaterial = BuildTransparentMaterial(LabelColor(data.kind, data.isPlane ? visualAlpha : volumeAlpha), doubleSided: false);
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        if (!data.isPlane)
            go.AddComponent<BoxCollider>();

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

    void PublishBox(AnchorData data, sbyte operation = 0)
    {
        Vector3 publishPos = worldOrigin != null
            ? worldOrigin.transform.InverseTransformPoint(data.worldCenter)
            : data.worldCenter;
        Quaternion publishRot = worldOrigin != null
            ? Quaternion.Inverse(worldOrigin.transform.rotation) * data.worldRotation
            : data.worldRotation;

        var msg = new CollisionObjectMsg
        {
            id = data.id,
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
                    dimensions = new double[] { data.size.x, data.size.z, data.size.y }
                }
            }
        };

        _ros.Publish("/collision_object", msg);
        if (!_publishedIds.Contains(data.id))
            _publishedIds.Add(data.id);

        if (collisionObjectsListener != null && data.visual != null && !collisionObjectsListener.objectsById.ContainsKey(data.id))
            collisionObjectsListener.objectsById[data.id] = data.visual;

        Debug.Log($"[SceneAnchorCollisionBridge] Published {data.label} as '{data.id}', size={data.size}");
    }

    void RemovePublishedAnchor(string id)
    {
        if (!_anchorDataById.TryGetValue(id, out var data))
            return;

        if (_publishedIds.Remove(id))
        {
            _ros.Publish("/collision_object", new CollisionObjectMsg
            {
                id = id,
                header = new HeaderMsg { frame_id = frameId, stamp = RosTimestamp() },
                operation = CollisionObjectMsg.REMOVE
            });
        }

        collisionObjectsListener?.objectsById.Remove(id);
        if (data.visual != null)
            Destroy(data.visual);
        _anchorDataById.Remove(id);
    }

    public void RepublishAll()
    {
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

        foreach (var data in _anchorDataById.Values)
        {
            if (data.isPlane && !publishPlanes) continue;
            PublishBox(data);
        }
    }

    static Color LabelColor(SurfaceKind kind, float alpha)
    {
        Color c = kind switch
        {
            SurfaceKind.Floor => Color.green,
            SurfaceKind.Ceiling => Color.red,
            SurfaceKind.Wall => Color.white,
            SurfaceKind.Table => Color.yellow,
            SurfaceKind.Seat => Color.blue,
            SurfaceKind.Storage => new Color(1f, 0.5f, 0f),
            SurfaceKind.Door => Color.magenta,
            SurfaceKind.Window => Color.cyan,
            _ => Color.grey,
        };
        c.a = alpha;
        return c;
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
            mat.SetInt("_Cull", doubleSided ? 0 : 2);
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
        return RosMessageCompatibility.CreateTime(
            ticks / TimeSpan.TicksPerSecond,
            (uint)((ticks % TimeSpan.TicksPerSecond) * 100L));
    }
}
