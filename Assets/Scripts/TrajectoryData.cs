using System;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

[CreateAssetMenu(fileName = "TrajectoryData", menuName = "Erupt/Trajectory Data")]
public class TrajectoryData : ScriptableObject
{
    [Serializable]
    public struct Waypoint
    {
        public double[] positions;
        public float timeFromStart;
    }

    public string[] jointNames;
    public Waypoint[] waypoints;

    public JointTrajectoryMsg ToJointTrajectoryMsg()
    {
        var points = new JointTrajectoryPointMsg[waypoints.Length];
        for (int i = 0; i < waypoints.Length; i++)
        {
            var wp = waypoints[i];
            int sec = (int)wp.timeFromStart;
            uint nanosec = (uint)((wp.timeFromStart - sec) * 1e9);
            points[i] = new JointTrajectoryPointMsg
            {
                positions = wp.positions,
                velocities = new double[0],
                accelerations = new double[0],
                effort = new double[0],
                time_from_start = new DurationMsg { sec = sec, nanosec = nanosec }
            };
        }
        return new JointTrajectoryMsg { joint_names = jointNames, points = points };
    }

    public static bool HasWaypoints(JointTrajectoryMsg trajectory) =>
        trajectory != null && trajectory.points != null && trajectory.points.Length > 0;

    public static TrajectoryData CreateFromTrajectory(JointTrajectoryMsg trajectory)
    {
        var data = CreateInstance<TrajectoryData>();
        data.jointNames = trajectory.joint_names;
        data.waypoints = new Waypoint[trajectory.points.Length];
        for (int i = 0; i < trajectory.points.Length; i++)
        {
            var point = trajectory.points[i];
            data.waypoints[i] = new Waypoint
            {
                positions = point.positions,
                timeFromStart = (float)(point.time_from_start.sec + point.time_from_start.nanosec * 1e-9)
            };
        }
        return data;
    }
}
