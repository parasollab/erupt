using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Proximity haptics: pulses both controllers as the robot's tip link approaches a
    /// non-robot collider. Haptics are a backend capability (<see cref="Capability.Haptics"/>),
    /// so this lives with the OpenXR backend. Absorbed from RADER in Phase 5.
    /// </summary>
    public class CollisionHaptics : MonoBehaviour
    {
        [SerializeField] private float maxIntensity = 1.0f;
        [SerializeField] private float minDistance = 0.3f;

        [SerializeField] private InputActionReference leftHapticAction;
        [SerializeField] private InputActionReference rightHapticAction;

        [Tooltip("The end effector or last link of the robot to check proximity from.")]
        [SerializeField] private Transform tipLink;

        [Tooltip("Additional objects to exclude from obstacle detection (e.g. the target sphere).")]
        [SerializeField] private Transform[] excludeTransforms;

        [Tooltip("Root of the robot URDF. Used to tag robot colliders on enable so they are excluded from obstacle detection.")]
        [SerializeField] private Transform robotRoot;

        [SerializeField] private LayerMask obstacleLayerMask = ~0;

        [SerializeField] private bool debugMode = false;

        private const string RobotTag = "robot";

        private HapticControlActionManager hapticManager;
        private readonly Collider[] overlapBuffer = new Collider[32];
        private float debugLogTimer;

        /// <summary>Point the haptics at a robot at runtime (a plugin or the app may assign the context's robot).</summary>
        public void SetRobot(Transform root, Transform tip)
        {
            robotRoot = root;
            tipLink = tip;
            if (isActiveAndEnabled) TagRobotColliders();
        }

        private void OnEnable()
        {
            hapticManager = new HapticControlActionManager();
            TagRobotColliders();
        }

        private void TagRobotColliders()
        {
            if (robotRoot != null)
                foreach (var col in robotRoot.GetComponentsInChildren<Collider>())
                    col.gameObject.tag = RobotTag;

            if (excludeTransforms == null) return;
            foreach (var t in excludeTransforms)
                if (t != null)
                    foreach (var col in t.GetComponentsInChildren<Collider>(true))
                        col.gameObject.tag = RobotTag;
        }

        private void Update()
        {
            if (tipLink == null) return;

            float closestDistance = FindClosestObstacleDistance();
            if (closestDistance >= minDistance) return;

            TriggerHaptics(CalculateIntensity(closestDistance));
        }

        private float FindClosestObstacleDistance()
        {
            float closest = float.MaxValue;
            Collider closestCollider = null;

            int count = Physics.OverlapSphereNonAlloc(
                tipLink.position, minDistance, overlapBuffer,
                obstacleLayerMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider col = overlapBuffer[i];
                if (col.CompareTag(RobotTag)) continue;

                Vector3 surfacePoint = col.ClosestPoint(tipLink.position);
                float dist = Vector3.Distance(tipLink.position, surfacePoint);
                if (dist < closest)
                {
                    closest = dist;
                    closestCollider = col;
                }
            }

            if (debugMode && closest < minDistance)
            {
                debugLogTimer += Time.deltaTime;
                if (debugLogTimer >= 1f)
                {
                    debugLogTimer = 0f;
                    Debug.Log($"[CollisionHaptics] Closest obstacle: '{closestCollider?.gameObject.name}' " +
                              $"(tag='{closestCollider?.tag}', path='{GetPath(closestCollider?.transform)}') " +
                              $"dist={closest:F3}m");
                }
            }

            return closest;
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private float CalculateIntensity(float distance)
        {
            // Smooth quadratic ramp: 0 at minDistance, maxIntensity at contact.
            float t = Mathf.Clamp01(1f - (distance / minDistance));
            return maxIntensity * t * t;
        }

        private void TriggerHaptics(float intensity)
        {
            if (intensity < 0.01f) return;
            SendImpulse(leftHapticAction, intensity);
            SendImpulse(rightHapticAction, intensity);
        }

        private void SendImpulse(InputActionReference actionRef, float intensity)
        {
            if (actionRef == null) return;
            var channel = hapticManager.GetChannelGroup(actionRef.action)?.GetChannel();
            channel?.SendHapticImpulse(intensity, 0.1f);
        }
    }
}
