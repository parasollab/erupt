using UnityEngine;

namespace Factory
{
    /// <summary>
    /// Keeps the instruction card at a fixed spot in the user's view: a camera-local offset
    /// (below the sightline, just over a metre out) with a dead zone so the card only glides
    /// to a new spot after the head has turned well away from it, never jittering with every
    /// head movement. The root's forward points away from the camera, so a world-space
    /// UIDocument under it (which faces its local -z) faces the user.
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

        private bool _moving = true;

        public bool IsMoving => _moving;

        private void OnEnable()
        {
            Camera cam = Cam();
            if (cam != null) transform.position = cam.transform.TransformPoint(offset);
            _moving = true;
        }

        private Camera Cam() => targetCamera != null ? targetCamera : Camera.main;

        private void LateUpdate()
        {
            Camera cam = Cam();
            if (cam == null) return;
            Transform eye = cam.transform;
            Vector3 wanted = eye.TransformPoint(offset);
            Vector3 current = transform.position;
            if (!_moving)
            {
                float angle = Vector3.Angle(current - eye.position, wanted - eye.position);
                if (angle > repositionAngle || Vector3.Distance(current, wanted) > repositionDistance) _moving = true;
            }
            if (_moving)
            {
                float k = 1f - Mathf.Exp(-moveSpeed * Time.deltaTime);
                current = Vector3.Lerp(current, wanted, k);
                if (Vector3.Distance(current, wanted) < 0.005f) { current = wanted; _moving = false; }
                transform.position = current;
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
            transform.position = cam.transform.TransformPoint(offset);
            _moving = false;
        }
    }
}
