using UnityEngine;

public class TrajectoryAutoPlayer : MonoBehaviour
{
    [SerializeField] private TrajectoryReplay trajectoryReplayer;
    [SerializeField] private TrajectoryData trajectoryData;
    [SerializeField] private float startDelay = 0f;
    [SerializeField] private MoveItPlanningRequestMenuUI moveItMenu;

    private void Start()
    {
        if (trajectoryReplayer == null || trajectoryData == null)
        {
            Debug.LogWarning("[TrajectoryAutoPlayer] Missing trajectoryReplayer or trajectoryData; not auto-playing.");
            return;
        }

        if (startDelay > 0f)
            Invoke(nameof(PlayNow), startDelay);
        else
            PlayNow();
    }

    private void PlayNow()
    {
        trajectoryReplayer.StartReplay(trajectoryData.ToJointTrajectoryMsg());
        if (moveItMenu != null)
            moveItMenu.SetStartAndGoalFromTrajectory(trajectoryData);
    }
}
