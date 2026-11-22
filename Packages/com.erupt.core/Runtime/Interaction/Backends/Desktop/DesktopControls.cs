using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Makes a scene usable with mouse and keyboard when no headset drives the view: it moves
    /// the main camera (a view of the robots at the start, then W A S D and the arrow keys),
    /// keeps tier 1 at the top of the view instead of on a wrist that is not there, opens tier 3
    /// square in front of the camera, and switches the <see cref="DesktopMouseBackend"/> on.
    /// It sits on the ERUPT root, so a scene needs nothing extra.
    /// </summary>
    /// <remarks>
    /// <para><b>Standing down.</b> The controls run only while no XR head device is present:
    /// no XR display is running (<see cref="XRSettings.isDeviceActive"/>) and the Input System
    /// has no head-mounted display (<see cref="XRHMD"/>, which is also what the XR simulators
    /// add). In a headset build, and in the editor while a headset or a simulator drives the
    /// view, they do nothing: the mouse backend is suppressed (no second ray next to the
    /// controllers'), the camera is left to its tracked pose driver, tier 1 stays on the wrist
    /// and tier 3 opens in front of the head. The check runs every frame, so the controls hand
    /// over when a headset appears and take over again when it goes.</para>
    /// <para>While they run, the camera's tracked pose driver is switched off: without a head
    /// device it pins the camera to the rig's origin every frame.</para>
    /// <para>The keys are serialized fields. The defaults stay clear of the editor's Game view
    /// shortcuts (all of which use Ctrl or Cmd) and of the keys the UI input module acts on
    /// (Enter, Space, Escape); the scenes' XR UI input module takes no keyboard navigation, so
    /// W A S D and the arrow keys never move a UI focus.</para>
    /// </remarks>
    [DefaultExecutionOrder(-40)]
    [DisallowMultipleComponent]
    public class DesktopControls : MonoBehaviour
    {
        [Header("Switch")]
        [Tooltip("Off: the scene is driven by a headset only; the mouse and the keyboard do nothing.")]
        [SerializeField] private bool desktopControls = true;

        [Tooltip("Show the keys in the Game view and log them once when the desktop controls start.")]
        [SerializeField] private bool showHint = true;

        [Header("Parts (found when empty)")]
        [SerializeField] private DesktopMouseBackend mouseBackend;
        [SerializeField] private TierUiRig ui;
        [SerializeField] private InteractionRouter router;

        [Header("View at the start")]
        [Tooltip("Start with the robots in view, seen from the side the scene's rig stands on. Off leaves the camera where the rig has it.")]
        [SerializeField] private bool frameRobotsAtStart = true;
        [Tooltip("Distance from the robots' end effectors to the camera, in metres (more when there are several robots).")]
        [SerializeField, Min(0.3f)] private float viewDistance = 1.4f;
        [Tooltip("How far above the end effectors the camera looks down from, in degrees.")]
        [SerializeField, Range(0f, 80f)] private float viewElevation = 18f;
        [Tooltip("The camera looks at a point this far above the end effectors, in metres: the arm below them stays in view and there is room for the menu that opens above a selected end effector.")]
        [SerializeField] private float lookAboveTips = 0.15f;

        [Header("Moving the view")]
        [SerializeField, Min(0f)] private float moveSpeed = 1.2f;
        [SerializeField, Min(1f)] private float fastMultiplier = 3f;
        [SerializeField, Min(0f)] private float lookDegreesPerSecond = 70f;
        [Tooltip("Degrees per pixel of mouse movement while the middle button is held.")]
        [SerializeField, Min(0f)] private float mouseLookDegreesPerPixel = 0.15f;

        [Header("Keys")]
        [SerializeField] private Key forwardKey = Key.W;
        [SerializeField] private Key backKey = Key.S;
        [SerializeField] private Key leftKey = Key.A;
        [SerializeField] private Key rightKey = Key.D;
        [SerializeField] private Key upKey = Key.E;
        [SerializeField] private Key downKey = Key.Q;
        [SerializeField] private Key fastKey = Key.LeftShift;
        [SerializeField] private Key lookLeftKey = Key.LeftArrow;
        [SerializeField] private Key lookRightKey = Key.RightArrow;
        [SerializeField] private Key lookUpKey = Key.UpArrow;
        [SerializeField] private Key lookDownKey = Key.DownArrow;
        [Tooltip("Back to the view of the robots.")]
        [SerializeField] private Key frameKey = Key.F;
        [Tooltip("Shows and hides the hint.")]
        [SerializeField] private Key hintKey = Key.H;

        [Header("Tier UI in the view")]
        [Tooltip("Where the centre of the tier 1 bar sits in the view (0..1 across, 0..1 up).")]
        [SerializeField] private Vector2 tierOneViewport = new(0.5f, 0.93f);
        [Tooltip("Height of the tier 1 bar as a fraction of the view's height.")]
        [SerializeField, Range(0.02f, 0.3f)] private float tierOneHeight = 0.07f;
        [Tooltip("Distance of the tier 1 bar from the camera, in metres. Close, so nothing in the scene comes in front of it.")]
        [SerializeField, Min(0.05f)] private float tierOneDistance = 0.3f;
        [Tooltip("The tier 3 panel opens at the distance where it fills this fraction of the view.")]
        [SerializeField, Range(0.2f, 1f)] private float panelViewFraction = 0.75f;
        [Tooltip("A tier 2 menu further from the camera than this (metres) is drawn larger, so it reads as it does from this distance. Zero draws it at its own size.")]
        [SerializeField, Min(0f)] private float menuReadableDistance = 1.3f;

        private Camera view;
        private float yaw, pitch;
        private Vector3 position;
        private bool framePending;
        private float frameDeadline;
        private readonly List<TrackedPoseDriver> pausedDrivers = new();
        private readonly List<Vector3> probePoints = new();

        private Transform tierOne;
        private Transform tierOneHome;
        private Vector3 tierOneHomePosition, tierOneHomeScale;
        private Quaternion tierOneHomeRotation;

        private DesktopHint hint;
        private bool keysLogged;

        /// <summary>True while the desktop controls drive the view (no XR head device is present).</summary>
        public bool IsActive { get; private set; }

        /// <summary>The serialized switch; off keeps the mouse and the keyboard out of the scene altogether.</summary>
        public bool Enabled
        {
            get => desktopControls;
            set => desktopControls = value;
        }

        /// <summary>Whether the keys are shown in the Game view and logged when the controls start.</summary>
        public bool ShowsHint
        {
            get => showHint;
            set
            {
                showHint = value;
                if (!value) SetHintVisible(false);
            }
        }

        /// <summary>The camera the controls move; null while they stand down.</summary>
        public Camera View => IsActive ? view : null;

        /// <summary>The mouse backend the controls switch on and off.</summary>
        public DesktopMouseBackend MouseBackend => mouseBackend;

        /// <summary>
        /// True when an XR head device is there to drive the view: an XR display is running, or
        /// the Input System has a head-mounted display (a headset, or an XR simulator's).
        /// </summary>
        public static bool XrHeadPresent()
        {
            if (XRSettings.isDeviceActive) return true;
            var devices = InputSystem.devices;
            for (int i = 0; i < devices.Count; i++)
                if (devices[i] is XRHMD && devices[i].added) return true;
            return false;
        }

        private void Awake()
        {
            if (mouseBackend == null) mouseBackend = GetComponent<DesktopMouseBackend>();
            if (mouseBackend == null) mouseBackend = FindFirstObjectByType<DesktopMouseBackend>(FindObjectsInactive.Include);
            if (ui == null) ui = GetComponent<TierUiRig>();
            // Nothing from the mouse until the first Update has decided who drives the view.
            if (mouseBackend != null) mouseBackend.Suppressed = true;
        }

        private void OnDisable()
        {
            if (IsActive) Leave();
        }

        private void Update()
        {
            bool wanted = desktopControls && Mouse.current != null && !XrHeadPresent();
            if (wanted && !IsActive)
            {
                Camera cam = Camera.main;
                if (cam == null) return;   // the rig's camera is not there yet
                Enter(cam);
            }
            else if (IsActive && (!wanted || view == null))
            {
                Leave();
            }
            if (!IsActive) return;

            if (framePending)
            {
                // The robots report their tips through the router once they are up, and take
                // their initial pose a moment later: the view is chosen when they stand still.
                if (TipsHaveSettled() || Time.unscaledTime > frameDeadline)
                {
                    FrameRobots();
                    framePending = false;
                }
            }

            ReadKeys();
            view.transform.SetPositionAndRotation(position, Rotation);
            PlaceTierOne();
            if (ui != null && ui.TierTwo != null)
            {
                // A flat view cannot step closer to a menu or look round what is in front of it.
                ui.TierTwo.ReadableDistance = menuReadableDistance;
                ui.TierTwo.ClearsLineOfSight = true;
            }
        }

        private Quaternion Rotation => Quaternion.Euler(-pitch, yaw, 0f);

        // ---- taking over and handing back -------------------------------------------------------

        private void Enter(Camera cam)
        {
            IsActive = true;
            view = cam;

            // Without a head device the pose driver writes the rig's origin every frame.
            pausedDrivers.Clear();
            foreach (TrackedPoseDriver driver in cam.GetComponents<TrackedPoseDriver>())
            {
                if (!driver.enabled) continue;
                driver.enabled = false;
                pausedDrivers.Add(driver);
            }

            Adopt(cam.transform.position, cam.transform.rotation);
            framePending = frameRobotsAtStart;
            frameDeadline = Time.unscaledTime + 2f;
            lastTips.Clear();
            tipsStillSince = -1f;

            if (mouseBackend != null) mouseBackend.Suppressed = false;
            if (ui == null) ui = FindFirstObjectByType<TierUiRig>();
            if (ui != null) ui.UseSummonPlacement(PanelPlacement);
            TakeTierOne();

            if (showHint)
            {
                SetHintVisible(true);
                if (!keysLogged)
                {
                    keysLogged = true;
                    Debug.Log("[ERUPT] No headset: desktop controls are on. " + ControlsSummary.Replace("\n", "  |  ") +
                              "  (Turn the hint off with 'Show Hint' on the ERUPT root's Desktop Controls.)");
                }
            }
        }

        private void Leave()
        {
            IsActive = false;
            if (mouseBackend != null) mouseBackend.Suppressed = true;
            if (ui != null) ui.UseSummonPlacement(null);
            if (ui != null && ui.TierTwo != null)
            {
                ui.TierTwo.ReadableDistance = 0f;
                ui.TierTwo.ClearsLineOfSight = false;
            }
            ReturnTierOne();
            SetHintVisible(false);

            foreach (TrackedPoseDriver driver in pausedDrivers)
                if (driver != null) driver.enabled = true;
            pausedDrivers.Clear();
            view = null;
        }

        private void Adopt(Vector3 worldPosition, Quaternion worldRotation)
        {
            position = worldPosition;
            Vector3 forward = worldRotation * Vector3.forward;
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>Put the view somewhere (a tool, a test); the keys carry on from there.</summary>
        public void SetView(Vector3 worldPosition, Quaternion worldRotation)
        {
            framePending = false;
            Adopt(worldPosition, worldRotation);
            if (IsActive && view != null) view.transform.SetPositionAndRotation(position, Rotation);
        }

        // ---- the view ---------------------------------------------------------------------------

        /// <summary>
        /// Look at the robots: the camera goes to the side of them the scene's rig stands on
        /// (or round them from there, to where their end effectors are not behind their own
        /// arms), far enough to see every end effector, a little above them. False when the
        /// scene has no robot to look at (the view then stays where it is).
        /// </summary>
        public bool FrameRobots()
        {
            if (view == null) return false;
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (router == null) return false;

            probePoints.Clear();
            foreach (IProximitySource source in router.ProximitySources) source.GetProbePoints(probePoints);
            if (probePoints.Count == 0) return false;

            var bounds = new Bounds(probePoints[0], Vector3.zero);
            foreach (Vector3 point in probePoints) bounds.Encapsulate(point);

            // The side the user would stand on: where the scene's author put the rig.
            Vector3 rigSide = position - bounds.center;
            rigSide.y = 0f;
            if (rigSide.sqrMagnitude < 0.01f) rigSide = -Vector3.ProjectOnPlane(Rotation * Vector3.forward, Vector3.up);
            if (rigSide.sqrMagnitude < 1e-6f) rigSide = Vector3.back;
            rigSide.Normalize();

            float distance = viewDistance + bounds.extents.magnitude * 1.3f;
            float elevation = viewElevation * Mathf.Deg2Rad;

            // From the rig's side when the end effectors can be seen from there; a robot that
            // faces away has its own arm in front of its end effector, so the view goes round
            // it, a step at a time to either side, to where most of them are in the clear.
            // Nor does the camera start inside something or with its nose against it (a wall,
            // a panel that hangs in the room).
            Physics.SyncTransforms();
            Vector3 eye = Vector3.zero;
            int best = int.MinValue;
            foreach (float turn in ViewTurns)
            {
                Vector3 side = Quaternion.AngleAxis(turn, Vector3.up) * rigSide;
                Vector3 candidate = bounds.center + side * (distance * Mathf.Cos(elevation)) + Vector3.up * (distance * Mathf.Sin(elevation));
                int seen = 0;
                foreach (Vector3 point in probePoints)
                    if (InTheClear(candidate, point)) seen++;
                bool crowded = Physics.CheckSphere(candidate, EyeClearance, ~0, QueryTriggerInteraction.Collide);
                int score = seen - (crowded ? probePoints.Count + 1 : 0);
                if (score <= best) continue;
                best = score;
                eye = candidate;
                if (score == probePoints.Count) break;
            }

            Vector3 target = bounds.center + Vector3.up * lookAboveTips;
            Adopt(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            view.transform.SetPositionAndRotation(position, Rotation);
            return true;
        }

        private readonly List<Vector3> lastTips = new();
        private float tipsStillSince = -1f;
        private const float SettleSeconds = 0.15f;

        // True once there are end effectors to look at and none of them has moved for a moment.
        private bool TipsHaveSettled()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            if (router == null) return false;
            probePoints.Clear();
            foreach (IProximitySource source in router.ProximitySources) source.GetProbePoints(probePoints);

            bool still = probePoints.Count > 0 && probePoints.Count == lastTips.Count;
            for (int i = 0; still && i < probePoints.Count; i++)
                still = (probePoints[i] - lastTips[i]).sqrMagnitude < 1e-6f;
            lastTips.Clear();
            lastTips.AddRange(probePoints);
            if (!still)
            {
                tipsStillSince = -1f;
                return false;
            }
            if (tipsStillSince < 0f) tipsStillSince = Time.unscaledTime;
            return Time.unscaledTime - tipsStillSince >= SettleSeconds;
        }

        // Degrees about the vertical, from the rig's side outwards.
        private static readonly float[] ViewTurns = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };

        // The camera does not start with anything (solid or a panel) this close to it, in metres.
        private const float EyeClearance = 0.5f;

        // How close to the point something solid may be and still not be "in front of it"
        // (an end effector sits in or on the robot's last link).
        private const float ClearanceAtPoint = 0.12f;

        private static bool InTheClear(Vector3 eye, Vector3 point)
        {
            Vector3 to = point - eye;
            float length = to.magnitude - ClearanceAtPoint;
            return length <= 0f || !Physics.Raycast(eye, to.normalized, length, ~0, QueryTriggerInteraction.Ignore);
        }

        private void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            float dt = Time.unscaledDeltaTime;

            Vector2 look = Vector2.zero;
            Vector3 move = Vector3.zero;
            bool fast = false;
            if (keyboard != null)
            {
                look.x = Axis(keyboard, lookRightKey, lookLeftKey) * lookDegreesPerSecond * dt;
                look.y = Axis(keyboard, lookUpKey, lookDownKey) * lookDegreesPerSecond * dt;
                move = new Vector3(Axis(keyboard, rightKey, leftKey), Axis(keyboard, upKey, downKey), Axis(keyboard, forwardKey, backKey));
                fast = Pressed(keyboard, fastKey);

                if (PressedThisFrame(keyboard, frameKey)) FrameRobots();
                if (PressedThisFrame(keyboard, hintKey)) SetHintVisible(hint == null);
            }
            if (mouse != null && mouse.middleButton.isPressed)
                look += mouse.delta.ReadValue() * mouseLookDegreesPerPixel;

            yaw = Mathf.Repeat(yaw + look.x + 180f, 360f) - 180f;
            pitch = Mathf.Clamp(pitch + look.y, -89f, 89f);

            if (move != Vector3.zero)
            {
                float speed = moveSpeed * (fast ? fastMultiplier : 1f);
                // Forward and sideways follow the view; up and down are the world's.
                Vector3 step = Rotation * new Vector3(move.x, 0f, move.z) + Vector3.up * move.y;
                position += step * (speed * dt);
            }
        }

        private static float Axis(Keyboard keyboard, Key positive, Key negative) =>
            (Pressed(keyboard, positive) ? 1f : 0f) - (Pressed(keyboard, negative) ? 1f : 0f);

        private static bool Pressed(Keyboard keyboard, Key key) => key != Key.None && keyboard[key].isPressed;

        private static bool PressedThisFrame(Keyboard keyboard, Key key) => key != Key.None && keyboard[key].wasPressedThisFrame;

        // ---- tier 1: at the top of the view -----------------------------------------------------

        private void TakeTierOne()
        {
            if (tierOne != null || ui == null || ui.TierOne == null) return;
            tierOne = ui.TierOne.transform;
            tierOneHome = tierOne.parent;
            tierOneHomePosition = tierOne.localPosition;
            tierOneHomeRotation = tierOne.localRotation;
            tierOneHomeScale = tierOne.localScale;
            tierOne.SetParent(view.transform, false);
            PlaceTierOne();
        }

        private void ReturnTierOne()
        {
            if (tierOne == null) return;
            // Back onto the wrist anchor; when that went with its scene, the bar goes with it,
            // and nothing is re-parented while the scene itself is on its way out.
            if (tierOneHome != null && gameObject.scene.isLoaded)
            {
                tierOne.SetParent(tierOneHome, false);
                tierOne.localPosition = tierOneHomePosition;
                tierOne.localRotation = tierOneHomeRotation;
                tierOne.localScale = tierOneHomeScale;
            }
            tierOne = null;
            tierOneHome = null;
        }

        private void PlaceTierOne()
        {
            if (tierOne == null)
            {
                // The rig builds its tiers in Awake; one that woke after this component is taken now.
                TakeTierOne();
                if (tierOne == null) return;
            }
            if (ui == null || ui.TierOne == null || ui.TierOne.Canvas == null) return;

            Vector2 size = ui.TierOne.Canvas.GetComponent<RectTransform>().sizeDelta / UiBuilder.PixelsPerUnit;
            if (size.x <= 0f || size.y <= 0f) return;

            // Close to the camera, but clear of its near plane.
            float distance = Mathf.Max(tierOneDistance, view.nearClipPlane * 1.5f);
            float viewHeight = 2f * distance * Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewWidth = viewHeight * view.aspect;
            // As tall as asked, but never wider than the view.
            float scale = Mathf.Min(tierOneHeight * viewHeight / size.y, 0.9f * viewWidth / size.x);

            tierOne.localPosition = new Vector3((tierOneViewport.x - 0.5f) * viewWidth, (tierOneViewport.y - 0.5f) * viewHeight, distance);
            tierOne.localRotation = Quaternion.identity;
            tierOne.localScale = Vector3.one * scale;
        }

        // ---- tier 3: square in front of the camera ----------------------------------------------

        // The default places the panel level with the horizon, for a head that looks down at a
        // table. A desktop camera pitched down would have most of that panel above its view.
        private Pose? PanelPlacement()
        {
            if (!IsActive || view == null || ui == null || ui.TierThree == null || ui.TierThree.Canvas == null) return null;
            Vector2 size = ui.TierThree.Canvas.GetComponent<RectTransform>().sizeDelta / UiBuilder.PixelsPerUnit;
            float heightAtOne = 2f * Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float widthAtOne = heightAtOne * view.aspect;
            float distance = Mathf.Max(size.y / (panelViewFraction * heightAtOne), size.x / (panelViewFraction * widthAtOne));
            distance = Mathf.Max(distance, Mathf.Max(tierOneDistance, view.nearClipPlane * 1.5f) + 0.1f);
            Transform cam = view.transform;
            return new Pose(cam.position + cam.forward * distance, Quaternion.LookRotation(cam.forward, cam.up));
        }

        // ---- the hint ---------------------------------------------------------------------------

        /// <summary>The controls in words, one line per group, with the keys as they are set.</summary>
        public string ControlsSummary
        {
            get
            {
                Key panel = mouseBackend != null ? mouseBackend.PanelKey : Key.Tab;
                return "Left click: select, press buttons    Right-drag: move the arm or a selected object (scroll: nearer / farther)\n" +
                       $"{Name(forwardKey)} {Name(leftKey)} {Name(backKey)} {Name(rightKey)}: move    {Name(downKey)} / {Name(upKey)}: down / up    " +
                       $"{Name(fastKey)}: faster    Arrow keys or middle-drag: look\n" +
                       $"{Name(panel)} or the Menu button at the top: panel    {Name(frameKey)}: view the robots    Scroll: turn the selected joint    {Name(hintKey)}: hide this";
            }
        }

        private static string Name(Key key) => key switch
        {
            Key.LeftShift => "Shift",
            Key.RightShift => "Right Shift",
            Key.None => "(none)",
            _ => key.ToString()
        };

        private void SetHintVisible(bool visible)
        {
            if (visible && hint == null && IsActive)
            {
                hint = gameObject.AddComponent<DesktopHint>();
                hint.Text = ControlsSummary;
            }
            else if (!visible && hint != null)
            {
                Destroy(hint);
                hint = null;
            }
        }

        /// <summary>Whether the hint is on screen.</summary>
        public bool HintVisible => hint != null;
    }
}
