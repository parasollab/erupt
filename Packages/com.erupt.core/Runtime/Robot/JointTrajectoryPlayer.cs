using System.Collections;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

namespace Erupt.Robot
{
    /// <summary>
    /// Something a trajectory can be played onto instead of the robot itself: a translucent
    /// copy of the robot that shows a plan while the robot stays where it is.
    /// </summary>
    public interface IJointPoseTarget
    {
        /// <summary>Put the named joints at the given positions (radians, or metres for a prismatic joint).</summary>
        void ApplyJointState(string[] names, double[] positions);
    }

    /// <summary>
    /// Plays a joint trajectory, in a loop until it is stopped: on the robot model, restoring
    /// the robot's pose when stopped, or onto an <see cref="IJointPoseTarget"/>, in which case
    /// the robot is not touched.
    /// </summary>
    public class JointTrajectoryPlayer : MonoBehaviour
    {
        [Tooltip("The robot to play on. Empty = the RobotInstance this player sits under.")]
        [SerializeField] private DirectArticulationIKController ikController;
        private Erupt.Robot.IRobotModel robot;

        /// <summary>Use a robot other than the serialised controller (a plugin's own player gets the robot it works with).</summary>
        public void SetRobot(Erupt.Robot.IRobotModel model) => robot = model;

        // Explicit null tests: a missing serialised reference is a Unity fake null that ?? would keep.
        private Erupt.Robot.IRobotModel Robot
        {
            get
            {
                if (robot != null) return robot;
                if (ikController != null) return ikController;
                RobotInstance owner = GetComponentInParent<RobotInstance>(true);
                return owner != null ? owner.Model : null;
            }
        }

        private bool isReplaying = false;
        private Coroutine replayRoutine;
        private bool hasFinishedOneLoop = false;

        private string[] savedNames;
        private float[] savedPositions;

        // What the running replay writes to; null = the robot.
        private IJointPoseTarget playingOnto;

        public bool HasFinishedOneLoop() => hasFinishedOneLoop;

        /// <summary>True from the start of a replay until it has been stopped and has let go.</summary>
        public bool IsReplaying => replayRoutine != null;

        /// <summary>
        /// Play a trajectory in a loop until <see cref="StopReplay"/>: on the robot, or onto
        /// <paramref name="target"/> when one is given (the robot is then left alone).
        /// </summary>
        public void StartReplay(JointTrajectoryMsg trajectory, IJointPoseTarget target = null)
        {
            if (replayRoutine != null)
            {
                Debug.LogWarning("[JointTrajectoryPlayer] StartReplay ignored: already running.");
                return;
            }
            replayRoutine = StartCoroutine(RunReplay(trajectory, target));
        }

        /// <summary>As <see cref="StartReplay"/>, stopping the replay that is running first.</summary>
        public void RestartReplay(JointTrajectoryMsg trajectory, IJointPoseTarget target = null)
        {
            if (replayRoutine != null)
                StartCoroutine(RestartRoutine(trajectory, target));
            else
                StartReplay(trajectory, target);
        }

        public void StopReplay()
        {
            if (!isReplaying && replayRoutine == null) return;
            isReplaying = false;
        }

        private void OnDisable()
        {
            isReplaying = false;
            if (replayRoutine != null)
            {
                StopCoroutine(replayRoutine);
                replayRoutine = null;
            }
            RestoreSavedPose();
            playingOnto = null;
        }

        private IEnumerator RestartRoutine(JointTrajectoryMsg newTrajectory, IJointPoseTarget target)
        {
            isReplaying = false;
            if (replayRoutine != null)
                yield return replayRoutine;
            StartReplay(newTrajectory, target);
        }

        private IEnumerator RunReplay(JointTrajectoryMsg trajectory, IJointPoseTarget target)
        {
            hasFinishedOneLoop = false;
            playingOnto = target;

            if (target == null)
            {
                if (Robot == null)
                {
                    Debug.LogError("[JointTrajectoryPlayer] No robot to play on.");
                    replayRoutine = null;
                    yield break;
                }

                // The playback drives the joints from here on: a live mirror of the real robot lets go.
                Robot.TakeControl();
                savedNames = Robot.GetJointStateNames();
                savedPositions = Robot.GetJointStatePositions();
            }

            isReplaying = true;

            try
            {
                while (isReplaying)
                {
                    bool ok = true;
                    yield return StartCoroutine(PlayTrajectory(trajectory, success => ok = success));
                    if (!ok) isReplaying = false;
                }
            }
            finally
            {
                RestoreSavedPose();
                playingOnto = null;
                replayRoutine = null;
                isReplaying = false;
            }
        }

        private void Apply(string[] names, double[] positions)
        {
            if (playingOnto != null) playingOnto.ApplyJointState(names, positions);
            else Robot.ApplyJointState(names, positions);
        }

        private IEnumerator PlayTrajectory(JointTrajectoryMsg trajectory, System.Action<bool> done)
        {
            var points = trajectory.points;
            if (points == null || points.Length == 0)
            {
                Debug.LogWarning("[JointTrajectoryPlayer] Empty trajectory.");
                done?.Invoke(false);
                yield break;
            }

            string[] names = trajectory.joint_names;

            Apply(names, points[0].positions);

            double prevTime = DurationToSeconds(points[0].time_from_start);
            double[] prevPos = points[0].positions;

            for (int i = 1; i < points.Length && isReplaying; i++)
            {
                double[] targetPos = points[i].positions;
                if (targetPos == null || targetPos.Length != names.Length)
                {
                    Debug.LogError("[JointTrajectoryPlayer] Positions length mismatch.");
                    done?.Invoke(false);
                    yield break;
                }

                double currTime = DurationToSeconds(points[i].time_from_start);
                float duration = Mathf.Max(0.000001f, (float)(currTime - prevTime));

                yield return LerpJointsOverTime(names, prevPos, targetPos, duration);

                prevPos = targetPos;
                prevTime = currTime;
            }

            hasFinishedOneLoop = true;
            done?.Invoke(true);
        }

        private IEnumerator LerpJointsOverTime(string[] names, double[] from, double[] to, float duration)
        {
            float elapsed = 0f;
            double[] lerped = new double[names.Length];

            while (elapsed < duration && isReplaying)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                for (int j = 0; j < names.Length; j++)
                    lerped[j] = from[j] + (to[j] - from[j]) * t;
                Apply(names, lerped);
                yield return null;
            }

            if (isReplaying)
                Apply(names, to);
        }

        private void RestoreSavedPose()
        {
            if (savedNames != null && savedPositions != null && Robot != null)
                Robot.ApplyJointState(savedNames, savedPositions);
            savedNames = null;
            savedPositions = null;
        }

        private static double DurationToSeconds(DurationMsg duration)
        {
            return duration.sec + (duration.nanosec * 1e-9);
        }
    }
}
