using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Draws a pointer ray for one source. Only the visual lives here: the input is read by a
    /// backend, and hit-testing is asked of the router, not repeated.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class InteractionRayVisual : MonoBehaviour
    {
        [SerializeField] private InteractionRouter router;
        [SerializeField] private InteractionSourceBehaviour source;

        [Tooltip("Hidden when the source lacks Capability.Ray.")]
        [SerializeField] private bool requireRayCapability = true;

        [SerializeField] private Color idleRayColor = new Color(0.05f, 0.75f, 1f, 0.85f);
        [SerializeField] private Color hitRayColor = new Color(1f, 0.75f, 0.05f, 0.95f);

        private LineRenderer lineRenderer;

        private void Awake()
        {
            lineRenderer = GetComponent<LineRenderer>();
            if (router == null) router = GetComponentInParent<InteractionRouter>();
            if (source == null) source = GetComponentInParent<InteractionSourceBehaviour>();
            ConfigureLineRenderer();
        }

        private void ConfigureLineRenderer()
        {
            lineRenderer.useWorldSpace = true;
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = 0.01f;
            lineRenderer.endWidth = 0.0025f;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) lineRenderer.material = new Material(shader);

            lineRenderer.startColor = idleRayColor;
            lineRenderer.endColor = idleRayColor;
        }

        private void LateUpdate()
        {
            if (router == null || source == null)
            {
                lineRenderer.enabled = false;
                return;
            }

            // Capability, not device — a source that reports no ray draws nothing.
            bool visible = source.IsActive && (!requireRayCapability || source.Has(Capability.Ray));
            lineRenderer.enabled = visible;
            if (!visible) return;

            bool hasHit = router.ResolvePointer(source, out Ray ray, out RaycastHit hit);
            float distance = hasHit ? hit.distance : router.RayLength;

            Color color = hasHit ? hitRayColor : idleRayColor;
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.SetPosition(0, ray.origin);
            lineRenderer.SetPosition(1, ray.GetPoint(distance));
        }
    }
}
