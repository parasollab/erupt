using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.StudyInterfaces;

// Subscribes to /study/resume on behalf of StudyController. The topic is latched and only
// published by study_controller_node when it was launched with crash:=true, carrying the
// study stop recorded before the crash — so in a normal session nothing ever arrives and
// startup is unchanged. Subscribed before /study/plan (see StudyController.Awake) so the
// latched resume message tends to arrive no later than the plan it refers to.
public static class StudyResumeReceiver
{
    public static void Listen(StudyController controller)
    {
        ROSConnection ros = ROSConnection.GetOrCreateInstance();
        // Same registration quirk as StudyPlanReceiver: force message registration now so the
        // Subscribe below resolves the correct RosMessageName during StartScene's Awake phase.
        StudyStateMsg.Register();
        ros.Subscribe<StudyStateMsg>("/study/resume", msg => controller.ApplyResumeState(msg));
    }
}
