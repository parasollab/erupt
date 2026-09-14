using System;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;
using Erupt.Robot;
using Erupt.Ros;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Joint-space demonstration recorder, ported from RADER's <c>SetupUI</c> onto
    /// <see cref="IRosBus"/> and <see cref="IRobotModel"/>. Samples the virtual robot's
    /// joint state at a fixed interval while recording, builds a
    /// <c>trajectory_msgs/JointTrajectory</c>, publishes it on request, mirrors
    /// <c>/{ns}/joint_states</c> back onto the robot when asked, and streams the virtual
    /// joint state periodically. Plain C#: the owning plugin feeds it time via
    /// <see cref="Tick"/>, so it runs unchanged against a <c>FakeRosBus</c>.
    /// </summary>
    /// <remarks>
    /// Wire conventions kept from RADER: <c>/{ns}/interaction</c> carries <c>false</c> when a
    /// demonstration starts and <c>true</c> when it stops; <c>effort</c> is the crude
    /// torque estimate <c>I · Δθ/Δt</c> with a fixed moment of inertia; positions are ROS
    /// radians as the robot model reports them (RADER's UR5e-specific index remap and
    /// degree negation are gone — <see cref="IRobotModel"/> already speaks ROS).
    /// </remarks>
    public sealed class DemonstrationRecorder : IDisposable
    {
        public const string TrajectorySuffix = "/joint_trajectory";
        public const string InputStateSuffix = "/joint_states";
        public const string OutputStateSuffix = "/virtual_joint_state";
        public const string InteractionSuffix = "/interaction";

        private readonly IRosBus ros;
        private readonly IRobotModel robot;
        private readonly string frameId;
        private readonly List<JointTrajectoryPointMsg> points = new();
        private readonly Action<JointStateMsg> onInputState;

        private float[] previousPositions;
        private double previousSampleTime;
        private double recordStart;
        private double nextSampleAt;
        private double nextStateAt;
        private bool started;

        // Intervals are floats (they come from serialised fields); 0.1f widened to double is
        // 0.10000000149, so a tick landing exactly on the schedule would otherwise be late.
        private const double DueTolerance = 1e-6;

        public string TrajectoryTopic { get; }
        public string InputStateTopic { get; }
        public string OutputStateTopic { get; }
        public string InteractionTopic { get; }

        /// <summary>Seconds between recorded points.</summary>
        public float RecordInterval { get; set; } = 0.1f;
        /// <summary>Seconds between virtual joint state publishes.</summary>
        public float PublishStateInterval { get; set; } = 0.2f;
        /// <summary>Per-joint moment of inertia used for the effort estimate.</summary>
        public float MomentOfInertia { get; set; } = 0.0005f;

        /// <summary>Stream the virtual robot's joint state on <see cref="OutputStateTopic"/>.</summary>
        public bool PublishState { get; set; } = true;
        /// <summary>Apply inbound <see cref="InputStateTopic"/> messages to the virtual robot.</summary>
        public bool MirrorInput { get; set; }

        public bool IsRecording { get; private set; }
        public int PointCount => points.Count;
        /// <summary>The last finished demonstration; null until one has been stopped.</summary>
        public JointTrajectoryMsg LastDemonstration { get; private set; }
        public int MirroredStates { get; private set; }

        /// <summary>Recording started/stopped, a point was added, or a demonstration was published.</summary>
        public event Action Changed;

        public DemonstrationRecorder(IRosBus ros, IRobotModel robot, string robotNamespace, string frameId)
        {
            this.ros = ros ?? throw new ArgumentNullException(nameof(ros));
            this.robot = robot ?? throw new ArgumentNullException(nameof(robot));
            this.frameId = string.IsNullOrEmpty(frameId) ? "robot" : frameId;

            string prefix = Prefix(robotNamespace);
            TrajectoryTopic = prefix + TrajectorySuffix;
            InputStateTopic = prefix + InputStateSuffix;
            OutputStateTopic = prefix + OutputStateSuffix;
            InteractionTopic = prefix + InteractionSuffix;

            onInputState = OnInputState;
        }

        /// <summary>"/ns" for a namespace, "" for none; tolerates a leading or trailing slash.</summary>
        public static string Prefix(string robotNamespace)
        {
            if (string.IsNullOrWhiteSpace(robotNamespace)) return string.Empty;
            return "/" + robotNamespace.Trim().Trim('/');
        }

        /// <summary>Register publishers and subscribe to the input state. Idempotent.</summary>
        public void Start()
        {
            if (started) return;
            started = true;
            ros.RegisterPublisher<JointTrajectoryMsg>(TrajectoryTopic);
            ros.RegisterPublisher<JointStateMsg>(OutputStateTopic);
            ros.RegisterPublisher<BoolMsg>(InteractionTopic);
            ros.Subscribe(InputStateTopic, onInputState);
        }

        public void Dispose()
        {
            if (!started) return;
            started = false;
            ros.Unsubscribe(InputStateTopic, onInputState);
            IsRecording = false;
            points.Clear();
        }

        /// <summary>Begin a demonstration at <paramref name="now"/>: tells ROS the human took over and takes the first point.</summary>
        public void StartRecording(double now)
        {
            if (IsRecording) return;
            IsRecording = true;
            points.Clear();
            recordStart = now;
            previousPositions = robot.GetJointStatePositions();
            previousSampleTime = now;
            ros.Publish(InteractionTopic, new BoolMsg(false));
            AddPoint(now);
            nextSampleAt = now + RecordInterval;
            Changed?.Invoke();
        }

        /// <summary>End the demonstration: builds <see cref="LastDemonstration"/> and tells ROS the human let go.</summary>
        public JointTrajectoryMsg StopRecording(double now)
        {
            if (!IsRecording) return LastDemonstration;
            IsRecording = false;
            LastDemonstration = new JointTrajectoryMsg
            {
                header = new HeaderMsg { frame_id = frameId, stamp = Stamp(now) },
                joint_names = robot.GetJointStateNames(),
                points = points.ToArray()
            };
            ros.Publish(InteractionTopic, new BoolMsg(true));
            Changed?.Invoke();
            return LastDemonstration;
        }

        /// <summary>Send <see cref="LastDemonstration"/> on <see cref="TrajectoryTopic"/>. False when there is none.</summary>
        public bool Publish()
        {
            if (LastDemonstration == null) return false;
            ros.Publish(TrajectoryTopic, LastDemonstration);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Advance time: take due samples while recording, publish the state when due.</summary>
        public void Tick(double now)
        {
            if (IsRecording && now + DueTolerance >= nextSampleAt)
            {
                // One point per due tick, stamped with the actual time: a stalled frame
                // yields one late point rather than a burst of identically-stamped ones.
                AddPoint(now);
                nextSampleAt = now + Math.Max(RecordInterval, 1e-3f);
            }

            if (PublishState && now + DueTolerance >= nextStateAt)
            {
                PublishVirtualState(now);
                nextStateAt = now + Math.Max(PublishStateInterval, 1e-3f);
            }
        }

        /// <summary>Publish the virtual robot's joint state once, regardless of the interval.</summary>
        public void PublishVirtualState(double now)
        {
            float[] positions = robot.GetJointStatePositions();
            ros.Publish(OutputStateTopic, new JointStateMsg
            {
                header = new HeaderMsg { frame_id = frameId, stamp = Stamp(now) },
                name = robot.GetJointStateNames(),
                position = Array.ConvertAll(positions, p => (double)p),
                velocity = Array.Empty<double>(),
                effort = Efforts(positions, now)
            });
        }

        private void AddPoint(double now)
        {
            float[] positions = robot.GetJointStatePositions();
            double t = now - recordStart;
            int sec = (int)Math.Floor(t);
            uint nanosec = (uint)Math.Round((t - sec) * 1e9);
            points.Add(new JointTrajectoryPointMsg
            {
                positions = Array.ConvertAll(positions, p => (double)p),
                velocities = Array.Empty<double>(),
                accelerations = Array.Empty<double>(),
                effort = Efforts(positions, now),
                time_from_start = new DurationMsg { sec = sec, nanosec = nanosec }
            });
            Changed?.Invoke();
        }

        /// <summary>RADER's torque estimate: I · angular velocity since the previous sample.</summary>
        private double[] Efforts(float[] positions, double now)
        {
            var efforts = new double[positions.Length];
            double dt = now - previousSampleTime;
            if (previousPositions != null && dt > 0)
            {
                int n = Math.Min(positions.Length, previousPositions.Length);
                for (int i = 0; i < n; i++)
                    efforts[i] = MomentOfInertia * (positions[i] - previousPositions[i]) / dt;
            }
            previousPositions = positions;
            previousSampleTime = now;
            return efforts;
        }

        private void OnInputState(JointStateMsg state)
        {
            if (!MirrorInput || state?.name == null || state.position == null) return;
            robot.ApplyJointState(state.name, state.position);
            MirroredStates++;
        }

        private static TimeMsg Stamp(double seconds)
        {
            int sec = (int)Math.Floor(seconds);
            return new TimeMsg { sec = sec, nanosec = (uint)Math.Round((seconds - sec) * 1e9) };
        }
    }
}
