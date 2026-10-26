using UnityEngine;

namespace Factory
{
    /// <summary>
    /// Keeps the instruction card at a fixed spot in the user's view: a camera-local offset
    /// (below the sightline, just over a metre out) with a dead zone so the card only glides
    /// to a new spot after the head has turned well away from it, never jittering with every
    /// head movement. The root's forward points away from the camera, so a world-space
    /// UIDocument under it (which faces its local -z) faces the user. When the user walks up to
    /// the machine the card's spot can lie inside its geometry; a raycast from the eye then
    /// pulls the card in front of the obstacle and scales it down to the same apparent size.
    /// </summary>
    public class GuideHeadFollow : MonoBehaviour
    {
        [Tooltip("Camera to follow; empty = Camera.main.")]
        public Camera targetCamera;
        [Tooltip("Where the card's centre sits, in the camera's local frame (metres).")]
        public Vector3 offset = new Vector3(0f, -0.36f, 1.3f);
        [Tooltip("The card starts gliding once its direction from the eye is this far (degrees) from the wanted one.")]
        public float repositionAngle = 14f;
        [Tooltip("...or once it is this far (metres) from the wanted spot (walking).")]
        public float repositionDistance = 0.35f;
        [Tooltip("Glide speed (higher = snappier).")]
        public float moveSpeed = 5f;
        [Tooltip("Keep the card level instead of tilting it toward the eye.")]
        public bool keepUpright = false;
        [Tooltip("When something solid (the machine, a panel) sits between the eye and the card's spot, pull the card in front of it " +
                 "and shrink it so it keeps its apparent size, instead of letting it sink into the geometry.")]
        public bool avoidOcclusion = true;
        [Tooltip("Never closer to the eye than this (metres).")]
        public float minDistance = 0.45f;
        [Tooltip("Gap kept in front of the blocking surface (metres).")]
        public float occlusionMargin = 0.08f;
        [Tooltip("Colliders the occlusion check looks at. Triggers are ignored.")]
        public LayerMask occlusionLayers = ~0;
        [Tooltip("Roots whose renderers' bounds also count as obstacles (the machine's mesh colliders are not convex, so a ray " +
                 "from an eye already inside the machine hits nothing). Big boxes only: renderers under this size are skipped.")]
        public Transform[] occluderRoots = new Transform[0];
        public float occluderMinSize = 0.3f;
        [Tooltip("Seconds between re-collecting the occluder renderers.")]
        public float occluderRefreshSeconds = 2f;

        private bool _moving = true;
        private readonly System.Collections.Generic.List<Renderer> _occluders = new System.Collections.Generic.List<Renderer>();
        private float _nextOccluderRefresh;
        private Vector3 _baseScale = Vector3.one;
        private float _nominalDistance = 1f;
        private float _scaleFactor = 1f;

        /// <summary>Current size factor (1 = nominal; smaller when pulled in front of an obstacle).</summary>
        public float ScaleFactor => _scaleFactor;
        /// <summary>True when the last placement was pulled in by an obstacle.</summary>
        public bool Occluded => _scaleFactor < 0.999f;

        private void RefreshOccluders()
        {
            _nextOccluderRefresh = Time.time + Mathf.Max(0.5f, occluderRefreshSeconds);
            _occluders.Clear();
            if (occluderRoots == null) return;
            foreach (Transform root in occluderRoots)
            {
                if (root == null) continue;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled && r.bounds.size.magnitude >= occluderMinSize) _occluders.Add(r);
            }
        }

        // Nearest obstacle along eye -> wanted from the renderer bounds; eye inside a box = 0.
        private float BoundsBlockDistance(Vector3 eye, Vector3 dir, float maxDist)
        {
            if (Time.time >= _nextOccluderRefresh) RefreshOccluders();
            float best = float.PositiveInfinity;
            var ray = new Ray(eye, dir);
            foreach (Renderer r in _occluders)
            {
                if (r == null) continue;
                Bounds b = r.bounds;
                if (b.Contains(eye)) return 0f;
                if (b.IntersectRay(ray, out float d) && d < maxDist && d < best) best = d;
            }
            return best;
        }

        public bool IsMoving => _moving;

        private void OnEnable()
        {
            _baseScale = transform.localScale / Mathf.Max(0.01f, _scaleFactor);
            _scaleFactor = 1f;
            transform.localScale = _baseScale;
            _nominalDistance = Mathf.Max(0.01f, offset.magnitude);
            RefreshOccluders();
            Camera cam = Cam();
            if (cam != null) transform.position = cam.transform.TransformPoint(offset);
            _moving = true;
        }

        private void OnDisable()
        {
            transform.localScale = _baseScale;
            _scaleFactor = 1f;
        }

        // The spot the card should be at: the camera-local offset, pulled toward the eye when a
        // collider blocks the line of sight to it. The card is scaled by the same factor so it
        // looks the same size from the eye.
        private Vector3 Wanted(Transform eye, out float scale)
        {
            Vector3 wanted = eye.TransformPoint(offset);
            scale = 1f;
            if (!avoidOcclusion) return wanted;
            Vector3 toCard = wanted - eye.position;
            float dist = toCard.magnitude;
            if (dist < 1e-4f) return wanted;
            Vector3 dir = toCard / dist;
            // A little beyond the card, so a wall right behind it also counts.
            float block = float.PositiveInfinity;
            if (Physics.Raycast(eye.position, dir, out RaycastHit hit, dist + occlusionMargin, occlusionLayers, QueryTriggerInteraction.Ignore))
                block = hit.distance;
            block = Mathf.Min(block, BoundsBlockDistance(eye.position, dir, dist + occlusionMargin));
            if (block < dist + occlusionMargin)
            {
                float allowed = Mathf.Clamp(block - occlusionMargin, minDistance, dist);
                scale = allowed / dist;
                return eye.position + dir * allowed;
            }
            return wanted;
        }

        private Camera Cam() => targetCamera != null ? targetCamera : Camera.main;

        private void LateUpdate()
        {
            Camera cam = Cam();
            if (cam == null) return;
            Transform eye = cam.transform;
            Vector3 wanted = Wanted(eye, out float scale);
            Vector3 current = transform.position;
            if (!_moving)
            {
                float angle = Vector3.Angle(current - eye.position, wanted - eye.position);
                // Being pulled in or let out along the sightline is a distance change only; the
                // scale tolerance keeps that from waiting on the dead zone.
                if (angle > repositionAngle || Vector3.Distance(current, wanted) > repositionDistance * scale || Mathf.Abs(scale - _scaleFactor) > 0.05f) _moving = true;
            }
            if (_moving)
            {
                float k = 1f - Mathf.Exp(-moveSpeed * Time.deltaTime);
                current = Vector3.Lerp(current, wanted, k);
                _scaleFactor = Mathf.Lerp(_scaleFactor, scale, k);
                if (Vector3.Distance(current, wanted) < 0.005f && Mathf.Abs(scale - _scaleFactor) < 0.01f) { current = wanted; _scaleFactor = scale; _moving = false; }
                transform.position = current;
                transform.localScale = _baseScale * _scaleFactor;
            }
            Vector3 away = current - eye.position;
            if (keepUpright) away.y = 0f;
            if (away.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        /// <summary>Jump to the wanted spot now (scene start, restart).</summary>
        public void Snap()
        {
            Camera cam = Cam();
            if (cam == null) return;
            transform.position = Wanted(cam.transform, out float scale);
            _scaleFactor = scale;
            transform.localScale = _baseScale * scale;
            _moving = false;
        }
    }
}
