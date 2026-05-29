using System.Collections;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

public class TrajectoryReplay : MonoBehaviour
{
    // State
    private bool isReplaying = false;
    private Coroutine replayRoutine;

    // Prefabs & refs
    public GameObject real_robot;
    public GameObject robot_prefab;

    private GameObject robot;
    private RobotManager robotManager;

    private bool hasFinishedOneLoop = false;

    // Default ghost color is cyan with 0.5 alpha
    public Color ghostColor = new Color(0, 1, 1, 0.5f);

    public bool HasFinishedOneLoop() => hasFinishedOneLoop;

    // ---------- Public API ----------

    // Start only if nothing is already running (extra-safe guard)
    public void StartReplay(JointTrajectoryMsg trajectory)
    {
        if (replayRoutine != null)
        {
            Debug.LogWarning("[TrajectoryReplay] StartReplay ignored: a replay is already running.");
            return;
        }
        replayRoutine = StartCoroutine(RunReplay(trajectory));
    }

    // Force a restart with a new trajectory (stop current, wait for cleanup, then start)
    public void RestartReplay(JointTrajectoryMsg trajectory)
    {
        if (replayRoutine != null)
        {
            StartCoroutine(RestartRoutine(trajectory));
        }
        else
        {
            StartReplay(trajectory);
        }
    }

    // Stop current replay and clean up
    public void StopReplay()
    {
        if (!isReplaying && replayRoutine == null)
            return;

        isReplaying = false; // signals the running coroutine to exit
    }

    private IEnumerator RestartRoutine(JointTrajectoryMsg newTrajectory)
    {
        // Signal stop and wait for current routine to finish & cleanup
        isReplaying = false;
        if (replayRoutine != null)
        {
            yield return replayRoutine; // waits until RunReplay completes
        }
        // Now safe to start again
        StartReplay(newTrajectory);
    }

    private void OnDisable()
    {
        // Ensure cleanup if object is disabled/destroyed mid-replay
        isReplaying = false;
        if (replayRoutine != null)
        {
            StopCoroutine(replayRoutine);
            replayRoutine = null;
        }
        SafeDestroyRobot();
    }

    // ---------- Core flow ----------

    private IEnumerator RunReplay(JointTrajectoryMsg trajectory)
    {
        hasFinishedOneLoop = false;

        // Guard: if the prefab or real robot refs are missing, fail gracefully
        if (robot_prefab == null || real_robot == null)
        {
            Debug.LogError("[TrajectoryReplay] Missing references (robot_prefab or real_robot).");
            yield break;
        }

        // Spawn ghost
        robot = Instantiate(robot_prefab, real_robot.transform.position, real_robot.transform.rotation);

        foreach (var col in robot.GetComponentsInChildren<Collider>())
            col.gameObject.tag = "robot";

        // Make the ghost translucent
        var overlay = robot.AddComponent<TranslucentOverride>();
        overlay.overlayColor = ghostColor;

        robotManager = robot.GetComponent<RobotManager>();
        if (robotManager == null)
        {
            Debug.LogError("[TrajectoryReplay] RobotManager not found on robot prefab.");
            SafeDestroyRobot();
            yield break;
        }

        isReplaying = true;

        try
        {
            // Loop the trajectory while replaying is true
            while (isReplaying)
            {
                // If playTrajectory bails (e.g., mismatch), stop the replay
                bool ok = true;
                yield return StartCoroutine(PlayTrajectory(trajectory, success => ok = success));
                if (!ok) isReplaying = false;
            }
        }
        finally
        {
            // Always cleanup even on early exits/errors
            SafeDestroyRobot();
            replayRoutine = null;
            isReplaying = false;
        }
    }

    // Return success flag via callback so we can stop the outer loop if needed
    private IEnumerator PlayTrajectory(JointTrajectoryMsg trajectory, System.Action<bool> done)
    {
        var points = trajectory.points;
        if (points == null || points.Length == 0)
        {
            Debug.LogWarning("[TrajectoryReplay] Empty trajectory points.");
            done?.Invoke(false);
            yield break;
        }

        // Init
        double prevTime = DurationToSeconds(points[0].time_from_start);
        double[] prevPos = new double[points[0].positions.Length];
        for (int i = 0; i < prevPos.Length; i++)
            prevPos[i] = -1 * (points[0].positions[i] * Mathf.Rad2Deg);

        // Snap ghost to trajectory start so knob values are in a clean state before lerping
        for (int j = 0; j < prevPos.Length; j++)
            robotManager.SnapJointAngle(j, (float)prevPos[j]);

        // Step through points
        for (int i = 1; i < points.Length && isReplaying; i++)
        {
            double[] positions = points[i].positions;
            if (positions == null || positions.Length != robotManager.GetJointNames().Count)
            {
                Debug.LogError("[TrajectoryReplay] Positions length mismatch with joint count.");
                done?.Invoke(false);
                yield break;
            }

            double[] modifiedPositions = new double[positions.Length];
            for (int j = 0; j < positions.Length; j++)
                modifiedPositions[j] = -1 * (positions[j] * Mathf.Rad2Deg);

            double currTime = DurationToSeconds(points[i].time_from_start);
            double movingTime = currTime - prevTime;
            if (movingTime < 0) movingTime = 0;

            yield return MoveKnobsOverTime(prevPos, modifiedPositions, movingTime);

            prevPos = modifiedPositions;
            prevTime = currTime;
        }

        hasFinishedOneLoop = true;
        done?.Invoke(true);
    }

    private IEnumerator MoveKnobsOverTime(double[] startPositions, double[] endPositions, double duration)
    {
        float elapsedTime = 0f;
        float dur = Mathf.Max(0.000001f, (float)duration);

        int jointCount = robotManager.GetJointNames().Count;

        while (elapsedTime < dur && isReplaying)
        {
            elapsedTime += Time.deltaTime;
            float t = Mathf.Clamp01(elapsedTime / dur);

            for (int j = 0; j < jointCount; j++)
            {
                float newPos = Mathf.Lerp((float)startPositions[j], (float)endPositions[j], t);
                robotManager.SetJointAngle(j, newPos);
            }
            yield return null;
        }

        // Snap to exact end on completion (only if still replaying)
        if (isReplaying)
        {
            for (int j = 0; j < jointCount; j++)
                robotManager.SetJointAngle(j, (float)endPositions[j]);
        }
    }

    private double DurationToSeconds(DurationMsg duration)
    {
        return duration.sec + (duration.nanosec * 1e-9);
    }

    private void SafeDestroyRobot()
    {
        if (robot != null)
        {
            Destroy(robot);
            robot = null;
        }
    }
}
