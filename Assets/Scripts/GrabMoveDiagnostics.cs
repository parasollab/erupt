using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using Unity.XR.CoreUtils;
using System.Collections.Generic;

/// <summary>
/// Temporary diagnostics. Attach to the XR Origin (XR Rig) root GameObject.
/// ADB: adb logcat -s Unity:D | grep "\[Diag\]\|\[SGC\]\|\[FarFilter\]"
/// Remove when done debugging.
/// </summary>
public class GrabMoveDiagnostics : MonoBehaviour
{
    [Header("Input actions (assign from XRI Default Input Actions)")]
    public InputActionReference rightHandMoveAction;
    public InputActionReference rightSnapTurnAction;

    [Header("Auto-found")]
    public XROrigin xrOrigin;

    private NearFarInteractor m_RightNFI;
    private Vector3 m_LastOriginPos;
    private Quaternion m_LastOriginRot;
    private float m_NextLog;
    private XRGrabInteractable m_TrackedGrab;
    private NearFarInteractor.Region m_LastRegion = NearFarInteractor.Region.None;

    // Cache of all XRGrabInteractables — rebuilt only when needed, never in Update
    private List<XRGrabInteractable> m_GrabCache;
    private float m_NextCacheRebuild;

    void Start()
    {
        if (xrOrigin == null)
            xrOrigin = GetComponentInChildren<XROrigin>() ?? FindObjectOfType<XROrigin>();

        foreach (var nfi in FindObjectsOfType<NearFarInteractor>())
        {
            var t = nfi.transform;
            bool underRight = false;
            while (t != null) { if (t.name.Contains("Right")) { underRight = true; break; } t = t.parent; }
            if (underRight) { m_RightNFI = nfi; break; }
        }

        m_GrabCache = new List<XRGrabInteractable>(FindObjectsOfType<XRGrabInteractable>());

        if (xrOrigin != null) { m_LastOriginPos = xrOrigin.transform.position; m_LastOriginRot = xrOrigin.transform.rotation; }
        Debug.Log($"[Diag] Started. XROrigin={xrOrigin?.name ?? "MISSING"}  RightNFI={m_RightNFI?.name ?? "NOT FOUND"}  GrabCache={m_GrabCache.Count}");
        Debug.Log($"[Diag] MoveAction={rightHandMoveAction?.action?.name ?? "NOT ASSIGNED"}  SnapTurnAction={rightSnapTurnAction?.action?.name ?? "NOT ASSIGNED"}");
    }

    void Update()
    {
        // Rebuild grab cache every 10 seconds (FindObjectsOfType is expensive)
        if (Time.time >= m_NextCacheRebuild)
        {
            m_NextCacheRebuild = Time.time + 10f;
            m_GrabCache.Clear();
            m_GrabCache.AddRange(FindObjectsOfType<XRGrabInteractable>());
        }

        // NearFarInteractor region changes
        if (m_RightNFI != null)
        {
            var region = m_RightNFI.selectionRegion.Value;
            if (region != m_LastRegion)
            {
                Debug.Log($"[Diag] NFI region: {m_LastRegion} → {region}  hasSelection={m_RightNFI.hasSelection}  hasOffset={m_RightNFI.interactionAttachController?.hasOffset}");
                m_LastRegion = region;
            }
        }

        // Track grabbed object (iterate cached list, not FindObjectsOfType)
        if (m_TrackedGrab == null)
        {
            foreach (var gi in m_GrabCache)
            {
                if (gi != null && gi.isSelected)
                {
                    m_TrackedGrab = gi;
                    Debug.Log($"[Diag] GRAB START: '{gi.name}'  enabled={gi.enabled}  selectMode={gi.selectMode}  region={m_RightNFI?.selectionRegion.Value}");
                    break;
                }
            }
        }
        else if (m_TrackedGrab == null || !m_TrackedGrab.isSelected)
        {
            Debug.Log($"[Diag] GRAB END: '{m_TrackedGrab?.name}'");
            m_TrackedGrab = null;
        }

        // Periodic 0.5s summary
        if (Time.time < m_NextLog) return;
        m_NextLog = Time.time + 0.5f;

        Vector2 moveVal = rightHandMoveAction != null ? rightHandMoveAction.action.ReadValue<Vector2>() : Vector2.zero;
        Vector2 turnVal = rightSnapTurnAction != null ? rightSnapTurnAction.action.ReadValue<Vector2>() : Vector2.zero;
        bool moveEnabled = rightHandMoveAction?.action?.enabled ?? false;
        bool turnEnabled = rightSnapTurnAction?.action?.enabled ?? false;

        Vector3 curPos = xrOrigin != null ? xrOrigin.transform.position : transform.position;
        Quaternion curRot = xrOrigin != null ? xrOrigin.transform.rotation : transform.rotation;
        float rotDelta = Quaternion.Angle(curRot, m_LastOriginRot);
        Vector3 posDelta = curPos - m_LastOriginPos;
        m_LastOriginPos = curPos;
        m_LastOriginRot = curRot;

        bool interesting = moveVal.magnitude > 0.1f || turnVal.magnitude > 0.1f
                           || posDelta.magnitude > 0.001f || rotDelta > 0.5f
                           || m_TrackedGrab != null;

        if (!interesting) return;

        string grabInfo = m_TrackedGrab != null ? $"'{m_TrackedGrab.name}' pos={m_TrackedGrab.transform.position:F3}" : "none";
        string nfiInfo = m_RightNFI != null
            ? $"region={m_RightNFI.selectionRegion.Value} sel={m_RightNFI.hasSelection} offset={m_RightNFI.interactionAttachController?.hasOffset}"
            : "NFI_MISSING";

        Debug.Log($"[Diag] move={moveVal:F2}(en={moveEnabled}) snap={turnVal:F2}(en={turnEnabled}) | " +
                  $"originΔ=({posDelta:F3},{rotDelta:F1}°) | {nfiInfo} | grabbed={grabInfo}");
    }
}
