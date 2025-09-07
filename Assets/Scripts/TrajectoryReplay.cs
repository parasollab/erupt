using System.Collections;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Std;
using RosMessageTypes.Sensor;

public class TrajectoryReplay : MonoBehaviour
{
    private bool isReplaying = false;
    public GameObject real_robot;
    public GameObject robot_prefab;
    private GameObject robot;
    private RobotManager robotManager;

    // Default ghost color is cyan with 0.5 alpha
    public Color ghostColor = new Color(0, 1, 1, 0.5f);

    public IEnumerator StartReplay(JointTrajectoryMsg trajectory)
    {
        // Spawn the ghost robot
        robot = Instantiate(robot_prefab, real_robot.transform.position, real_robot.transform.rotation);

        // Make the ghost transluscent
        robot.AddComponent<TranslucentOverride>().overlayColor = ghostColor;

        // Get the robot manager from the robot
        robotManager = robot.GetComponent<RobotManager>();

        isReplaying = true;
        while (isReplaying)
        {
            yield return StartCoroutine(playTrajectory(trajectory));
        }
        // Cleanup
        Destroy(robot);
        robot = null;
    }

    // Call this to stop replay and destroy the ghost robot
    public void StopReplay()
    {
        isReplaying = false;
    }

    IEnumerator playTrajectory(JointTrajectoryMsg trajectory)
    {
        JointTrajectoryPointMsg[] points = trajectory.points;
        double prevTime = durationToDouble(points[0].time_from_start);
        double[] prevPos = new double[points[0].positions.Length];
        for (int i = 0; i < prevPos.Length; i++)
            prevPos[i] = -1 * (points[0].positions[i] * Mathf.Rad2Deg);
        for (int i = 1; i < points.Length; i++)
        {
            double[] positions = points[i].positions;
            double[] modifiedPositions = new double[positions.Length];
            for (int j = 0; j < positions.Length; j++)
                modifiedPositions[j] = -1 * (positions[j] * Mathf.Rad2Deg);
            double currTime = durationToDouble(points[i].time_from_start);
            double movingTime = currTime - prevTime;
            if (positions.Length != robotManager.GetJointNames().Count)
            {
                Debug.LogError("Positions array length does not match knobs count.");
                yield break;
            }
            yield return StartCoroutine(MoveKnobsOverTime(prevPos, modifiedPositions, movingTime));
            prevPos = modifiedPositions;
            prevTime = currTime;
        }
    }

    IEnumerator MoveKnobsOverTime(double[] startPositions, double[] endPositions, double duration)
    {
        float elapsedTime = 0f;
        if (duration <= 0f) duration = 0.000001f;
        while (elapsedTime < duration)
        {
            elapsedTime += Time.deltaTime;
            float t = Mathf.Clamp01(elapsedTime / (float)duration);
            for (int j = 0; j < robotManager.GetJointNames().Count; j++)
            {
                float newPos = Mathf.Lerp((float)startPositions[j], (float)endPositions[j], t);
                robotManager.SetJointAngle(j, newPos);
            }
            yield return null;
        }
    }

    double durationToDouble(DurationMsg duration)
    {
        return duration.sec + (duration.nanosec * 1e-9);
    }
}
