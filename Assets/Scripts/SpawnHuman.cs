using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using System.Collections;

public class SpawnHuman : MonoBehaviour
{
    public Transform spawnPoint;
    public XROrigin xrOrigin;

    // Resolved from the rig's InputActionManager by name rather than serialized, so no scene
    // rewiring is needed. Pressing it (left-controller Y / keyboard Y) snaps the participant
    // back to the spawn point mid-scene.
    private InputAction _resetAction;

    // The Guardian/boundary pause-resume dip below only happens once, at true app cold
    // boot. It survives scene loads (only reset by a domain reload/app restart) so later
    // scenes in a multi-scene flow don't repeat the wait before snapping to their spawn point.
    private static bool s_TrackingHasSettled = false;

    IEnumerator Start()
    {
        // Streaming content scenes deliberately do not contain their own XR Origin. Resolve the
        // persistent rig before touching it (including while looking up the reset input action).
        xrOrigin = PersistentXRInfrastructure.ResolveXROrigin(xrOrigin);
        if (xrOrigin == null)
        {
            Debug.LogError("SpawnHuman: no persistent XROrigin is available.");
            yield break;
        }

        InputActionManager actionManager =
            xrOrigin.GetComponent<InputActionManager>() ?? xrOrigin.GetComponentInChildren<InputActionManager>();
        _resetAction = actionManager != null && actionManager.actionAssets.Count > 0
            ? actionManager.actionAssets[0]?.FindActionMap("XRI Left Interaction")?.FindAction("ResetToSpawn")
            : null;
        if (_resetAction != null)
        {
            _resetAction.performed += OnResetPressed;
            _resetAction.Enable();
        }
        else
        {
            Debug.LogWarning("SpawnHuman: No 'ResetToSpawn' action found on the XR Origin's InputActionManager.");
        }

        yield return null;

        if (!s_TrackingHasSettled)
        {
            // On Quest, tracking initializes in two stages:
            //   1. Camera Floor Offset only — CameraInOriginSpacePos = (0, 1.36, 0), no real XZ
            //   2. ~1s later, after a Guardian/boundary pause-resume cycle, real 6DOF kicks in
            // We detect stage 2 by waiting for the camera to deviate from its initial local
            // position (the y=0 reset during the pause is the tell), then recover above 0.5m.
            Vector3 initialLocal = xrOrigin.CameraInOriginSpacePos;
            float deadline = Time.unscaledTime + 1.5f;

            yield return new WaitUntil(() =>
                Vector3.Distance(xrOrigin.CameraInOriginSpacePos, initialLocal) > 0.1f ||
                Time.unscaledTime >= deadline);

            yield return new WaitUntil(() => xrOrigin.CameraInOriginSpacePos.y > 0.5f);

            s_TrackingHasSettled = true;
        }

        MoveToSpawnPoint();
    }

    // Snaps the participant to the spawn point: origin yaw matched to the spawn point's
    // facing, camera placed over its position at the participant's current eye height.
    public void MoveToSpawnPoint()
    {
        if (xrOrigin == null || spawnPoint == null)
        {
            Debug.LogError("SpawnHuman: cannot move to spawn because the XR Origin or spawn point is missing.");
            return;
        }

        xrOrigin.MatchOriginUpCameraForward(spawnPoint.up, spawnPoint.forward);
        Vector3 eyeLevelTarget = spawnPoint.position + Vector3.up * xrOrigin.CameraInOriginSpacePos.y;
        xrOrigin.MoveCameraToWorldLocation(eyeLevelTarget);
    }

    private void OnResetPressed(InputAction.CallbackContext context)
    {
        MoveToSpawnPoint();
    }

    private void OnDestroy()
    {
        if (_resetAction != null)
        {
            _resetAction.performed -= OnResetPressed;
        }
    }
}
