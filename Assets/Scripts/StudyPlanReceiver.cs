using System.Collections;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.StudyInterfaces;

// Subscribes to /study/plan on behalf of StudyController and applies whichever plan
// arrives first: the externally-supplied one from study_controller_node, or (if none
// arrives within the timeout, e.g. running standalone without ROS) the local fallback.
public static class StudyPlanReceiver
{
    public static void WaitForPlanOrTimeout(StudyController controller, float planTimeoutSeconds)
    {
        bool received = false;

        ROSConnection ros = ROSConnection.GetOrCreateInstance();
        // StudyPlanMsg's own Register() defaults to RuntimeInitializeLoadType.AfterSceneLoad,
        // which runs after StudyController.Awake() (this call happens in StartScene's Awake
        // phase). Without this, MessageRegistry doesn't know StudyPlanMsg's RosMessageName yet,
        // so Subscribe below resolves an empty name that's never corrected afterward.
        StudyPlanMsg.Register();
        ros.Subscribe<StudyPlanMsg>("/study/plan", msg =>
        {
            if (received) return;
            received = true;
            controller.ApplyExternalPlan(msg);
        });

        controller.StartCoroutine(FallbackAfterTimeout(controller, planTimeoutSeconds, () => received));
    }

    // How long to wait for the TCP connection itself before the plan timeout starts
    // counting. The headset's WiFi connection can take several seconds to come up — with a
    // fixed timeout measured from app start, the local fallback plan raced (and sometimes
    // beat) the connection, silently desynchronizing the scene order from the ROS side and
    // skipping crash-recovery resume entirely.
    private const float kConnectionWaitSeconds = 8f;

    private static IEnumerator FallbackAfterTimeout(StudyController controller, float timeoutSeconds, System.Func<bool> receivedCheck)
    {
        ROSConnection ros = ROSConnection.GetOrCreateInstance();

        // Phase 1: wait for the connection. HasConnectionError starts false before the
        // connect attempt begins, so only trust "no error" as "connected" once the
        // connection thread exists and a moment has passed.
        float startTime = Time.unscaledTime;
        float connectDeadline = startTime + kConnectionWaitSeconds;
        bool sawConnection = false;
        while (Time.unscaledTime < connectDeadline && !receivedCheck())
        {
            if (Time.unscaledTime > startTime + 0.5f && ros.HasConnectionThread && !ros.HasConnectionError)
            {
                sawConnection = true;
                break;
            }
            yield return null;
        }

        // Phase 2: connected (or gave up waiting) — the plan republishes at 1 Hz, so
        // timeoutSeconds from here is ample.
        float planDeadline = Time.unscaledTime + timeoutSeconds;
        while (!receivedCheck() && Time.unscaledTime < planDeadline)
        {
            yield return null;
        }

        if (!receivedCheck())
        {
            Debug.LogWarning(sawConnection
                ? "StudyPlanReceiver: connected to ROS but no /study/plan arrived; using the local fallback plan."
                : $"StudyPlanReceiver: no ROS connection within {kConnectionWaitSeconds}s; using the local fallback plan.");
            controller.ApplyLocalFallbackPlan();
        }
    }
}
