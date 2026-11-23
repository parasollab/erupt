using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering;
using Erupt.Environment;

namespace Erupt.Plugins.Ar
{
    /// <summary>
    /// Makes real-world objects detected via Meta Quest Space Setup part of the environment, so
    /// the planning scenes get them as collision objects, and renders a color-coded
    /// semi-transparent box for each anchor so you can see the boundaries in AR. Each anchor
    /// that is shared becomes an environment object (a box at the anchor's bounds) in the
    /// <see cref="EnvironmentRegistry"/>; whatever keeps planners in step with the environment
    /// (the MoveIt plugin's fan-out) sends it to every robot like any other obstacle. This
    /// component does not talk to ROS. Requires an MRUK component in the scene and Space Setup
    /// to have been run on the device. Because the anchors are ordinary environment objects,
    /// a robot that is placed somewhere else gets them again at their pose in its own frame.
    /// </summary>
    public class SceneAnchorCollisionBridge : MonoBehaviour
    {
        [Tooltip("Thickness (meters) added to plane anchors (walls, floor, ceiling) along their normal.")]
        public float planeDepth = 0.05f;

        [Tooltip("Opacity of plane overlays (floor, walls, ceiling).")]
        [Range(0f, 1f)]
        public float visualAlpha = 0.3f;

        [Tooltip("Opacity of volume overlays (furniture, tables, storage).")]
        [Range(0f, 1f)]
        public float volumeAlpha = 0.6f;

        [Tooltip("Share plane anchors (floor, ceiling, walls) with the planners as collision objects. " +
                 "Disable if you don't need them in the robot planning scenes.")]
        public bool publishPlanes = false;

        [Tooltip("Environment mirror the anchors are registered in. Found in the scene if empty.")]
        [SerializeField] private EnvironmentRegistry registry;

        private Mesh _cubeMesh;
        private Mesh _quadMesh;

        // The environment objects made for the shared anchors, by id.
        private readonly Dictionary<string, GameObject> _collisionBoxes = new();

        private struct AnchorData
        {
            public MRUKAnchor anchor;
            public Vector3 localCenter;
            public Vector3 size;
            public bool isPlane;
            public MeshRenderer visual;
        }
        private readonly List<AnchorData> _anchorData = new();

        private bool _hasFloor;
        private Vector3 _floorPosition;
        private Vector3 _floorNormal;

        public bool VisualsVisible { get; private set; } = true;

        public bool HasFloor => _hasFloor;
        public Vector3 FloorPosition => _floorPosition;
        public Vector3 FloorNormal => _floorNormal;

        void Start()
        {
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
            if (registry == null)
                Debug.LogError("[SceneAnchorCollisionBridge] No EnvironmentRegistry in the scene; anchors are shown but not shared with the planners.");

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
            var rooms = MRUK.Instance.Rooms;
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
            var rooms = MRUK.Instance?.Rooms;
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
                PublishBox(anchor, worldCenter, size);

            _anchorData.Add(new AnchorData
            {
                anchor = anchor,
                localCenter = localCenter,
                size = size,
                isPlane = isPlane,
                visual = visual
            });
        }

        MeshRenderer SpawnVisual(MRUKAnchor anchor, Vector3 size, Vector3 worldCenter, Quaternion worldRotation, bool isPlane)
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

            mr.enabled = VisualsVisible;
            return mr;
        }

        public void SetVisualsVisible(bool visible)
        {
            VisualsVisible = visible;
            foreach (var data in _anchorData)
                if (data.visual != null) data.visual.enabled = visible;
        }

        // Registers the anchor's bounds as an environment object: an invisible box at the
        // anchor's centre with the anchor's rotation and size, which is exactly what the
        // planning scenes should hold. (The visual is not used for this: a plane's visual is a
        // quad set off from the surface.)
        void PublishBox(MRUKAnchor anchor, Vector3 worldCenter, Vector3 size)
        {
            if (registry == null) return;
            string id = $"scene_{anchor.Label}_{anchor.GetInstanceID()}";

            var box = new GameObject($"Collision_{id}");
            box.transform.SetParent(transform, worldPositionStays: true);
            box.transform.SetPositionAndRotation(worldCenter, anchor.transform.rotation);
            box.transform.localScale = size;
            box.AddComponent<MeshFilter>().sharedMesh = _cubeMesh;

            _collisionBoxes[id] = box;
            registry.Adopt(box, id, EnvironmentOwner.Unity, PrimitiveType.Cube, isMesh: false);
            Debug.Log($"[SceneAnchorCollisionBridge] Registered {anchor.Label} as '{id}', size={size}");
        }

        public void RepublishAll()
        {
            // Take every shared anchor out of the environment (which removes it from the
            // planning scenes) and register each again with freshly computed poses.
            RemoveCollisionBoxes();

            foreach (var data in _anchorData)
            {
                if (data.isPlane && !publishPlanes) continue;
                Vector3 worldCenter = data.anchor.transform.TransformPoint(data.localCenter);
                PublishBox(data.anchor, worldCenter, data.size);
            }
        }

        void RemoveCollisionBoxes()
        {
            foreach (var kv in _collisionBoxes)
            {
                if (registry != null) registry.Unregister(kv.Key, RemovalOrigin.Local);
                if (kv.Value != null) Destroy(kv.Value);
            }
            _collisionBoxes.Clear();
        }

        void OnDestroy() => RemoveCollisionBoxes();

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
    }
}
