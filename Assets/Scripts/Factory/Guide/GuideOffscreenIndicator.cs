using UnityEngine;

namespace Factory
{
    /// <summary>
    /// A chevron at the edge of the user's view pointing toward the guide's target whenever the
    /// target is outside the headset's field of view, so a first-time user who is looking the
    /// wrong way is turned toward the ring, the billet, the vise or the right panel. Hidden as
    /// soon as the target is in view (the highlight takes over). Positioned on an ellipse in the
    /// camera's near field each frame; not parented to the camera so rig scripts cannot fight it.
    /// </summary>
    public class GuideOffscreenIndicator : MonoBehaviour
    {
        [SerializeField] private DemoGuide guide;
        [SerializeField, Tooltip("Unlit material (serialized asset so it survives Android stripping).")]
        private Material material;
        [SerializeField] private Color color = new Color(1f, 0.8f, 0.2f);
        [Tooltip("Camera to work from; empty = Camera.main.")]
        public Camera targetCamera;
        [SerializeField, Tooltip("Targets within this many degrees of the view axis count as in view (Quest 3: ~50 deg half-FOV, kept tighter so the user sees the highlight).")]
        private float inViewAngle = 32f;
        [SerializeField, Tooltip("Distance in front of the eye the chevron floats at (metres).")]
        private float distance = 1.1f;
        [SerializeField, Tooltip("Half-axes of the ellipse the chevron rides on, at that distance (metres).")]
        private Vector2 ellipse = new Vector2(0.42f, 0.28f);
        [SerializeField] private float size = 0.12f;
        [SerializeField] private float pulseHz = 1.5f;

        private MeshRenderer _renderer;
        private MaterialPropertyBlock _pb;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public bool Visible => _renderer != null && _renderer.enabled;
        /// <summary>Angle between the view axis and the target, degrees (last frame).</summary>
        public float LastAngle { get; private set; }

        private void Awake()
        {
            var go = new GameObject("Offscreen Chevron");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = GuideTargetHighlight.BuildChevronMesh();
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            if (material != null) _renderer.sharedMaterial = material;
            _renderer.enabled = false;
            _pb = new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_pb);
            _pb.SetColor(BaseColorId, color);
            _pb.SetColor(ColorId, color);
            _renderer.SetPropertyBlock(_pb);
        }

        private void OnDisable()
        {
            if (_renderer != null) _renderer.enabled = false;
        }

        private void LateUpdate()
        {
            Camera cam = targetCamera != null ? targetCamera : Camera.main;
            Transform target = guide != null ? guide.TargetTransform : null;
            if (cam == null || target == null || !target.gameObject.activeInHierarchy)
            {
                _renderer.enabled = false;
                return;
            }
            Vector3 targetPoint = guide.TargetBoxSize != Vector3.zero ? target.TransformPoint(guide.TargetBoxLocalCentre) : TargetCentre(target);
            Transform eye = cam.transform;
            Vector3 local = eye.InverseTransformPoint(targetPoint);
            LastAngle = Vector3.Angle(Vector3.forward, local);
            if (LastAngle < inViewAngle)
            {
                _renderer.enabled = false;
                return;
            }
            // Direction to the target projected on the view plane; behind the user it still
            // points the shortest way round.
            Vector2 dir = new Vector2(local.x, local.y);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.down;
            dir.Normalize();
            Vector3 onEllipse = new Vector3(dir.x * ellipse.x, dir.y * ellipse.y, distance);
            _renderer.transform.position = eye.TransformPoint(onEllipse);
            // The mesh points down (-y) in its own XY plane; turn it to point along dir in the view plane.
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 90f;
            _renderer.transform.rotation = eye.rotation * Quaternion.Euler(0f, 0f, angle);
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseHz * Mathf.PI * 2f);
            _renderer.transform.localScale = Vector3.one * size * (0.85f + 0.3f * pulse);
            _renderer.enabled = true;
        }

        // Renderer bounds; a panel (no renderer, poke collider at its face) by its collider; else the pivot.
        private static Vector3 TargetCentre(Transform t)
        {
            Renderer[] renderers = t.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0)
            {
                var collider = t.GetComponent<Collider>();
                return collider != null ? collider.bounds.center : t.position;
            }
            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
            return b.center;
        }
    }
}
