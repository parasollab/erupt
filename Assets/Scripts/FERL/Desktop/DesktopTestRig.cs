using UnityEngine;
using UnityEngine.XR;

// Turns the mouse/keyboard test controls on when no headset is driving the scene (editor
// play mode without a device) and leaves them off on the Quest, so VR behaviour is unchanged.
[DefaultExecutionOrder(-50)]
public class DesktopTestRig : MonoBehaviour
{
    [Tooltip("Force the desktop controls on even when an HMD is active.")]
    [SerializeField] private bool forceEnable = false;
    [SerializeField] private bool enableWhenNoHmd = true;
    [Tooltip("Components (fly camera, mouse draggers, keyboard shortcuts) that start disabled and are switched on here.")]
    [SerializeField] private Behaviour[] desktopComponents;
    [SerializeField] private bool hideRobotRays = true;
    [Tooltip("Components that only make sense with a headset (e.g. SpawnHuman) and are switched off in desktop mode.")]
    [SerializeField] private Behaviour[] hmdOnlyComponents;
    [Tooltip("Seconds to keep re-checking for a headset that initializes late; desktop mode is switched off if one appears.")]
    [SerializeField] private float recheckSeconds = 3f;

    public bool IsActive { get; private set; }

    private void Awake()
    {
        // Awake, before any Start: SpawnHuman starts its coroutine in Start, and a disabled
        // component never runs Start, so this is the last moment to keep it quiet.
        Apply(forceEnable || (enableWhenNoHmd && !HmdActive()));
        if (IsActive)
        {
            Debug.Log("[DesktopTestRig] active (no HMD): WASD/QE + right-mouse to fly, left-click to select/drag objects, " +
                      "drag the end-effector handle with the mouse, arrow keys jog a selected joint; see FERLKeyboardShortcuts for keys.");
            if (!forceEnable && recheckSeconds > 0f)
                StartCoroutine(RecheckForHmd());
        }
    }

    private static bool HmdActive()
    {
        return XRSettings.isDeviceActive || !string.IsNullOrEmpty(XRSettings.loadedDeviceName);
    }

    private void Apply(bool active)
    {
        IsActive = active;
        foreach (Behaviour component in desktopComponents)
            if (component != null)
                component.enabled = active;
        foreach (Behaviour component in hmdOnlyComponents)
            if (component != null)
                component.enabled = !active;
        if (hideRobotRays)
        {
            foreach (Quest3ControllerRayInteractor ray in FindObjectsByType<Quest3ControllerRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                LineRenderer line = ray.GetComponent<LineRenderer>();
                if (line != null)
                    line.enabled = !active;
                ray.enabled = !active;
            }
        }
    }

    private System.Collections.IEnumerator RecheckForHmd()
    {
        float deadline = Time.unscaledTime + recheckSeconds;
        while (Time.unscaledTime < deadline)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (HmdActive())
            {
                Debug.Log("[DesktopTestRig] headset became active; desktop controls switched off.");
                Apply(false);
                yield break;
            }
        }
    }
}
