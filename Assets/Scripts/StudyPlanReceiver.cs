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

    private static IEnumerator FallbackAfterTimeout(StudyController controller, float timeoutSeconds, System.Func<bool> receivedCheck)
    {
        yield return new WaitForSeconds(timeoutSeconds);
        if (!receivedCheck())
        {
            controller.ApplyLocalFallbackPlan();
        }
    }
}
