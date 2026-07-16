using System.Collections;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

// Plays a joint trajectory directly on a SIRL ghost's own DirectArticulationIKController
// (pure forward joint-position playback, no nested ghost spawn) and draws the end
// effector's path as a persistent colored line so trajectories can be compared visually
// even before, during, or after playback.
[RequireComponent(typeof(LineRenderer))]
public class SirlTrajectoryPlayer : MonoBehaviour
{
    private DirectArticulationIKController ik;
    private LineRenderer pathLine;
    private Coroutine playRoutine;

    public bool IsRunning => playRoutine != null;

    public void Configure(DirectArticulationIKController controller)
    {
        ik = controller;

        pathLine = GetComponent<LineRenderer>();
        pathLine.useWorldSpace = true;
        pathLine.startWidth = pathLine.endWidth = 0.005f;
        pathLine.material = new Material(Shader.Find("Sprites/Default"));
        pathLine.positionCount = 0;
    }

    // Samples the end effector's world position at every waypoint via forward kinematics
    // (posing the ghost through each point in turn) and draws the result as a line, then
    // leaves the ghost posed at the trajectory's first point.
    public void ShowPath(JointTrajectoryMsg trajectory, Color color)
    {
        pathLine.positionCount = 0;
        if (ik == null || ik.EndEffector == null || trajectory?.points == null || trajectory.points.Length == 0)
            return;

        color.a = 1f;
        pathLine.startColor = color;
        pathLine.endColor = color;

        var positions = new Vector3[trajectory.points.Length];
        for (int i = 0; i < trajectory.points.Length; i++)
        {
            ik.ApplyJointState(trajectory.joint_names, trajectory.points[i].positions);
            positions[i] = ik.EndEffector.position;
        }

        pathLine.positionCount = positions.Length;
        pathLine.SetPositions(positions);

        ik.ApplyJointState(trajectory.joint_names, trajectory.points[0].positions);
    }

    public void StartReplay(JointTrajectoryMsg trajectory, bool loop = false)
    {
        StopReplay();
        playRoutine = StartCoroutine(RunReplay(trajectory, loop));
    }

    public void StopReplay()
    {
        if (playRoutine != null)
        {
            StopCoroutine(playRoutine);
            playRoutine = null;
        }
    }

    private void OnDisable() => StopReplay();

    private IEnumerator RunReplay(JointTrajectoryMsg trajectory, bool loop)
    {
        var points = trajectory.points;
        if (points == null || points.Length == 0 || ik == null)
        {
            playRoutine = null;
            yield break;
        }

        string[] names = trajectory.joint_names;

        do
        {
            ik.ApplyJointState(names, points[0].positions);
            double prevTime = DurationToSeconds(points[0].time_from_start);

            for (int i = 1; i < points.Length; i++)
            {
                double currTime = DurationToSeconds(points[i].time_from_start);
                float duration = Mathf.Max(0.000001f, (float)(currTime - prevTime));
                yield return LerpJoints(names, points[i - 1].positions, points[i].positions, duration);
                prevTime = currTime;
            }
        } while (loop);

        playRoutine = null;
    }

    private IEnumerator LerpJoints(string[] names, double[] from, double[] to, float duration)
    {
        float elapsed = 0f;
        var lerped = new double[names.Length];

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int j = 0; j < names.Length; j++)
                lerped[j] = from[j] + (to[j] - from[j]) * t;
            ik.ApplyJointState(names, lerped);
            yield return null;
        }

        ik.ApplyJointState(names, to);
    }

    private static double DurationToSeconds(DurationMsg duration) => duration.sec + duration.nanosec * 1e-9;
}
