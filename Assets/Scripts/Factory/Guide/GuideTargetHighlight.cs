using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    /// <summary>
    /// Frames whatever <see cref="DemoGuide"/> points at: a pulsing wire box around the world
    /// object (ring, billet, jaw opening) with a bobbing chevron over it, or a pulsing frame
    /// around a panel button plus a USS class ("guide-pulse") blinking on the button itself.
    /// Nothing on the target is touched (no material swaps), so it works on CAD parts, baked
    /// billets and UI alike. The pulse speeds up once the guide escalates.
    /// </summary>
    public class GuideTargetHighlight : MonoBehaviour
    {
        public const string PulseClass = "guide-pulse";

        [SerializeField] private DemoGuide guide;
        [Tooltip("Camera the chevron faces; empty = Camera.main.")]
        public Camera targetCamera;
        [SerializeField, Tooltip("Unlit material for the box and the chevron (serialized asset so it survives Android stripping).")]
        private Material material;
        [SerializeField] private Color color = new Color(1f, 0.8f, 0.2f);
        [SerializeField] private float lineWidth = 0.008f;
        [SerializeField, Tooltip("Metres added around the target's renderer bounds.")]
        private float padding = 0.015f;
        [SerializeField] private float chevronSize = 0.07f;
        [SerializeField] private float chevronGap = 0.04f, bobHeight = 0.025f;
        [SerializeField] private float pulseHz = 1f, escalatedPulseHz = 2.5f;
        [SerializeField, Tooltip("Padding around a framed button, in the document's local units (a world-space document reports " +
                                 "worldBound in those units: x right, y down from 0, 1 unit = 1 cm before the root's scale).")]
        private float buttonPadding = 0.04f;

        private LineRenderer _box;
        private MeshRenderer _chevron;
        private MaterialPropertyBlock _pb;
        private VisualElement _pulsedElement;
        private float _blinkAt;
        private bool _blinkOn;
        private readonly Vector3[] _corners = new Vector3[8];
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public bool Visible => _box != null && _box.enabled;
        public Bounds LastBounds { get; private set; }
        public VisualElement PulsedElement => _pulsedElement;

        private void Awake()
        {
            _pb = new MaterialPropertyBlock();
            var boxGo = new GameObject("Highlight Box");
            boxGo.transform.SetParent(transform, false);
            _box = boxGo.AddComponent<LineRenderer>();
            _box.useWorldSpace = true;
            _box.loop = false;
            _box.alignment = LineAlignment.View;
            _box.textureMode = LineTextureMode.Stretch;
            _box.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _box.receiveShadows = false;
            _box.numCornerVertices = 2;
            if (material != null) _box.sharedMaterial = material;
            _box.enabled = false;

            var chevronGo = new GameObject("Highlight Chevron");
            chevronGo.transform.SetParent(transform, false);
            chevronGo.AddComponent<MeshFilter>().sharedMesh = BuildChevronMesh();
            _chevron = chevronGo.AddComponent<MeshRenderer>();
            _chevron.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _chevron.receiveShadows = false;
            if (material != null) _chevron.sharedMaterial = material;
            _chevron.enabled = false;
            Tint(color);
        }

        private void OnDisable()
        {
            SetPulsedElement(null);
            if (_box != null) _box.enabled = false;
            if (_chevron != null) _chevron.enabled = false;
        }

        private void LateUpdate()
        {
            if (guide == null || guide.TargetTransform == null || !guide.TargetTransform.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }
            float hz = guide.Escalation >= 1 ? escalatedPulseHz : pulseHz;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * hz * Mathf.PI * 2f);

            VisualElement element = FindElement();
            SetPulsedElement(element);
            if (element != null)
            {
                if (Time.time >= _blinkAt)
                {
                    _blinkOn = !_blinkOn;
                    _blinkAt = Time.time + 0.5f / Mathf.Max(0.1f, hz);
                    element.EnableInClassList(PulseClass, _blinkOn);
                }
                if (!TryElementQuad(guide.TargetDocument, element, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d))
                {
                    Hide();
                    return;
                }
                _box.positionCount = 5;
                _box.SetPositions(new[] { a, b, c, d, a });
                Bounds eb = new Bounds(a, Vector3.zero);
                eb.Encapsulate(b); eb.Encapsulate(c); eb.Encapsulate(d);
                LastBounds = eb;
                PlaceChevron(eb, pulse, guide.TargetDocument.transform.up);
            }
            else
            {
                Bounds bounds = TargetBounds();
                LastBounds = bounds;
                FillBoxCorners(bounds);
                _box.positionCount = 16;
                // Every edge of the box in one strip: the bottom square, then each vertical with its top edge.
                _box.SetPositions(new[]
                {
                    _corners[0], _corners[1], _corners[2], _corners[3], _corners[0],
                    _corners[4], _corners[5], _corners[1], _corners[5], _corners[6], _corners[2], _corners[6],
                    _corners[7], _corners[3], _corners[7], _corners[4],
                });
                PlaceChevron(bounds, pulse, Vector3.up);
            }
            _box.widthMultiplier = lineWidth * (0.7f + 0.6f * pulse);
            _box.enabled = true;
            _chevron.enabled = true;
        }

        private void Hide()
        {
            SetPulsedElement(null);
            if (_box != null) _box.enabled = false;
            if (_chevron != null) _chevron.enabled = false;
        }

        private VisualElement FindElement()
        {
            if (guide.TargetDocument == null || string.IsNullOrEmpty(guide.TargetElementName)) return null;
            // Same button as last frame: no query.
            if (_pulsedElement != null && _pulsedElement.panel != null && _pulsedElement.name == guide.TargetElementName
                && _pulsedElement.panel == guide.TargetDocument.rootVisualElement?.panel)
                return _pulsedElement;
            VisualElement root = guide.TargetDocument.rootVisualElement;
            return root?.Q<VisualElement>(guide.TargetElementName);
        }

        private void SetPulsedElement(VisualElement element)
        {
            if (_pulsedElement == element) return;
            _pulsedElement?.RemoveFromClassList(PulseClass);
            _pulsedElement = element;
            _blinkOn = false;
            _blinkAt = 0f;
        }

        // World bounds of the target: its renderers, or the guide's local box when it has none.
        private Bounds TargetBounds()
        {
            Transform t = guide.TargetTransform;
            if (guide.TargetBoxSize != Vector3.zero)
            {
                Vector3 half = guide.TargetBoxSize * 0.5f;
                Bounds local = new Bounds(t.TransformPoint(guide.TargetBoxLocalCentre), Vector3.zero);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                    local.Encapsulate(t.TransformPoint(guide.TargetBoxLocalCentre + c));
                }
                return local;
            }
            Renderer[] renderers = t.GetComponentsInChildren<Renderer>(false);
            Bounds b;
            if (renderers.Length == 0) b = new Bounds(t.position, Vector3.one * 0.05f);
            else
            {
                b = renderers[0].bounds;
                foreach (Renderer r in renderers) if (r != null && r.enabled) b.Encapsulate(r.bounds);
            }
            b.Expand(padding * 2f);
            return b;
        }

        private void FillBoxCorners(Bounds b)
        {
            Vector3 mn = b.min, mx = b.max;
            _corners[0] = new Vector3(mn.x, mn.y, mn.z);
            _corners[1] = new Vector3(mx.x, mn.y, mn.z);
            _corners[2] = new Vector3(mx.x, mn.y, mx.z);
            _corners[3] = new Vector3(mn.x, mn.y, mx.z);
            _corners[4] = new Vector3(mn.x, mx.y, mn.z);
            _corners[5] = new Vector3(mx.x, mx.y, mn.z);
            _corners[6] = new Vector3(mx.x, mx.y, mx.z);
            _corners[7] = new Vector3(mn.x, mx.y, mx.z);
        }

        // The element's rectangle in world space, a hair in front of the panel (its -z faces the user).
        // A world-space document's worldBound is already in the document's local units, y running
        // downward from 0 (the Confirm button of a 300 x 430 panel reads x 0.1..2.9, y -3.39..-3.19).
        private bool TryElementQuad(UIDocument doc, VisualElement element, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
        {
            a = b = c = d = Vector3.zero;
            Rect wb = element.worldBound;
            if (float.IsNaN(wb.x) || float.IsNaN(wb.width) || wb.width <= 0f || wb.height <= 0f) return false;
            float pad = buttonPadding;
            float x0 = wb.xMin - pad, x1 = wb.xMax + pad;
            float y0 = wb.yMin - pad, y1 = wb.yMax + pad;
            const float z = -0.01f;
            Transform t = doc.transform;
            a = t.TransformPoint(new Vector3(x0, y0, z));
            b = t.TransformPoint(new Vector3(x1, y0, z));
            c = t.TransformPoint(new Vector3(x1, y1, z));
            d = t.TransformPoint(new Vector3(x0, y1, z));
            return true;
        }

        private void PlaceChevron(Bounds b, float pulse, Vector3 up)
        {
            float bob = bobHeight * pulse;
            Vector3 top = b.center + up * (b.extents.y + chevronGap + chevronSize * 0.5f + bob);
            if (up != Vector3.up) top = b.center + up * (chevronGap + chevronSize * 0.5f + bob) + Vector3.up * 0f;
            _chevron.transform.position = top;
            _chevron.transform.localScale = Vector3.one * chevronSize * (0.9f + 0.2f * pulse);
            Camera cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam != null)
            {
                Vector3 away = top - cam.transform.position;
                if (away.sqrMagnitude > 1e-6f) _chevron.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
            }
        }

        private void Tint(Color c)
        {
            if (_pb == null) _pb = new MaterialPropertyBlock();
            foreach (Renderer r in new Renderer[] { _box, _chevron })
            {
                if (r == null) continue;
                r.GetPropertyBlock(_pb);
                _pb.SetColor(BaseColorId, c);
                _pb.SetColor(ColorId, c);
                r.SetPropertyBlock(_pb);
            }
        }

        /// <summary>A flat "V" pointing down (-y) in its local XY plane, both windings so it reads from either side.</summary>
        public static Mesh BuildChevronMesh()
        {
            const float w = 0.5f, h = 0.5f, t = 0.14f; // half width, height, arm thickness
            Vector3[] v =
            {
                new Vector3(-w, h, 0f), new Vector3(-w + t, h, 0f), new Vector3(0f, -h + t, 0f), new Vector3(0f, -h, 0f),
                new Vector3(w, h, 0f), new Vector3(w - t, h, 0f),
            };
            // Left arm: 0,1,2,3 ; right arm: 4,5,2,3
            int[] front = { 0, 1, 2, 0, 2, 3, 5, 4, 3, 5, 3, 2 };
            int[] back = { 2, 1, 0, 3, 2, 0, 3, 4, 5, 2, 3, 5 };
            var tris = new int[front.Length + back.Length];
            front.CopyTo(tris, 0);
            back.CopyTo(tris, front.Length);
            var mesh = new Mesh { name = "GuideChevron" };
            mesh.SetVertices(v);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
