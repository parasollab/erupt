using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using Erupt.Ui;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Proximity haptics: pulses both controllers as a robot's tip approaches a collider that
    /// is not part of a robot. Haptics are a backend capability (<see cref="Capability.Haptics"/>),
    /// so this lives with the OpenXR backend. What to measure from comes from the interaction
    /// router (<see cref="InteractionRouter.ProximitySources"/>): every robot in the scene
    /// offers its tip there and says which colliders are its own, so no robot has to be
    /// assigned here.
    /// </summary>
    /// <remarks>
    /// The user changes three things while using the app, on the tier 3 Settings tab: whether
    /// the controllers pulse at all (<see cref="HapticsEnabled"/>), how strongly
    /// (<see cref="Strength"/>) and from how far (<see cref="Distance"/>). The component adds
    /// its own section to that tab when the scene has a tier UI, so no scene wires anything
    /// and a scene without this component has no such controls. The serialized values are
    /// the defaults; what the user sets is kept between sessions (<see cref="UserSettings"/>)
    /// and applies from the next frame.
    /// </remarks>
    public class CollisionHaptics : MonoBehaviour
    {
        /// <summary>The range of <see cref="Distance"/>, in metres.</summary>
        public const float NearestDistance = 0.05f, FarthestDistance = 0.5f;

        /// <summary>The keys the user's settings are kept under (<see cref="UserSettings"/>).</summary>
        public const string EnabledKey = "erupt.collisionHaptics.on",
                            StrengthKey = "erupt.collisionHaptics.strength",
                            DistanceKey = "erupt.collisionHaptics.distance";

        /// <summary>The id of the section on the Settings tab.</summary>
        public const string SettingsSection = "collision-haptics";

        [Header("Defaults (the user changes these on the Settings tab)")]
        [Tooltip("Whether the controllers pulse at all.")]
        [SerializeField] private bool hapticsOn = true;
        [Tooltip("Strength of the pulse at contact, 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float maxIntensity = 1.0f;
        [Tooltip("How close a robot's tip has to come to an obstacle before the controllers pulse, in metres.")]
        [SerializeField, Range(NearestDistance, FarthestDistance)] private float minDistance = 0.3f;

        [Header("Settings tab")]
        [Tooltip("Add the switch and the two sliders to the tier 3 Settings tab.")]
        [SerializeField] private bool showInSettings = true;
        [Tooltip("Keep what the user sets between sessions. Off: every session starts from the values above.")]
        [SerializeField] private bool rememberSettings = true;
        [Tooltip("The tier UI whose Settings tab gets the controls. Found in the scene if empty.")]
        [SerializeField] private TierUiRig ui;

        [SerializeField] private InputActionReference leftHapticAction;
        [SerializeField] private InputActionReference rightHapticAction;

        [Tooltip("Found in the scene if empty. The robots' tips are read from it.")]
        [SerializeField] private InteractionRouter router;

        [Tooltip("Optional extra point to check proximity from, besides the robots' tips.")]
        [SerializeField] private Transform tipLink;

        [Tooltip("Additional objects to exclude from obstacle detection (e.g. the target sphere).")]
        [SerializeField] private Transform[] excludeTransforms;

        [Tooltip("Optional root whose colliders are never obstacles, besides the robots' own.")]
        [SerializeField] private Transform robotRoot;

        [SerializeField] private LayerMask obstacleLayerMask = ~0;

        [SerializeField] private bool debugMode = false;

        private HapticControlActionManager hapticManager;
        private readonly Collider[] overlapBuffer = new Collider[32];
        private readonly List<Vector3> probePoints = new();
        private float debugLogTimer;

        /// <summary>The closest obstacle distance found by the last update; infinity when nothing is near.</summary>
        public float LastClosestDistance { get; private set; } = float.PositiveInfinity;

        /// <summary>The strength of the pulse the last update asked for, 0 to 1; zero when it asked for none.</summary>
        public float LastIntensity { get; private set; }

        /// <summary>Raised when the switch, the strength or the distance changes.</summary>
        public event Action SettingsChanged;

        /// <summary>Whether the controllers pulse at all. Applies from the next frame and is remembered.</summary>
        public bool HapticsEnabled
        {
            get => hapticsOn;
            set
            {
                if (hapticsOn == value) return;
                hapticsOn = value;
                if (rememberSettings) UserSettings.SetBool(EnabledKey, value);
                RaiseSettingsChanged();
            }
        }

        /// <summary>The strength of the pulse at contact, 0 to 1. Applies from the next frame and is remembered.</summary>
        public float Strength
        {
            get => maxIntensity;
            set
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Approximately(maxIntensity, value)) return;
                maxIntensity = value;
                if (rememberSettings) UserSettings.SetFloat(StrengthKey, value);
                RaiseSettingsChanged();
            }
        }

        /// <summary>
        /// How close a robot's tip has to come to an obstacle before the controllers pulse, in
        /// metres (<see cref="NearestDistance"/> to <see cref="FarthestDistance"/>). Applies
        /// from the next frame and is remembered.
        /// </summary>
        public float Distance
        {
            get => minDistance;
            set
            {
                value = Mathf.Clamp(value, NearestDistance, FarthestDistance);
                if (Mathf.Approximately(minDistance, value)) return;
                minDistance = value;
                if (rememberSettings) UserSettings.SetFloat(DistanceKey, value);
                RaiseSettingsChanged();
            }
        }

        /// <summary>The pulse for an obstacle at a distance, with the settings as they are: 0 at <see cref="Distance"/> and beyond, <see cref="Strength"/> at contact, nothing while switched off.</summary>
        public float IntensityAt(float distance) =>
            hapticsOn && distance < minDistance ? CalculateIntensity(distance) : 0f;

        /// <summary>Read the robots' tips from this router.</summary>
        public void SetRouter(InteractionRouter target) => router = target;

        /// <summary>Add a point and a root of its own, besides the robots the router knows.</summary>
        public void SetRobot(Transform root, Transform tip)
        {
            robotRoot = root;
            tipLink = tip;
        }

        private void Awake()
        {
            // What the user set in an earlier session; the serialized values otherwise.
            if (!rememberSettings) return;
            hapticsOn = UserSettings.GetBool(EnabledKey, hapticsOn);
            maxIntensity = Mathf.Clamp01(UserSettings.GetFloat(StrengthKey, maxIntensity));
            minDistance = Mathf.Clamp(UserSettings.GetFloat(DistanceKey, minDistance), NearestDistance, FarthestDistance);
        }

        private void OnEnable()
        {
            hapticManager = new HapticControlActionManager();
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
        }

        private void Start()
        {
            if (!showInSettings) return;
            if (ui == null) ui = FindFirstObjectByType<TierUiRig>();
            lookForSettings = ui != null;
            TryAddSettings();
        }

        // Written out when the app goes to the background: a headset app is put away, not quit.
        private void OnDisable() { if (rememberSettings) UserSettings.Save(); }
        private void OnApplicationPause(bool paused) { if (paused && rememberSettings) UserSettings.Save(); }

        private void OnDestroy()
        {
            SettingsChanged = null;
            if (settings != null) settings.RemoveSection(SettingsSection);
            settings = null;
        }

        private void Update()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (lookForSettings) TryAddSettings();

            LastIntensity = 0f;
            if (!hapticsOn)
            {
                LastClosestDistance = float.PositiveInfinity;
                return;
            }

            float closestDistance = FindClosestObstacleDistance();
            LastClosestDistance = closestDistance;
            if (closestDistance >= minDistance) return;

            TriggerHaptics(CalculateIntensity(closestDistance));
        }

        // ---- the Settings tab -------------------------------------------------------------------

        private SettingsTab settings;
        private bool lookForSettings;
        private Button switchButton;
        private Slider strengthSlider, distanceSlider;

        /// <summary>
        /// Put the switch and the two sliders on a tier UI's Settings tab (the component does
        /// this by itself for the tier UI in its scene).
        /// </summary>
        public void ShowInSettings(TierUiRig rig)
        {
            if (settings != null) settings.RemoveSection(SettingsSection);
            settings = null;
            ui = rig;
            lookForSettings = rig != null;
            TryAddSettings();
        }

        // The rig builds its tiers when it wakes, which may be after this component started.
        private void TryAddSettings()
        {
            if (ui == null) { lookForSettings = false; return; }
            if (ui.Settings == null) return;
            lookForSettings = false;
            settings = ui.Settings;
            settings.AddSection(SettingsSection, BuildSettings);
        }

        /// <summary>The texts on the Settings tab.</summary>
        public const string SwitchLabel = "Collision haptics", StrengthLabel = "Strength", DistanceLabel = "Distance";

        private void BuildSettings(Transform column)
        {
            switchButton = UiBuilder.CreateButton("CollisionHaptics", column, "", new Vector2(300f, 56f),
                () => HapticsEnabled = !HapticsEnabled);
            strengthSlider = UiBuilder.CreateSlider("CollisionHapticsStrength", column, StrengthLabel, 0f, 1f, Strength,
                new Vector2(300f, 76f), value => Strength = value, value => $"{Mathf.RoundToInt(value * 100f)}%", 0.05f);
            distanceSlider = UiBuilder.CreateSlider("CollisionHapticsDistance", column, DistanceLabel, NearestDistance, FarthestDistance, Distance,
                new Vector2(300f, 76f), value => Distance = value, value => $"{Mathf.RoundToInt(value * 100f)} cm", 0.01f);
            var hint = UiBuilder.CreateLabel("CollisionHapticsHint", column,
                "The controllers pulse when a robot's end effector comes this close to an obstacle.", 18f);
            hint.enableWordWrapping = true;
            RefreshSettings();
        }

        private void RaiseSettingsChanged()
        {
            RefreshSettings();
            SettingsChanged?.Invoke();
        }

        // The controls show the values as they are, whoever changed them.
        private void RefreshSettings()
        {
            if (switchButton != null) UiBuilder.SetButtonText(switchButton, $"{SwitchLabel}: {(hapticsOn ? "on" : "off")}");
            if (strengthSlider != null)
            {
                if (!Mathf.Approximately(strengthSlider.value, maxIntensity)) strengthSlider.value = maxIntensity;
                UiBuilder.SetInteractable(strengthSlider, hapticsOn);
            }
            if (distanceSlider != null)
            {
                if (!Mathf.Approximately(distanceSlider.value, minDistance)) distanceSlider.value = minDistance;
                UiBuilder.SetInteractable(distanceSlider, hapticsOn);
            }
        }

        /// <summary>The distance from the nearest probe point (any robot's tip) to the nearest obstacle within range; infinity when there is none.</summary>
        public float FindClosestObstacleDistance()
        {
            probePoints.Clear();
            if (router != null)
                foreach (IProximitySource source in router.ProximitySources)
                    source.GetProbePoints(probePoints);
            if (tipLink != null) probePoints.Add(tipLink.position);

            float closest = float.MaxValue;
            Collider closestCollider = null;

            foreach (Vector3 point in probePoints)
            {
                int count = Physics.OverlapSphereNonAlloc(
                    point, minDistance, overlapBuffer,
                    obstacleLayerMask, QueryTriggerInteraction.Ignore);

                for (int i = 0; i < count; i++)
                {
                    Collider col = overlapBuffer[i];
                    if (IsNotAnObstacle(col)) continue;

                    Vector3 surfacePoint = col.ClosestPoint(point);
                    float dist = Vector3.Distance(point, surfacePoint);
                    if (dist < closest)
                    {
                        closest = dist;
                        closestCollider = col;
                    }
                }
            }

            if (debugMode && closest < minDistance)
            {
                debugLogTimer += Time.deltaTime;
                if (debugLogTimer >= 1f)
                {
                    debugLogTimer = 0f;
                    Debug.Log($"[CollisionHaptics] Closest obstacle: '{closestCollider?.gameObject.name}' " +
                              $"(path='{GetPath(closestCollider?.transform)}') dist={closest:F3}m");
                }
            }

            return closest;
        }

        // Any robot's own body, and whatever was excluded by hand, is not an obstacle.
        private bool IsNotAnObstacle(Collider col)
        {
            if (router != null)
                foreach (IProximitySource source in router.ProximitySources)
                    if (source.IsOwnCollider(col)) return true;

            Transform t = col.transform;
            if (robotRoot != null && (t == robotRoot || t.IsChildOf(robotRoot))) return true;
            if (excludeTransforms != null)
                foreach (Transform excluded in excludeTransforms)
                    if (excluded != null && (t == excluded || t.IsChildOf(excluded))) return true;
            return false;
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
            LastIntensity = intensity;
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
