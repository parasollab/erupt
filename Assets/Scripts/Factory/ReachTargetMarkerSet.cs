using System.Collections.Generic;
using UnityEngine;

namespace Factory
{
    /// <summary>
    /// One small sphere per pick/place target, coloured from the robot's reachability map: green
    /// when a top-down approach is solvable there, amber when only other approach directions are,
    /// red when the point is outside the reachable workspace. Sampling goes through
    /// ReachabilityVolumeVisualizer.SampleDexterity, which reads the robot base live, so the
    /// markers answer correctly while the robot is being moved. Keep this on its own root: a
    /// MeshFilter under a 'table' compound root would be published as a collision primitive.
    /// </summary>
    public class ReachTargetMarkerSet : MonoBehaviour
    {
        [SerializeField, Tooltip("Visualizer whose current map (baked while placing) is sampled.")]
        private ReachabilityVolumeVisualizer reachability;

        [SerializeField, Tooltip("Points the robot must reach: fixture drop anchor, stock slots, conveyor drop anchor.")]
        private Transform[] targets = new Transform[0];

        [SerializeField, Tooltip("Opaque URP/Lit material for the spheres (serialized asset so it survives Android stripping).")]
        private Material markerMaterial;

        [SerializeField] private float markerRadius = 0.025f;
        [SerializeField] private Color reachableColor = new Color(0.15f, 0.9f, 0.25f);
        [SerializeField] private Color marginalColor = new Color(1f, 0.75f, 0.1f);
        [SerializeField] private Color unreachableColor = new Color(0.95f, 0.2f, 0.15f);

        [SerializeField, Range(0.05f, 0.95f),
         Tooltip("Trilinear top-down dexterity at or above this counts as reachable (the map's 'down' bit is " +
                 "0/1 per voxel, so 0.5 means the nearest voxels mostly have it).")]
        private float topDownPassThreshold = 0.5f;

        private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        private readonly List<float> _topDown = new List<float>();
        private readonly List<float> _any = new List<float>();
        private MaterialPropertyBlock _pb;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public int Count => targets != null ? targets.Length : 0;
        public int ReachableCount { get; private set; }
        public IReadOnlyList<float> TopDownDexterity => _topDown;
        public IReadOnlyList<Transform> Targets => targets;

        /// <summary>True when the map says a top-down approach is solvable at target i.</summary>
        public bool IsReachable(int i) => i >= 0 && i < _topDown.Count && _topDown[i] >= topDownPassThreshold;

        /// <summary>Short label for warnings: parent/name, since several targets are called DropAnchor.</summary>
        public string TargetLabel(int i)
        {
            if (targets == null || i < 0 || i >= targets.Length || targets[i] == null) return $"target {i}";
            Transform t = targets[i];
            return t.parent != null ? $"{t.parent.name}/{t.name}" : t.name;
        }

        private void Awake()
        {
            _pb = new MaterialPropertyBlock();
            for (int i = 0; i < Count; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Marker {i} ({TargetLabel(i)})";
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * (markerRadius * 2f);
                Collider col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var r = go.GetComponent<MeshRenderer>();
                if (markerMaterial != null) r.sharedMaterial = markerMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderers.Add(r);
                _topDown.Add(0f);
                _any.Add(0f);
            }
        }

        private void OnEnable()
        {
            if (reachability != null) reachability.MapChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (reachability != null) reachability.MapChanged -= Refresh;
        }

        private void LateUpdate() => Refresh();

        public void SetVisible(bool on)
        {
            foreach (MeshRenderer r in _renderers) if (r != null) r.enabled = on;
        }

        /// <summary>Re-samples every target and recolours its marker.</summary>
        public void Refresh()
        {
            int reachable = 0;
            for (int i = 0; i < _renderers.Count && i < Count; i++)
            {
                Transform t = targets[i];
                MeshRenderer r = _renderers[i];
                if (t == null || r == null) continue;
                r.transform.position = t.position;
                float down = reachability != null ? reachability.SampleDexterity(t.position, true) : 0f;
                float any = reachability != null ? reachability.SampleDexterity(t.position, false) : 0f;
                _topDown[i] = down;
                _any[i] = any;
                Color c;
                if (down >= topDownPassThreshold) { c = Color.Lerp(marginalColor, reachableColor, Mathf.InverseLerp(0.3f, 0.9f, any)); reachable++; }
                else if (any > 0.05f) c = Color.Lerp(unreachableColor, marginalColor, Mathf.InverseLerp(0.05f, 0.5f, any));
                else c = unreachableColor;
                r.GetPropertyBlock(_pb);
                _pb.SetColor(BaseColorId, c);
                _pb.SetColor(ColorId, c);
                r.SetPropertyBlock(_pb);
            }
            ReachableCount = reachable;
        }
    }
}
