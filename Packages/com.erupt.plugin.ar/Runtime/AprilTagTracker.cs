using System;
using System.Collections.Generic;
using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Erupt.Plugins.Ar
{
    /// <summary>The latest filtered observation of one tag.</summary>
    public struct TrackedTag
    {
        public int Id;
        /// <summary>Size of the tag's detection square in metres, as configured for this tag.</summary>
        public float Size;
        /// <summary>World pose of the tag (tag frame: x right, y up in the tag's plane, z into the tag), low-pass filtered.</summary>
        public Pose Pose;
        /// <summary>When the tag was last seen, on the tracker's clock (<see cref="AprilTagTracker.Now"/>).</summary>
        public float LastSeenTime;
        public long DetectionCount;
    }

    /// <summary>One tag the tracker looks for.</summary>
    [Serializable]
    public class TrackedTagEntry
    {
        [Tooltip("tagStandard41h12 id (0-2114). Each id is listed once.")]
        public int id;

        [Tooltip(AprilTagTracker.SizeRule)]
        public float tagSize = 0.10f;

        [Tooltip("Optional transform laid on the tag whenever it is seen (turned from the tag frame by Tag To Object Euler). " +
                 "Leave empty for a tag that is only read through TryGetTag / TagObserved.")]
        public Transform target;
    }

    /// <summary>
    /// Tracks printed AprilTags of the tagStandard41h12 family seen by the Meta Quest
    /// passthrough camera: any number of tags, each with its own id and size, from one
    /// detection pass per frame read. Frames come from Meta's <see cref="PassthroughCameraAccess"/>
    /// through an asynchronous GPU readback; detection and pose estimation run in
    /// <c>jp.keijiro.apriltag</c> (the fork with the full-intrinsics <c>ProcessImage</c>, which
    /// is needed because the passthrough camera's principal point is not at the image centre).
    /// </summary>
    /// <remarks>
    /// Meta Quest only: there is no camera abstraction. Without a passthrough camera (in the
    /// editor, in a test) the tracker does nothing; <see cref="InjectObservation"/> feeds a
    /// pose through the same filtering and events as a detection. The arithmetic is in
    /// <see cref="MarkerMath"/>.
    /// </remarks>
    public class AprilTagTracker : MonoBehaviour
    {
        /// <summary>The size rule, as shown on every tag-size field.</summary>
        public const string SizeRule =
            "Size of the tag's detection square in metres: the inner square the detector measures, which for tagStandard41h12 is " +
            "the 5 central cells of the 9-cell printed pattern, NOT the full printed square. A tag printed from " +
            "Documentation~/tags at 100 % scale has the size in its file name (100mm = 0.10); measure the inner square with a ruler " +
            "and enter what you measure.";

        // Detection runs once per image at this size; each tag's pose is then rescaled to its own
        // size (MarkerMath.RescaleToTagSize), so the value only has to be positive.
        private const float ReferenceTagSize = 0.1f;

        [Header("Passthrough camera")]
        [Tooltip("The Quest passthrough camera. Found in the scene if empty; without one the tracker does nothing.")]
        [SerializeField] private PassthroughCameraAccess passthroughCamera;

        [Tooltip("The transform the headset's poses are relative to (the XR rig's camera offset). Empty = the parent of the main " +
                 "camera; with neither, device poses are taken as world poses.")]
        [SerializeField] private Transform trackingSpace;

        [Header("Tags")]
        [Tooltip("The tags to look for. The AR plugin adds the tags of the robots' marker anchors by itself; list here the tags " +
                 "that something else reads. A tag that is not listed is ignored: its size is not known, so its distance would be wrong.")]
        [SerializeField] private List<TrackedTagEntry> tags = new List<TrackedTagEntry>();

        [Tooltip("Rotation from the tag frame (x right, y up in the tag's plane, z into the tag) to a target transform. The default " +
                 "lays the target flat on the tag: its up is the tag's normal, its forward the tag's top edge.")]
        [SerializeField] private Vector3 tagToObjectEuler = new Vector3(-90f, 0f, 0f);

        [Header("Detection")]
        [Tooltip("Detections per second. Detection is expensive and placing a robot is not time-critical.")]
        [SerializeField, Min(0.01f)] private float detectionRate = 4f;

        [Tooltip("AprilTag quad decimation. Higher is faster and shortens the distance at which a tag is detected.")]
        [SerializeField, Min(1)] private int decimation = 2;

        [Range(0, 1)]
        [Tooltip("Low-pass filter on each tag's pose: 0 takes every detection as it is, 1 never moves.")]
        [SerializeField] private float poseFilterCoefficient = 0.5f;

        [Tooltip("Reverse the image rows before detection. The detector expects Unity's bottom-up row order. Leave off unless the " +
                 "debug renderer shows the frame upside down; a vertically mirrored tag never decodes, so the symptom is no detection at all.")]
        [SerializeField] private bool flipImageVertically;

        [Header("Debug")]
        [Tooltip("Optional renderer whose material shows the frame exactly as the detector gets it.")]
        [SerializeField] private MeshRenderer debugRenderer;

        private TagDetector detector;
        private int detectorWidth, detectorHeight;
        private Color32[] pixels;
        private Color32[] readback;
        private bool readbackPending;
        private Pose pendingCameraPose;
        private float nextDetection;
        private Texture2D debugTexture;
        private bool warnedNoReadback, lookedForCamera;
        private readonly Dictionary<int, TrackedTagEntry> entriesById = new Dictionary<int, TrackedTagEntry>();
        private readonly Dictionary<int, TrackedTag> observed = new Dictionary<int, TrackedTag>();
        private bool entriesRead;

        // The clock tag ages are measured on; tests replace it.
        internal Func<float> clock = () => Time.time;

        /// <summary>The time tag observations are stamped with (<see cref="TrackedTag.LastSeenTime"/>).</summary>
        public float Now => clock();

        /// <summary>Low-pass filter on each tag's pose: 0 takes every detection as it is, 1 never moves.</summary>
        public float PoseFilterCoefficient
        {
            get => poseFilterCoefficient;
            set => poseFilterCoefficient = Mathf.Clamp01(value);
        }

        /// <summary>True once the passthrough camera is delivering frames.</summary>
        public bool IsReady => passthroughCamera != null && passthroughCamera.IsPlaying;

        /// <summary>Raised on the main thread each time a tag is observed, with its filtered pose.</summary>
        public event Action<TrackedTag> TagObserved;

        /// <summary>Every tag observed so far, by id.</summary>
        public IReadOnlyDictionary<int, TrackedTag> Tags => observed;

        /// <summary>The latest observation of a tag; false for a tag that has not been seen.</summary>
        public bool TryGetTag(int id, out TrackedTag tag) => observed.TryGetValue(id, out tag);

        /// <summary>Whether a tag was seen within the last <paramref name="maxAgeSeconds"/>.</summary>
        public bool IsVisible(int id, float maxAgeSeconds) =>
            observed.TryGetValue(id, out TrackedTag tag) && Now - tag.LastSeenTime <= maxAgeSeconds;

        /// <summary>The size the tracker has for a tag id; false for a tag it does not look for.</summary>
        public bool TryGetTagSize(int id, out float size)
        {
            ReadEntries();
            size = entriesById.TryGetValue(id, out TrackedTagEntry entry) ? entry.tagSize : 0f;
            return size > 0f;
        }

        /// <summary>
        /// Look for a tag of this id and size from now on (the AR plugin calls this for every
        /// robot's marker anchor). Asking again with the same size changes nothing. False,
        /// with an error in the log, for a size that is not positive or that contradicts the
        /// size the id already has: one printed tag has one size.
        /// </summary>
        public bool Track(int id, float tagSize) => Track(id, tagSize, null);

        /// <summary>As <see cref="Track(int, float)"/>, and lay <paramref name="target"/> on the tag whenever it is seen (null leaves the tag's target as it is).</summary>
        public bool Track(int id, float tagSize, Transform target)
        {
            ReadEntries();
            if (tagSize <= 0f)
            {
                Debug.LogError($"[AprilTagTracker] Tag {id}: a tag size of {tagSize} m is not a size; the tag is not tracked.", this);
                return false;
            }
            if (entriesById.TryGetValue(id, out TrackedTagEntry existing))
            {
                if (Mathf.Abs(existing.tagSize - tagSize) <= 1e-5f)
                {
                    if (target != null) existing.target = target;
                    return true;
                }
                Debug.LogError($"[AprilTagTracker] Tag {id} is asked for with a size of {tagSize} m but is already tracked with {existing.tagSize} m; " +
                               "the first size stays. Give every user of one tag the same size.", this);
                return false;
            }
            entriesById.Add(id, new TrackedTagEntry { id = id, tagSize = tagSize, target = target });
            return true;
        }

        /// <summary>What the tracker is doing, in words, for a status line.</summary>
        public string StatusText
        {
            get
            {
                ReadEntries();
                if (passthroughCamera == null) return "no passthrough camera";
                if (!passthroughCamera.IsPlaying) return "waiting for the passthrough camera";
                if (entriesById.Count == 0) return "camera running, no tag to look for";
                int visible = 0;
                foreach (KeyValuePair<int, TrackedTag> tag in observed)
                    if (Now - tag.Value.LastSeenTime < 2f) visible++;
                return observed.Count == 0
                    ? $"camera running, no tag seen yet ({entriesById.Count} looked for)"
                    : $"{visible} of {entriesById.Count} tags in view";
            }
        }

        private void Awake() => ReadEntries();

        // The serialized list, checked once: a bad entry is reported and left out.
        private void ReadEntries()
        {
            if (entriesRead) return;
            entriesRead = true;
            foreach (TrackedTagEntry entry in tags)
            {
                if (entry == null) continue;
                if (entry.tagSize <= 0f)
                {
                    Debug.LogError($"[AprilTagTracker] Tags: id {entry.id} has a size of {entry.tagSize} m; the entry is ignored.", this);
                    continue;
                }
                if (entriesById.ContainsKey(entry.id))
                {
                    Debug.LogError($"[AprilTagTracker] Tags: id {entry.id} is listed twice; the later entry is ignored.", this);
                    continue;
                }
                entriesById.Add(entry.id, entry);
            }
        }

        private void Update()
        {
            if (passthroughCamera == null && !lookedForCamera)
            {
                lookedForCamera = true;
                passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();
            }
            if (!IsReady || readbackPending || Time.time < nextDetection) return;

            Texture texture = passthroughCamera.GetTexture();
            if (texture == null) return;

            nextDetection = Time.time + 1f / Mathf.Max(detectionRate, 0.01f);

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                if (!warnedNoReadback)
                {
                    warnedNoReadback = true;
                    Debug.LogError("[AprilTagTracker] Asynchronous GPU readback is not supported on this graphics API; passthrough frames cannot be read.", this);
                }
                return;
            }

            // The camera pose at the latest image's timestamp, taken now so it matches the frame that is read back.
            pendingCameraPose = InWorld(passthroughCamera.GetCameraPose());
            readbackPending = true;
            AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, OnReadback);
        }

        // Device poses are relative to the rig's tracking space, which need not be the world origin.
        private Pose InWorld(Pose devicePose)
        {
            Transform space = trackingSpace;
            if (space == null && Camera.main != null) space = Camera.main.transform.parent;
            return space == null ? devicePose : MarkerMath.ToWorld(new Pose(space.position, space.rotation), devicePose.position, devicePose.rotation);
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (this == null || !enabled) return;
            if (request.hasError)
            {
                Debug.LogWarning("[AprilTagTracker] Reading back the passthrough frame failed.", this);
                return;
            }

            int width = request.width;
            int height = request.height;
            NativeArray<Color32> data = request.GetData<Color32>();
            if (width <= 0 || height <= 0 || data.Length < width * height) return;

            if (pixels == null || pixels.Length != width * height) pixels = new Color32[width * height];
            if (flipImageVertically)
            {
                if (readback == null || readback.Length != width * height) readback = new Color32[width * height];
                NativeArray<Color32>.Copy(data, readback, width * height);
                MarkerMath.FlipRows(readback, pixels, width, height);
            }
            else
            {
                NativeArray<Color32>.Copy(data, pixels, width * height);
            }

            Detect(width, height);
        }

        private void Detect(int width, int height)
        {
            PassthroughCameraAccess.CameraIntrinsics intrinsics = passthroughCamera.Intrinsics;
            if (!MarkerMath.TryScaleIntrinsics(intrinsics.SensorResolution, intrinsics.FocalLength, intrinsics.PrincipalPoint,
                    width, height, out float fx, out float fy, out float cx, out float cy))
                return;

            if (detector == null || detectorWidth != width || detectorHeight != height)
            {
                detector?.Dispose();
                detector = new TagDetector(width, height, Mathf.Max(1, decimation));
                detectorWidth = width;
                detectorHeight = height;
                Debug.Log($"[AprilTagTracker] Detector {width}x{height}, fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cy:F1}");
            }

            // One pass at the reference size; each tag's own size is recovered by rescaling below.
            detector.ProcessImage(pixels, fx, fy, cx, cy, ReferenceTagSize);
            UpdateDebugTexture(width, height);

            foreach (TagPose detected in detector.DetectedTags)
            {
                if (!entriesById.TryGetValue(detected.ID, out TrackedTagEntry entry)) continue;
                Vector3 cameraLocal = MarkerMath.RescaleToTagSize(detected.Position, entry.tagSize, ReferenceTagSize);
                Observe(entry, MarkerMath.ToWorld(pendingCameraPose, cameraLocal, detected.Rotation));
            }
        }

        /// <summary>
        /// Feed an observation of a tag through the same filtering and events as a detection:
        /// for the editor and for tests, where there is no passthrough camera. The tag has to
        /// be one the tracker looks for (listed, or added with <see cref="Track"/>).
        /// </summary>
        /// <param name="worldPose">The tag's world pose in the tag frame (x right, y up in the tag's plane, z into the tag).</param>
        public bool InjectObservation(int id, Pose worldPose)
        {
            ReadEntries();
            if (!entriesById.TryGetValue(id, out TrackedTagEntry entry)) return false;
            Observe(entry, worldPose);
            return true;
        }

        private void Observe(TrackedTagEntry entry, Pose worldPose)
        {
            TrackedTag tracked;
            if (observed.TryGetValue(entry.id, out TrackedTag previous))
            {
                tracked.Pose = MarkerMath.Filter(previous.Pose, worldPose, poseFilterCoefficient);
                tracked.DetectionCount = previous.DetectionCount + 1;
            }
            else
            {
                tracked.Pose = worldPose;
                tracked.DetectionCount = 1;
            }
            tracked.Id = entry.id;
            tracked.Size = entry.tagSize;
            tracked.LastSeenTime = Now;
            observed[entry.id] = tracked;

            if (entry.target != null)
            {
                Pose onTag = MarkerMath.ObjectOnTag(tracked.Pose, tagToObjectEuler);
                entry.target.SetPositionAndRotation(onTag.position, onTag.rotation);
            }

            TagObserved?.Invoke(tracked);
        }

        private void UpdateDebugTexture(int width, int height)
        {
            if (debugRenderer == null) return;
            if (debugTexture == null || debugTexture.width != width || debugTexture.height != height)
            {
                debugTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                debugRenderer.material.mainTexture = debugTexture;
            }
            debugTexture.SetPixels32(pixels);
            debugTexture.Apply(false);
        }

        private void OnDestroy()
        {
            detector?.Dispose();
            detector = null;
            if (debugTexture != null) Destroy(debugTexture);
        }
    }
}
