using System;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Renders the robot's reachable workspace from a <see cref="ReachabilityMap"/> as (a) a translucent
/// outer shell of everything reachable plus an inner shell of the high-dexterity core and (b) a
/// horizontal height-slice heatmap coloured by dexterity. Everything is parented under the robot's
/// ROS base anchor (RobotReachProfile.baseAnchor, else a child named BaseTransform, else the root
/// yawed by the profile's offset) so it follows the AprilTag-placed robot.
/// The map comes from the baked TextAsset on the robot's <see cref="RobotReachProfile"/> and is
/// replaced by any map received on <see cref="topic"/>; because latched topics do not survive the
/// ROS-TCP-Endpoint relay, a request is published on <see cref="requestTopic"/> until one arrives.
/// </summary>
public class ReachabilityVolumeVisualizer : MonoBehaviour
{
    private const string LogTag = "[ReachMap]";
    private const string BaseTransformName = "BaseTransform";
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    [Header("Robot")]
    [SerializeField, Tooltip("Root of the robot prefab instance. Its RobotReachProfile supplies the baked map, the " +
                             "base anchor (or a child named BaseTransform) and the default slice height.")]
    private GameObject robot;

    [Header("Data")]
    [SerializeField, Tooltip("std_msgs/UInt8MultiArray topic carrying an RMAP payload; a received map replaces the baked one.")]
    private string topic = "/reachability_map";

    [SerializeField, Tooltip("std_msgs/Empty topic the map server answers with a (re)publish. Needed because the " +
                             "endpoint relay drops latched samples published before Unity connected.")]
    private string requestTopic = "/reachability_map/request";

    [SerializeField, Tooltip("Seconds between map requests while no topic map has been received.")]
    private float requestRetrySeconds = 5f;

    [SerializeField, Tooltip("Load the baked map from the robot's RobotReachProfile at start.")]
    private bool useBakedMap = true;

    [SerializeField, Tooltip("Subscribe to the topic and let a received map override the baked one.")]
    private bool useRosMap = true;

    [Header("Shell")]
    [SerializeField, Tooltip("Transparent URP/Lit material (serialized asset so the variant survives Android stripping). " +
                             "Colour is applied per renderer through a MaterialPropertyBlock.")]
    private Material shellMaterial;

    [SerializeField] private Color outerColor = new Color(0.1f, 0.9f, 0.2f, 0.18f);

    [SerializeField, Tooltip("Also draw a shell around the region where most sampled tool directions are solvable.")]
    private bool showInnerShell = true;

    [SerializeField] private Color innerColor = new Color(0.1f, 0.9f, 0.2f, 0.4f);

    [SerializeField, Range(0.05f, 0.95f), Tooltip("Dexterity iso-level of the inner shell.")]
    private float innerIso = 0.5f;

    [SerializeField, Tooltip("One 3x3x3 box-filter pass before extraction, softening voxel stair-steps.")]
    private bool smoothField = true;

    [SerializeField, Range(0f, 1f), Tooltip("Shell alpha multiplier while the head is inside the reachable volume.")]
    private float insideAlphaFactor = 0.3f;

    [SerializeField, Tooltip("Headset transform for the inside-the-volume fade. Empty = Camera.main.")]
    private Transform headTransform;

    [Header("Slice")]
    [SerializeField, Tooltip("Transparent URP/Unlit material with a texture slot (serialized asset). The slice texture " +
                             "is bound per renderer through a MaterialPropertyBlock.")]
    private Material sliceMaterial;

    [SerializeField, Tooltip("Use the gradient below instead of the built-in amber-to-green ramp.")]
    private bool customSliceRamp = false;

    [SerializeField, Tooltip("Dexterity (0..1) to colour. Alpha 0 hides unreachable cells.")]
    private Gradient sliceRamp;

    [Header("Initial state")]
    [SerializeField] private bool shellVisibleOnStart = true;
    [SerializeField] private bool sliceVisibleOnStart = false;
    [SerializeField] private bool topDownOnlyOnStart = false;

    [Header("Debug")]
    [SerializeField, Tooltip("ROS point in the planning frame for the 'Log dexterity at debug point' context menu.")]
    private Vector3 debugPointRos = new Vector3(0.5f, 0f, 0.3f);

    [SerializeField] private bool verboseLogging = false;

    // ---- state -----------------------------------------------------------------------------

    private ReachabilityMap _map;
    private string _mapSource = "none";
    private RobotReachProfile _profile;
    private Transform _anchor;
    private Transform _root;
    private MeshRenderer _outerRenderer, _innerRenderer, _sliceRenderer;
    private Mesh _outerMesh, _innerMesh, _sliceMesh;
    private Texture2D _sliceTexture;
    private Color32[] _slicePixels;
    private Color32[] _lut;
    private float[] _field, _scratch;
    private readonly List<Vector3> _verts = new List<Vector3>();
    private readonly List<int> _tris = new List<int>();
    private readonly Dictionary<long, int> _edgeCache = new Dictionary<long, int>();
    private MaterialPropertyBlock _pb;

    private bool _shellVisible, _sliceVisible, _topDownOnly;
    private float _sliceHeight;
    private float _shellAlphaScale = 1f;
    private bool _innerShellActive;

    private ROSConnection _ros;
    private bool _subscribed;
    private bool _gotTopicMap;
    private float _lastRequestTime = float.NegativeInfinity;
    private bool _loggedRequest;
    private ulong _lastPayloadHash;

    // ---- public API --------------------------------------------------------------------------

    public ReachabilityMap Map => _map;
    public bool HasMap => _map != null;
    public string MapSource => _mapSource;
    public event Action MapChanged;

    public bool ShellVisible => _shellVisible;
    public bool SliceVisible => _sliceVisible;
    public bool TopDownOnly => _topDownOnly;
    /// <summary>Slice height in metres above the planning frame (ROS z).</summary>
    public float SliceHeight => _sliceHeight;
    public float SliceMinHeight => _map != null ? _map.ZMinRos : -0.5f;
    public float SliceMaxHeight => _map != null ? _map.ZMaxRos : 1.2f;

    public void SetVisible(bool on)
    {
        SetShellVisible(on);
        SetSliceVisible(on);
    }

    public void SetShellVisible(bool on)
    {
        _shellVisible = on;
        ApplyVisibility();
    }

    public void SetSliceVisible(bool on)
    {
        _sliceVisible = on;
        ApplyVisibility();
    }

    public void SetSliceHeight(float metresAboveBase)
    {
        float clamped = Mathf.Clamp(metresAboveBase, SliceMinHeight, SliceMaxHeight);
        if (Mathf.Approximately(clamped, _sliceHeight))
            return;
        _sliceHeight = clamped;
        RebuildSlice();
    }

    public void SetOrientationFilter(bool topDownOnly)
    {
        if (_topDownOnly == topDownOnly)
            return;
        _topDownOnly = topDownOnly;
        Rebuild();
    }

    /// <summary>Dexterity (current orientation filter) at a Unity world point; 0 without a map.</summary>
    public float SampleDexterity(Vector3 worldPoint)
    {
        if (_map == null || _anchor == null)
            return 0f;
        return _map.SampleDexterity(WorldToRos(worldPoint), CurrentFilter);
    }

    // ---- lifecycle --------------------------------------------------------------------------

    private uint CurrentFilter => _map == null ? 0u : (_topDownOnly ? _map.DownMask : _map.AllMask);

    private void Awake()
    {
        if (robot == null)
            Debug.LogError($"{LogTag} 'robot' is not assigned; nothing will be shown.");
        else
            _profile = robot.GetComponentInChildren<RobotReachProfile>(true);
        if (shellMaterial == null)
            Debug.LogWarning($"{LogTag} 'shellMaterial' is not assigned; shells will use the default material (opaque, may be stripped in builds).");
        if (sliceMaterial == null)
            Debug.LogWarning($"{LogTag} 'sliceMaterial' is not assigned; the slice will use the default material.");
        if (headTransform == null && Camera.main != null)
            headTransform = Camera.main.transform;

        _shellVisible = shellVisibleOnStart;
        _sliceVisible = sliceVisibleOnStart;
        _topDownOnly = topDownOnlyOnStart;
        _sliceHeight = _profile != null ? _profile.defaultSliceHeight : 0.3f;
        _pb = new MaterialPropertyBlock();
        BuildLut();

        _anchor = ResolveAnchor();
        if (_anchor != null)
            BuildSceneObjects();
    }

    private void Start()
    {
        if (useBakedMap)
            LoadBakedMap();

        if (!useRosMap)
            return;
        _ros = ROSConnection.GetOrCreateInstance();
        try
        {
            _ros.Subscribe<UInt8MultiArrayMsg>(topic, OnMapMessage);
            _ros.RegisterPublisher<EmptyMsg>(requestTopic);
            _subscribed = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{LogTag} could not subscribe to {topic}: {e.Message}");
        }
    }

    private void Update()
    {
        if (_subscribed && !_gotTopicMap && _ros != null && _ros.HasConnectionThread && !_ros.HasConnectionError &&
            Time.time - _lastRequestTime >= requestRetrySeconds)
        {
            _lastRequestTime = Time.time;
            try
            {
                _ros.Publish(requestTopic, new EmptyMsg());
                if (!_loggedRequest || verboseLogging)
                {
                    _loggedRequest = true;
                    Debug.Log($"{LogTag} requesting map on {requestTopic}");
                }
            }
            catch (Exception e)
            {
                if (verboseLogging)
                    Debug.LogWarning($"{LogTag} request publish failed: {e.Message}");
            }
        }

        UpdateInsideFade();
    }

    private void OnDestroy()
    {
        if (_subscribed && _ros != null)
        {
            try { _ros.Unsubscribe(topic); } catch (Exception) { /* connector may already be gone */ }
            _subscribed = false;
        }
        if (_outerMesh != null) Destroy(_outerMesh);
        if (_innerMesh != null) Destroy(_innerMesh);
        if (_sliceMesh != null) Destroy(_sliceMesh);
        if (_sliceTexture != null) Destroy(_sliceTexture);
        if (_root != null) Destroy(_root.gameObject);
    }

    // ---- anchor and scene objects ----------------------------------------------------------

    /// <summary>Unity transform of the ROS planning frame, same preference order as TagReachabilityIndicator.</summary>
    private Transform ResolveAnchor()
    {
        if (robot == null)
            return null;
        if (_profile != null && _profile.baseAnchor != null)
            return _profile.baseAnchor;
        foreach (Transform t in robot.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == BaseTransformName)
                return t;
        }
        float yaw = _profile != null ? _profile.baseYawOffsetDegrees : -90f;
        var created = new GameObject("ReachabilityAnchor").transform;
        created.SetParent(robot.transform, false);
        created.localPosition = Vector3.zero;
        created.localRotation = Quaternion.Euler(0f, yaw, 0f);
        created.localScale = Vector3.one;
        if (verboseLogging)
            Debug.Log($"{LogTag} no BaseTransform under '{robot.name}'; created ReachabilityAnchor yawed {yaw} deg");
        return created;
    }

    private void BuildSceneObjects()
    {
        _root = new GameObject("ReachabilityVolume").transform;
        _root.SetParent(_anchor, false);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;
        _root.localScale = Vector3.one;

        _outerRenderer = CreateMeshObject("OuterShell", shellMaterial, out _outerMesh);
        _innerRenderer = CreateMeshObject("InnerShell", shellMaterial, out _innerMesh);
        _sliceRenderer = CreateMeshObject("Slice", sliceMaterial, out _sliceMesh);
        SetColor(_outerRenderer, outerColor);
        SetColor(_innerRenderer, innerColor);
        ApplyVisibility();
    }

    private MeshRenderer CreateMeshObject(string name, Material material, out Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        if (material != null)
            renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
        return renderer;
    }

    private void SetColor(Renderer renderer, Color color)
    {
        if (renderer == null)
            return;
        renderer.GetPropertyBlock(_pb);
        _pb.SetColor(BaseColorId, color);
        _pb.SetColor(ColorId, color);
        renderer.SetPropertyBlock(_pb);
    }

    private void ApplyVisibility()
    {
        bool haveMap = _map != null;
        if (_outerRenderer != null) _outerRenderer.enabled = haveMap && _shellVisible;
        if (_innerRenderer != null) _innerRenderer.enabled = haveMap && _shellVisible && _innerShellActive;
        if (_sliceRenderer != null) _sliceRenderer.enabled = haveMap && _sliceVisible;
    }

    private void UpdateInsideFade()
    {
        if (_map == null || !_shellVisible || headTransform == null || _anchor == null)
            return;
        bool inside = _map.SampleDexterity(WorldToRos(headTransform.position), CurrentFilter) > 0f;
        float target = inside ? insideAlphaFactor : 1f;
        float next = Mathf.MoveTowards(_shellAlphaScale, target, Time.deltaTime * 3f);
        if (Mathf.Approximately(next, _shellAlphaScale))
            return;
        _shellAlphaScale = next;
        SetColor(_outerRenderer, Scaled(outerColor, _shellAlphaScale));
        SetColor(_innerRenderer, Scaled(innerColor, _shellAlphaScale));
    }

    private static Color Scaled(Color c, float alphaScale) => new Color(c.r, c.g, c.b, c.a * alphaScale);

    // ---- frames ---------------------------------------------------------------------------------

    /// <summary>Anchor-local Unity vector to ROS axes; the (x, z, y) swap from Conversions.cs.</summary>
    private static Vector3 ToRos(Vector3 unityLocal) => new Vector3(unityLocal.x, unityLocal.z, unityLocal.y);

    private static Vector3 ToUnityLocal(Vector3 ros) => new Vector3(ros.x, ros.z, ros.y);

    private Vector3 WorldToRos(Vector3 world) => ToRos(_anchor.InverseTransformPoint(world));

    /// <summary>Padded grid index (px, py, pz) -> anchor-local Unity position.</summary>
    private Matrix4x4 GridToLocal()
    {
        float r = _map.Resolution;
        Vector3 o = _map.OriginRos;
        // ros = origin + (p - 1) * r ; unity = (ros.x, ros.z, ros.y)
        var m = Matrix4x4.zero;
        m.SetRow(0, new Vector4(r, 0f, 0f, o.x - r));
        m.SetRow(1, new Vector4(0f, 0f, r, o.z - r));
        m.SetRow(2, new Vector4(0f, r, 0f, o.y - r));
        m.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
        return m;
    }

    // ---- map loading -----------------------------------------------------------------------

    private void LoadBakedMap()
    {
        TextAsset asset = _profile != null ? _profile.reachabilityMap : null;
        string source = asset != null ? $"asset '{asset.name}'" : null;
        if (asset == null && _profile != null && !string.IsNullOrEmpty(_profile.reachabilityMapResource))
        {
            asset = Resources.Load<TextAsset>("ReachabilityMaps/" + _profile.reachabilityMapResource);
            source = $"Resources/ReachabilityMaps/{_profile.reachabilityMapResource}";
        }
        if (asset == null)
        {
            Debug.LogWarning($"{LogTag} no baked map on the robot's RobotReachProfile; waiting for {topic}.");
            return;
        }
        if (!ReachabilityMap.TryParse(asset.bytes, out var map, out string error))
        {
            Debug.LogError($"{LogTag} baked map {source} is invalid: {error}");
            return;
        }
        ApplyMap(map, "baked " + source);
    }

    private void OnMapMessage(UInt8MultiArrayMsg msg)
    {
        byte[] data = msg?.data;
        if (data == null || data.Length == 0)
            return;
        ulong hash = Fnv1a(data);
        if (hash == _lastPayloadHash && _gotTopicMap)
            return;
        if (!ReachabilityMap.TryParse(data, out var map, out string error))
        {
            Debug.LogWarning($"{LogTag} ignoring invalid map on {topic}: {error}");
            return;
        }
        _lastPayloadHash = hash;
        _gotTopicMap = true;
        ApplyMap(map, $"topic {topic} ({data.Length} bytes)");
    }

    private void ApplyMap(ReachabilityMap map, string source)
    {
        _map = map;
        _mapSource = source;
        _sliceHeight = Mathf.Clamp(_sliceHeight, map.ZMinRos, map.ZMaxRos);
        Debug.Log($"{LogTag} map loaded from {source}: {map.Describe()}");
        if (_anchor != null)
            Rebuild();
        MapChanged?.Invoke();
    }

    private static ulong Fnv1a(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < data.Length; i++)
            h = (h ^ data[i]) * 1099511628211UL;
        return h;
    }

    // ---- geometry -----------------------------------------------------------------------------

    private void Rebuild()
    {
        if (_map == null || _root == null)
            return;

        int len = _map.PaddedLength;
        if (_field == null || _field.Length < len)
        {
            _field = new float[len];
            _scratch = new float[len];
        }
        uint filter = CurrentFilter;
        Matrix4x4 gridToLocal = GridToLocal();
        float t0 = Time.realtimeSinceStartup;

        _map.FillScalarField(_field, filter, binary: true);
        if (smoothField)
        {
            ReachabilityMap.BoxSmooth(_field, _scratch, _map.PaddedNx, _map.PaddedNy, _map.PaddedNz);
            // Blurring erodes thin tips and convex caps (the iso-surface pulls inside the outermost
            // reachable voxels). Keep every reachable voxel centre inside the shell: the surface
            // then follows the smooth blurred falloff on the outside only. _scratch still holds the
            // unsmoothed binary field after BoxSmooth.
            for (int i = 0; i < len; i++)
            {
                if (_scratch[i] > 0f && _field[i] < 0.75f)
                    _field[i] = 0.75f;
            }
        }
        ExtractInto(_outerMesh, 0.5f, gridToLocal);

        _innerShellActive = showInnerShell && ReachabilityMap.PopCount(filter) > 1;
        if (_innerShellActive)
        {
            _map.FillScalarField(_field, filter, binary: false);
            if (smoothField)
                ReachabilityMap.BoxSmooth(_field, _scratch, _map.PaddedNx, _map.PaddedNy, _map.PaddedNz);
            ExtractInto(_innerMesh, innerIso, gridToLocal);
            if (_innerMesh.vertexCount == 0)
                _innerShellActive = false;
        }
        else
        {
            _innerMesh.Clear();
        }

        RebuildSlice();
        ApplyVisibility();
        if (verboseLogging)
            Debug.Log($"{LogTag} rebuilt (filter {(_topDownOnly ? "top-down" : "any")}): outer {_outerMesh.triangles.Length / 3} tris, " +
                      $"inner {(_innerShellActive ? _innerMesh.triangles.Length / 3 : 0)} tris in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms");
    }

    private void ExtractInto(Mesh mesh, float iso, Matrix4x4 gridToLocal)
    {
        MarchingTetrahedra.Polygonize(_field, _map.PaddedNx, _map.PaddedNy, _map.PaddedNz, iso, gridToLocal,
                                      _verts, _tris, _edgeCache);
        mesh.Clear();
        if (_verts.Count == 0)
            return;
        mesh.SetVertices(_verts);
        mesh.SetTriangles(_tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private void RebuildSlice()
    {
        if (_map == null || _sliceRenderer == null)
            return;
        int nx = _map.Nx, ny = _map.Ny;
        if (_sliceTexture == null || _sliceTexture.width != nx || _sliceTexture.height != ny)
        {
            if (_sliceTexture != null)
                Destroy(_sliceTexture);
            _sliceTexture = new Texture2D(nx, ny, TextureFormat.RGBA32, false)
            {
                name = "ReachabilitySlice",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _slicePixels = new Color32[nx * ny];
            _sliceRenderer.GetPropertyBlock(_pb);
            _pb.SetTexture(BaseMapId, _sliceTexture);
            _pb.SetTexture(MainTexId, _sliceTexture);
            _pb.SetColor(BaseColorId, Color.white);
            _pb.SetColor(ColorId, Color.white);
            _sliceRenderer.SetPropertyBlock(_pb);
        }

        uint filter = CurrentFilter;
        for (int iy = 0; iy < ny; iy++)
        {
            int row = iy * nx;
            for (int ix = 0; ix < nx; ix++)
            {
                float d = _map.DexterityAtHeight(ix, iy, _sliceHeight, filter);
                _slicePixels[row + ix] = _lut[Mathf.Clamp(Mathf.RoundToInt(d * 255f), 0, 255)];
            }
        }
        _sliceTexture.SetPixels32(_slicePixels);
        _sliceTexture.Apply(false);

        // Quad spanning half a voxel beyond the outermost voxel centres so texel centres coincide
        // with voxel centres. ROS x -> Unity x, ROS y -> Unity z, height -> Unity y.
        float r = _map.Resolution;
        Vector3 o = _map.OriginRos;
        float x0 = o.x - 0.5f * r, x1 = o.x + (nx - 0.5f) * r;
        float y0 = o.y - 0.5f * r, y1 = o.y + (ny - 0.5f) * r;
        float h = _sliceHeight;
        var verts = new[]
        {
            ToUnityLocal(new Vector3(x0, y0, h)),
            ToUnityLocal(new Vector3(x0, y1, h)),
            ToUnityLocal(new Vector3(x1, y1, h)),
            ToUnityLocal(new Vector3(x1, y0, h)),
        };
        var uvs = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
        _sliceMesh.Clear();
        _sliceMesh.vertices = verts;
        _sliceMesh.uv = uvs;
        _sliceMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };          // front face +y (Unity up)
        _sliceMesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        _sliceMesh.RecalculateBounds();
    }

    private void BuildLut()
    {
        Gradient g = sliceRamp;
        if (!customSliceRamp || g == null || g.colorKeys == null || g.colorKeys.Length == 0)
        {
            g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.0f, 0.65f, 0.1f), 0f),     // amber: marginal
                    new GradientColorKey(new Color(0.75f, 0.85f, 0.15f), 0.5f),
                    new GradientColorKey(new Color(0.1f, 0.9f, 0.2f), 1f),      // green: dexterous (matches the tag indicator)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),                               // unreachable: invisible
                    new GradientAlphaKey(0.5f, 0.08f),
                    new GradientAlphaKey(0.6f, 1f),
                });
        }
        _lut = new Color32[256];
        for (int i = 0; i < 256; i++)
            _lut[i] = g.Evaluate(i / 255f);
    }

    // ---- debug helpers ------------------------------------------------------------------------

    [ContextMenu("Rebuild now")]
    private void RebuildNow() => Rebuild();

    [ContextMenu("Log map summary")]
    private void LogMapSummary()
    {
        Debug.Log(_map == null ? $"{LogTag} no map loaded" : $"{LogTag} source={_mapSource} {_map.Describe()} sliceHeight={_sliceHeight:F2}");
    }

    [ContextMenu("Log dexterity at debug point")]
    private void LogDexterityAtDebugPoint()
    {
        if (_map == null)
        {
            Debug.Log($"{LogTag} no map loaded");
            return;
        }
        float any = _map.SampleDexterity(debugPointRos, _map.AllMask);
        float down = _map.SampleDexterity(debugPointRos, _map.DownMask);
        bool nearest = _map.IsReachableNearest(debugPointRos, _map.AllMask);
        Debug.Log($"{LogTag} ROS ({debugPointRos.x:F2}, {debugPointRos.y:F2}, {debugPointRos.z:F2}): dexterity any={any:F2} top-down={down:F2} nearest voxel {(nearest ? "reachable" : "unreachable")}");
    }
}
