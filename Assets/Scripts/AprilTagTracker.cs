using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Positions a GameObject on a printed AprilTag (tagStandard41h12 family) seen by the Quest
/// passthrough camera. Replaces the OpenCV-for-Unity ChArUco tracker that used to live on
/// this GameObject: frames come from Meta's <see cref="PassthroughCameraAccess"/> through an
/// async GPU readback, detection and pose estimation run in jp.keijiro.apriltag (the
/// courtneymcbeth fork, whose full-intrinsics ProcessImage overload is required because the
/// passthrough camera's principal point is not at the image centre). Detection logic is
/// ported from BARD's AprilTagLocator.
/// </summary>
public class AprilTagTracker : MonoBehaviour
{
    [Header("Passthrough Camera")]
    [SerializeField] private PassthroughCameraAccess m_passthroughCameraAccess;

    [Header("Tag")]
    [SerializeField, Tooltip("GameObject placed on the detected tag (the marker indicator MarkerRobotPlacement reads).")]
    private GameObject _arObject;

    [SerializeField, Tooltip("Only accept this tag ID. -1 accepts the first tag detected in the frame.")]
    private int tagId = 0;

    [SerializeField, Tooltip("Detection-square size in metres. For tagStandard41h12 this is the 5 central cells " +
                             "of the 9-cell printed pattern, NOT the full printed square. Docs/tag41_12_id0.svg " +
                             "is generated to match 0.10 m when printed at 100% scale.")]
    private float tagSize = 0.10f;

    [SerializeField, Tooltip("Rotation from the tag frame (x = right, y = up in the tag plane, z = into the tag) " +
                             "to the AR object. The default lays the object flat on the tag with its up = tag " +
                             "normal and its right = tag x (MarkerRobotPlacement uses right as the robot's forward).")]
    private Vector3 tagToObjectEuler = new Vector3(-90f, 0f, 0f);

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

    /// <summary>True once the passthrough camera is delivering frames.</summary>
    public bool IsReady => m_passthroughCameraAccess != null && m_passthroughCameraAccess.IsPlaying;

    /// <summary>True once the tag has been detected at least once.</summary>
    public bool HasPose { get; private set; }

    /// <summary>Filtered world-space tag pose (tag frame: x right, y up in the tag plane, z into the tag).</summary>
    public Pose FilteredTagPose { get; private set; }

    public float LastDetectionTime { get; private set; } = -1f;
    public long DetectionCount { get; private set; }

    public string StatusText
    {
        get
        {
            if (m_passthroughCameraAccess == null) return "no PassthroughCameraAccess assigned";
            if (!m_passthroughCameraAccess.IsPlaying) return "waiting for passthrough camera";
            if (DetectionCount == 0) return "camera running | no tag seen yet";
            float age = Time.time - LastDetectionTime;
            return age < 2f
                ? $"tag {tagId} tracked ({DetectionCount} detections)"
                : $"tag lost {age:F0}s ago";
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

        _detector.ProcessImage(_pixels, fx, fy, cx, cy, tagSize);
        UpdateDebugTexture(width, height);

        TagPose tag = default;
        bool found = false;
        foreach (var detected in _detector.DetectedTags)
        {
            if (tagId >= 0 && detected.ID != tagId)
                continue;
            tag = detected;
            found = true;
            break;
        }
        if (!found)
            return;

        // Tag pose is camera-local; lift to world with the camera pose at acquisition.
        var worldPose = new Pose(
            _pendingCameraPose.position + _pendingCameraPose.rotation * tag.Position,
            _pendingCameraPose.rotation * tag.Rotation);

        if (!HasPose)
        {
            FilteredTagPose = worldPose;
            HasPose = true;
        }
        else
        {
            float t = poseFilterCoefficient;
            FilteredTagPose = new Pose(
                Vector3.Lerp(worldPose.position, FilteredTagPose.position, t),
                Quaternion.Slerp(worldPose.rotation, FilteredTagPose.rotation, t));
        }
        LastDetectionTime = Time.time;
        DetectionCount++;

        if (_arObject != null)
            _arObject.transform.SetPositionAndRotation(
                FilteredTagPose.position,
                FilteredTagPose.rotation * Quaternion.Euler(tagToObjectEuler));
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
