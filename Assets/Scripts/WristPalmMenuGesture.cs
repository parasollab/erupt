using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

[DisallowMultipleComponent]
public sealed class WristPalmMenuGesture : MonoBehaviour
{
    public enum TrackedHand
    {
        Left,
        Right
    }

    public enum PalmNormalAxis
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ
    }

    [Header("References")]
    [SerializeField] private WristMenuController wristMenu;
    [SerializeField] private Camera headCamera;

    [Header("Hand")]
    [SerializeField]
    [Tooltip("Only this hand can reveal the wrist menu. Leave as Left for now; switch to Right later if needed.")]
    private TrackedHand menuHand = TrackedHand.Left;
    [SerializeField] private PalmNormalAxis palmNormalAxis = PalmNormalAxis.NegativeY;
    [SerializeField] private bool requireOpenPalm = false;

    [Header("Gesture")]
    [SerializeField, Range(5f, 60f)] private float gazeAngleDegrees = 28f;
    [SerializeField, Range(0f, 1f)] private float palmFacingDotThreshold = 0.65f;
    [SerializeField, Min(0f)] private float showHoldSeconds = 0.35f;
    [SerializeField, Min(0f)] private float hideHoldSeconds = 0.2f;
    [SerializeField] private bool closeWhenGestureEnds = true;
    [SerializeField] private bool onlyCloseIfOpenedByGesture = true;
    [SerializeField] private bool debugLogs = false;

    private XRHandSubsystem subsystem;
    private bool gestureHeld;
    private bool openedMenu;
    private float matchTimer;
    private float missTimer;

    private static readonly List<XRHandSubsystem> Subsystems = new();

    private void Awake()
    {
        if (wristMenu == null)
            wristMenu = GetComponent<WristMenuController>();
        if (headCamera == null)
            headCamera = Camera.main;
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        gestureHeld = false;
        openedMenu = false;
        matchTimer = 0f;
        missTimer = 0f;
    }

    private void TrySubscribe()
    {
        if (subsystem != null)
            return;

        SubsystemManager.GetSubsystems(Subsystems);
        if (Subsystems.Count == 0)
        {
            if (debugLogs)
                Debug.Log("WristPalmMenuGesture: No XRHandSubsystem available yet.", this);
            return;
        }

        subsystem = Subsystems[0];
        subsystem.updatedHands += OnUpdatedHands;
    }

    private void Unsubscribe()
    {
        if (subsystem == null)
            return;

        subsystem.updatedHands -= OnUpdatedHands;
        subsystem = null;
    }

    private void Update()
    {
        if (subsystem == null)
            TrySubscribe();
    }

    private void OnUpdatedHands(
        XRHandSubsystem updatedSubsystem,
        XRHandSubsystem.UpdateSuccessFlags updateSuccessFlags,
        XRHandSubsystem.UpdateType updateType)
    {
        if (updateType != XRHandSubsystem.UpdateType.Dynamic)
            return;

        bool hasJoints = menuHand == TrackedHand.Left
            ? HasFlag(updateSuccessFlags, XRHandSubsystem.UpdateSuccessFlags.LeftHandJoints)
            : HasFlag(updateSuccessFlags, XRHandSubsystem.UpdateSuccessFlags.RightHandJoints);

        bool matches = hasJoints && IsPalmMenuGesture(menuHand == TrackedHand.Left
            ? updatedSubsystem.leftHand
            : updatedSubsystem.rightHand);

        UpdateGestureState(matches, Time.deltaTime);
    }

    private void UpdateGestureState(bool matches, float deltaTime)
    {
        if (matches)
        {
            matchTimer += deltaTime;
            missTimer = 0f;

            if (!gestureHeld && matchTimer >= showHoldSeconds)
            {
                gestureHeld = true;
                if (wristMenu != null)
                {
                    openedMenu = !wristMenu.IsMenuVisible;
                    wristMenu.SetMenuVisibility(true);
                }
            }

            return;
        }

        matchTimer = 0f;
        missTimer += deltaTime;

        if (!gestureHeld || missTimer < hideHoldSeconds)
            return;

        gestureHeld = false;

        if (closeWhenGestureEnds && wristMenu != null && (!onlyCloseIfOpenedByGesture || openedMenu))
            wristMenu.SetMenuVisibility(false);

        openedMenu = false;
    }

    private bool IsPalmMenuGesture(XRHand xrHand)
    {
        if (wristMenu == null)
            return false;

        if (headCamera == null)
            headCamera = Camera.main;
        if (headCamera == null)
            return false;

        if (!xrHand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose))
            return false;

        Transform cameraTransform = headCamera.transform;
        Vector3 cameraToPalm = palmPose.position - cameraTransform.position;
        if (cameraToPalm.sqrMagnitude <= Mathf.Epsilon)
            return false;

        Vector3 cameraToPalmDirection = cameraToPalm.normalized;
        float gazeDot = Vector3.Dot(cameraTransform.forward, cameraToPalmDirection);
        float minGazeDot = Mathf.Cos(gazeAngleDegrees * Mathf.Deg2Rad);

        Vector3 palmNormal = GetPalmNormal(palmPose.rotation);
        Vector3 palmToCameraDirection = -cameraToPalmDirection;
        float palmFacingDot = Vector3.Dot(palmNormal, palmToCameraDirection);

        bool isMatch = gazeDot >= minGazeDot &&
            palmFacingDot >= palmFacingDotThreshold &&
            (!requireOpenPalm || IsOpenPalm(xrHand));

        if (debugLogs)
        {
            Debug.Log(
                $"WristPalmMenuGesture: match={isMatch} gazeDot={gazeDot:F2}/{minGazeDot:F2} palmDot={palmFacingDot:F2}/{palmFacingDotThreshold:F2}",
                this);
        }

        return isMatch;
    }

    private Vector3 GetPalmNormal(Quaternion palmRotation)
    {
        return palmNormalAxis switch
        {
            PalmNormalAxis.PositiveX => palmRotation * Vector3.right,
            PalmNormalAxis.NegativeX => palmRotation * Vector3.left,
            PalmNormalAxis.PositiveY => palmRotation * Vector3.up,
            PalmNormalAxis.NegativeY => palmRotation * Vector3.down,
            PalmNormalAxis.PositiveZ => palmRotation * Vector3.forward,
            PalmNormalAxis.NegativeZ => palmRotation * Vector3.back,
            _ => palmRotation * Vector3.down
        };
    }

    private static bool IsOpenPalm(XRHand xrHand)
    {
        return IsFingerExtended(xrHand, XRHandJointID.IndexTip, XRHandJointID.IndexProximal) &&
            IsFingerExtended(xrHand, XRHandJointID.MiddleTip, XRHandJointID.MiddleProximal) &&
            IsFingerExtended(xrHand, XRHandJointID.RingTip, XRHandJointID.RingProximal) &&
            IsFingerExtended(xrHand, XRHandJointID.LittleTip, XRHandJointID.LittleProximal);
    }

    private static bool IsFingerExtended(XRHand xrHand, XRHandJointID tipId, XRHandJointID proximalId)
    {
        if (!(xrHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose) &&
              xrHand.GetJoint(tipId).TryGetPose(out Pose tipPose) &&
              xrHand.GetJoint(proximalId).TryGetPose(out Pose proximalPose)))
        {
            return false;
        }

        Vector3 wristToTip = tipPose.position - wristPose.position;
        Vector3 wristToProximal = proximalPose.position - wristPose.position;
        return wristToTip.sqrMagnitude > wristToProximal.sqrMagnitude;
    }

    private static bool HasFlag(
        XRHandSubsystem.UpdateSuccessFlags successFlags,
        XRHandSubsystem.UpdateSuccessFlags successFlag)
    {
        return (successFlags & successFlag) == successFlag;
    }
}
