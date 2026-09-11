using UnityEngine;
using Unity.XR.CoreUtils;
using System.Collections;

public class SpawnHuman : MonoBehaviour
{
    public Transform spawnPoint;
    public XROrigin xrOrigin;

    IEnumerator Start()
    {
        yield return null;

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

        xrOrigin.MatchOriginUpCameraForward(spawnPoint.up, spawnPoint.forward);
        Vector3 eyeLevelTarget = spawnPoint.position + Vector3.up * xrOrigin.CameraInOriginSpacePos.y;
        xrOrigin.MoveCameraToWorldLocation(eyeLevelTarget);
    }
}
