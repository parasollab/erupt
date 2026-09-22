using System;
using System.Collections.Generic;
using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Tracks printed AprilTags (tagStandard41h12 family) seen by the Quest passthrough camera.
/// The primary tag places <see cref="_arObject"/> (the marker MarkerRobotPlacement reads);
/// any number of additional tags with their own IDs and sizes are tracked alongside it from the
/// same detection pass and reported through <see cref="OnTagObserved"/> / <see cref="TryGetTag"/>.
/// Replaces the OpenCV-for-Unity ChArUco tracker that used to live on this GameObject: frames
/// come from Meta's <see cref="PassthroughCameraAccess"/> through an async GPU readback,
/// detection and pose estimation run in jp.keijiro.apriltag (the courtneymcbeth fork, whose
/// full-intrinsics ProcessImage overload is required because the passthrough camera's principal
/// point is not at the image centre). Detection logic is ported from BARD's AprilTagLocator.
/// </summary>
public class AprilTagTracker : MonoBehaviour
{
    [Serializable]
    public class AdditionalTagEntry
    {
        [Tooltip("tagStandard41h12 ID (0-2114). Must differ from the primary tag ID and from the other entries.")]
        public int id = 1;

        [Tooltip("Detection-square size in metres: the 5 central cells of the 9-cell printed pattern, NOT the " +
                 "full printed square. 0.05 for the object tags from Docs/make_apriltag_svg.py --size 0.05.")]
        public float tagSize = 0.05f;

        [Tooltip("Optional transform laid on the tag with the same tagToObjectEuler as the primary tag. Leave " +
                 "empty for tags that are only consumed through OnTagObserved / TryGetTag.")]
        public Transform target;
    }

    /// <summary>Latest filtered observation of one tag.</summary>
    public struct TrackedTag
    {
        public int Id;
        /// <summary>Detection-square size in metres that was configured for this tag.</summary>
        public float Size;
        /// <summary>World-space tag pose (tag frame: x right, y up in the tag plane, z into the tag).</summary>
        public Pose FilteredPose;
        public float LastSeenTime;
        public long DetectionCount;
    }

    [Header("Passthrough Camera")]
    [SerializeField] private PassthroughCameraAccess m_passthroughCameraAccess;

    [Header("Tag")]
    [SerializeField, Tooltip("GameObject placed on the detected tag (the marker indicator MarkerRobotPlacement reads).")]
    private GameObject _arObject;

    [SerializeField, Tooltip("Only accept this tag ID as the primary tag. -1 accepts the first detected tag that is not " +
                             "listed under Additional Tags.")]
    private int tagId = 0;

    [SerializeField, Tooltip("Detection-square size in metres. For tagStandard41h12 this is the 5 central cells " +
                             "of the 9-cell printed pattern, NOT the full printed square. Docs/tag41_12_id0.svg " +
                             "is generated to match 0.10 m when printed at 100% scale.")]
    private float tagSize = 0.10f;

    [SerializeField, Tooltip("Rotation from the tag frame (x = right, y = up in the tag plane, z = into the tag) " +
                             "to the AR object. The default lays the object flat on the tag with its up = tag " +
                             "normal and its right = tag x (MarkerRobotPlacement uses right as the robot's forward).")]
    private Vector3 tagToObjectEuler = new Vector3(-90f, 0f, 0f);

    [Header("Additional Tags")]
    [SerializeField, Tooltip("Other tags to track from the same detection pass (e.g. small tags on objects for the " +
                             "reachability indicators). Detection runs once at the primary tag size; each entry's " +
                             "pose is rescaled to its own size, which is exact because the estimated translation " +
                             "is proportional to the assumed tag size and the rotation does not depend on it.")]
    private List<AdditionalTagEntry> additionalTags = new List<AdditionalTagEntry>();

    [Header("Detection")]
    [SerializeField, Tooltip("Detections per second. Detection is expensive and placement is not time-critical.")]
    private float detectionRate = 4f;

    [SerializeField, Tooltip("AprilTag quad decimation. Higher = faster but shorter detection range.")]
    private int decimation = 2;

    [Range(0, 1)]
    [SerializeField, Tooltip("Low-pass filter coefficient for the tag pose (0 = raw, 1 = frozen). " +
                             "Higher values mean more smoothing, as with the old ChArUco tracker.")]
    private float poseFilterCoefficient = 0.5f;

    [SerializeField, Tooltip("Reverse image rows before detection. The detector expects Unity's bottom-up row " +
                             "order. Leave off unless the debug renderer shows the frame upside down; a " +
                             "vertically mirrored tag never decodes, so the symptom is zero detections.")]
    private bool flipImageVertically = false;

    [Header("Debug")]
    [SerializeField, Tooltip("Optional renderer whose material shows the frame exactly as the detector sees it.")]
    private MeshRenderer m_debugRenderer;

    private TagDetector _detector;
    private int _detectorWidth;
    private int _detectorHeight;
    private Color32[] _pixels;
    private bool _readbackPending;
    private Pose _pendingCameraPose;
    private float _nextDetection;
    private Texture2D _debugTexture;
    private bool _warnedNoReadback;
    private readonly Dictionary<int, AdditionalTagEntry> _entriesById = new Dictionary<int, AdditionalTagEntry>();
    private readonly Dictionary<int, TrackedTag> _tags = new Dictionary<int, TrackedTag>();

    /// <summary>True once the passthrough camera is delivering frames.</summary>
    public bool IsReady => m_passthroughCameraAccess != null && m_passthroughCameraAccess.IsPlaying;

    /// <summary>True once the primary tag has been detected at least once.</summary>
    public bool HasPose { get; private set; }

    /// <summary>Filtered world-space primary tag pose (tag frame: x right, y up in the tag plane, z into the tag).</summary>
    public Pose FilteredTagPose { get; private set; }

    public float LastDetectionTime { get; private set; } = -1f;
    public long DetectionCount { get; private set; }

    /// <summary>ID of the primary (robot) tag, or -1 when any unlisted tag is accepted.</summary>
    public int PrimaryTagId => tagId;

    /// <summary>Raised on the main thread for every tag (primary and additional) each time it is observed.</summary>
    public event Action<TrackedTag> OnTagObserved;

    /// <summary>All tags observed so far, keyed by ID (including the primary tag).</summary>
    public IReadOnlyDictionary<int, TrackedTag> Tags => _tags;

    public bool TryGetTag(int id, out TrackedTag tag) => _tags.TryGetValue(id, out tag);

    public string StatusText
    {
        get
        {
            if (m_passthroughCameraAccess == null) return "no PassthroughCameraAccess assigned";
            if (!m_passthroughCameraAccess.IsPlaying) return "waiting for passthrough camera";
            if (DetectionCount == 0 && _tags.Count == 0) return "camera running | no tag seen yet";
            string primary;
            if (DetectionCount == 0)
            {
                primary = "primary tag not seen yet";
            }
            else
            {
                float age = Time.time - LastDetectionTime;
                primary = age < 2f
                    ? $"tag {tagId} tracked ({DetectionCount} detections)"
                    : $"tag lost {age:F0}s ago";
            }
            if (additionalTags.Count == 0)
                return primary;

            int objectTags = 0;
            foreach (var kv in _tags)
            {
                if (_entriesById.ContainsKey(kv.Key) && Time.time - kv.Value.LastSeenTime < 2f)
                    objectTags++;
            }
            return $"{primary} | {objectTags}/{additionalTags.Count} object tags visible";
        }
    }

    private void Awake()
    {
        _entriesById.Clear();
        foreach (var entry in additionalTags)
        {
            if (entry == null) continue;
            if (entry.id == tagId)
            {
                Debug.LogError($"[AprilTagTracker] additionalTags: id {entry.id} is the primary tag ID; entry ignored.");
                continue;
            }
            if (entry.tagSize <= 0f)
            {
                Debug.LogError($"[AprilTagTracker] additionalTags: id {entry.id} has tagSize {entry.tagSize}; entry ignored.");
                continue;
            }
            if (_entriesById.ContainsKey(entry.id))
            {
                Debug.LogError($"[AprilTagTracker] additionalTags: duplicate id {entry.id}; later entry ignored.");
                continue;
            }
            _entriesById.Add(entry.id, entry);
        }
    }

    private void Update()
    {
        if (!IsReady || _readbackPending || Time.time < _nextDetection)
            return;

        var texture = m_passthroughCameraAccess.GetTexture();
        if (texture == null)
            return;

        _nextDetection = Time.time + 1f / Mathf.Max(detectionRate, 0.01f);

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            if (!_warnedNoReadback)
            {
                _warnedNoReadback = true;
                Debug.LogError("[AprilTagTracker] AsyncGPUReadback is not supported on this graphics API; cannot read passthrough frames.");
            }
            return;
        }

        // Camera pose at the latest image's timestamp, captured now so it matches the frame we read back.
        _pendingCameraPose = m_passthroughCameraAccess.GetCameraPose();
        _readbackPending = true;
        AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, OnReadback);
    }

    private void OnReadback(AsyncGPUReadbackRequest request)
    {
        _readbackPending = false;
        if (this == null || !enabled)
            return;
        if (request.hasError)
        {
            Debug.LogWarning("[AprilTagTracker] Passthrough frame readback failed.");
            return;
        }

        int width = request.width;
        int height = request.height;
        var data = request.GetData<Color32>();
        if (width <= 0 || height <= 0 || data.Length < width * height)
            return;

        if (_pixels == null || _pixels.Length != width * height)
            _pixels = new Color32[width * height];

        if (flipImageVertically)
        {
            for (int row = 0; row < height; row++)
                NativeArray<Color32>.Copy(data, row * width, _pixels, (height - 1 - row) * width, width);
        }
        else
        {
            NativeArray<Color32>.Copy(data, _pixels, width * height);
        }

        Detect(width, height);
    }

    private void Detect(int width, int height)
    {
        if (!TryGetIntrinsics(width, height, out float fx, out float fy, out float cx, out float cy))
            return;

        if (_detector == null || _detectorWidth != width || _detectorHeight != height)
        {
            _detector?.Dispose();
            _detector = new TagDetector(width, height, Mathf.Max(1, decimation));
            _detectorWidth = width;
            _detectorHeight = height;
            Debug.Log($"[AprilTagTracker] Detector {width}x{height}, fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cy:F1}");
        }

        // One pass at the primary tag size; other sizes are recovered by rescaling below.
        _detector.ProcessImage(_pixels, fx, fy, cx, cy, tagSize);
        UpdateDebugTexture(width, height);

        bool primarySeen = false;
        foreach (var detected in _detector.DetectedTags)
        {
            bool isPrimary;
            float realSize;
            Transform target;

            bool isListed = _entriesById.TryGetValue(detected.ID, out var entry);
            if (tagId >= 0 ? detected.ID == tagId : (!primarySeen && !isListed))
            {
                isPrimary = true;
                realSize = tagSize;
                target = _arObject != null ? _arObject.transform : null;
            }
            else if (isListed)
            {
                isPrimary = false;
                realSize = entry.tagSize;
                target = entry.target;
            }
            else
            {
                continue;
            }

            // The pose estimator solved for corners at +/- tagSize/2. Corners at +/- realSize/2 project
            // to the same pixels exactly when the translation is scaled by realSize/tagSize; the
            // rotation is unchanged. TagPose is immutable, so build the rescaled vector here.
            Vector3 cameraLocal = detected.Position * (realSize / tagSize);

            // Tag pose is camera-local; lift to world with the camera pose at acquisition.
            var worldPose = new Pose(
                _pendingCameraPose.position + _pendingCameraPose.rotation * cameraLocal,
                _pendingCameraPose.rotation * detected.Rotation);

            ObserveTag(detected.ID, realSize, worldPose, isPrimary, target);
            if (isPrimary)
                primarySeen = true;
        }
    }

    /// <summary>
    /// Feeds a fake observation through the same filtering/event path as a real detection. For
    /// editor testing of consumers (e.g. TagReachabilityIndicator) where passthrough is unavailable.
    /// </summary>
    public void InjectDebugObservation(int id, float size, Pose worldPose)
    {
        bool isPrimary = id == tagId;
        Transform target = null;
        if (isPrimary)
            target = _arObject != null ? _arObject.transform : null;
        else if (_entriesById.TryGetValue(id, out var entry))
            target = entry.target;
        ObserveTag(id, size, worldPose, isPrimary, target);
    }

    private void ObserveTag(int id, float size, Pose worldPose, bool isPrimary, Transform target)
    {
        TrackedTag tracked;
        if (_tags.TryGetValue(id, out var previous))
        {
            float t = poseFilterCoefficient;
            tracked.FilteredPose = new Pose(
                Vector3.Lerp(worldPose.position, previous.FilteredPose.position, t),
                Quaternion.Slerp(worldPose.rotation, previous.FilteredPose.rotation, t));
            tracked.DetectionCount = previous.DetectionCount + 1;
        }
        else
        {
            tracked.FilteredPose = worldPose;
            tracked.DetectionCount = 1;
        }
        tracked.Id = id;
        tracked.Size = size;
        tracked.LastSeenTime = Time.time;
        _tags[id] = tracked;

        if (target != null)
            target.SetPositionAndRotation(
                tracked.FilteredPose.position,
                tracked.FilteredPose.rotation * Quaternion.Euler(tagToObjectEuler));

        if (isPrimary)
        {
            FilteredTagPose = tracked.FilteredPose;
            HasPose = true;
            LastDetectionTime = Time.time;
            DetectionCount++;
        }

        OnTagObserved?.Invoke(tracked);
    }

    /// <summary>
    /// Intrinsics at the delivered resolution. Mirrors PassthroughCameraAccess.CalcSensorCropRegion:
    /// the delivered image is a centred crop of the sensor scaled to the current resolution.
    /// </summary>
    private bool TryGetIntrinsics(int width, int height, out float fx, out float fy, out float cx, out float cy)
    {
        fx = fy = cx = cy = 0f;
        var intrinsics = m_passthroughCameraAccess.Intrinsics;
        Vector2 sensor = intrinsics.SensorResolution;
        if (sensor.x <= 0f || sensor.y <= 0f)
            return false;

        Vector2 scale = new Vector2(width / sensor.x, height / sensor.y);
        scale /= Mathf.Max(scale.x, scale.y);
        float cropX = sensor.x * (1f - scale.x) * 0.5f;
        float cropY = sensor.y * (1f - scale.y) * 0.5f;
        float cropWidth = sensor.x * scale.x;
        float cropHeight = sensor.y * scale.y;

        float sx = width / cropWidth;
        float sy = height / cropHeight;
        fx = intrinsics.FocalLength.x * sx;
        fy = intrinsics.FocalLength.y * sy;
        cx = (intrinsics.PrincipalPoint.x - cropX) * sx;
        cy = (intrinsics.PrincipalPoint.y - cropY) * sy;
        return true;
    }

    private void UpdateDebugTexture(int width, int height)
    {
        if (m_debugRenderer == null)
            return;
        if (_debugTexture == null || _debugTexture.width != width || _debugTexture.height != height)
        {
            _debugTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            m_debugRenderer.material.mainTexture = _debugTexture;
        }
        _debugTexture.SetPixels32(_pixels);
        _debugTexture.Apply(false);
    }

    private void OnDestroy()
    {
        _detector?.Dispose();
        _detector = null;
    }
}
