using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Std;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// The opt-in switch of the live suite and the helpers every live test uses.
    /// </summary>
    /// <remarks>
    /// The live tests run the reference app's real scenes on the real <c>LiveRosBus</c> against
    /// a running ROS 2 stack (see <c>README.md</c> next to this file). They are ignored unless
    /// <see cref="EndpointVariable"/> names the ROS-TCP endpoint and <see cref="ScenarioVariable"/>
    /// names the stack that is running, so the normal suites and CI never touch a network.
    /// </remarks>
    public static class LiveRos
    {
        /// <summary><c>host:port</c> of the ROS-TCP endpoint, e.g. <c>127.0.0.1:10000</c>.</summary>
        public const string EndpointVariable = "ERUPT_LIVE_ROS";
        /// <summary>Which stack is behind the endpoint: <c>single</c>, <c>arms</c> or <c>offline</c>.</summary>
        public const string ScenarioVariable = "ERUPT_LIVE_SCENARIO";

        /// <summary>One robot in the root namespace (<c>docker compose up erupt</c>): the FR3, or the one <see cref="LiveRobots.RobotVariable"/> names.</summary>
        public const string Single = "single";
        /// <summary>Three robots in <c>left</c>, <c>right</c>, <c>third</c>, each at the origin of its own world: FR3s, or the ones <see cref="LiveRobots.ArmsVariable"/> names.</summary>
        public const string Arms = "arms";
        /// <summary>Nothing listens on the endpoint when the test starts.</summary>
        public const string Offline = "offline";

        /// <summary>
        /// For the <c>offline</c> scenario: how many seconds the second half of the fixture waits
        /// for the ROS side to be started while it runs. Unset = that half is ignored.
        /// </summary>
        public const string WaitForRosVariable = "ERUPT_LIVE_WAIT_FOR_ROS";

        public static float WaitForRosSeconds =>
            float.TryParse(System.Environment.GetEnvironmentVariable(WaitForRosVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
                ? seconds : 0f;

        public static string Scenario =>
            (System.Environment.GetEnvironmentVariable(ScenarioVariable) ?? "").Trim().ToLowerInvariant();

        public static bool TryGetEndpoint(out string host, out int port)
        {
            host = null;
            port = 0;
            string value = System.Environment.GetEnvironmentVariable(EndpointVariable);
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            int colon = value.LastIndexOf(':');
            if (colon < 0)
            {
                host = value;
                port = 10000;
                return true;
            }
            host = value.Substring(0, colon);
            return host.Length > 0 && int.TryParse(value.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
        }

        /// <summary>Ignore the calling test unless the live suite is switched on for this scenario.</summary>
        public static void Require(string scenario)
        {
            if (!TryGetEndpoint(out _, out _))
                Assert.Ignore($"Live ROS tests are opt-in: set {EndpointVariable}=host:port and {ScenarioVariable}={scenario}.");
            if (Scenario != scenario)
                Assert.Ignore($"This test needs {ScenarioVariable}={scenario} (it is '{Scenario}').");
        }

        /// <summary>
        /// Point the app's ROS connection at the endpoint under test. The app creates its
        /// <c>ROSConnection</c> lazily, in the scene whose plugins first use the bus, and that
        /// connection lives and dies with the scene. To leave that lifecycle as it is, the
        /// connection is created the same way (from the per-machine prefab, in the scene) the
        /// moment <paramref name="sceneName"/> has loaded, before any <c>Start</c> runs, and only
        /// its address is replaced.
        /// </summary>
        public static void UseEndpointIn(string sceneName)
        {
            if (!TryGetEndpoint(out string host, out int port))
                throw new InvalidOperationException($"{EndpointVariable} is not set.");
            endpointScene = sceneName;
            endpointHost = host;
            endpointPort = port;
            if (hooked) return;
            hooked = true;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Stop redirecting connections (the suite is done).</summary>
        public static void StopUsingEndpoint()
        {
            if (!hooked) return;
            hooked = false;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>The connection of the scene that was loaded last; null before the first load.</summary>
        public static ROSConnection CurrentConnection { get; private set; }

        private static bool hooked;
        private static string endpointScene, endpointHost;
        private static int endpointPort;

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (scene.name != endpointScene) return;
            LiveRobots.Apply(scene, Scenario);
            ROSConnection connection = ROSConnection.GetOrCreateInstance();
            connection.RosIPAddress = endpointHost;
            connection.RosPort = endpointPort;
            connection.ShowHud = false;
            // The app's bus clears this when it creates the connection, before the connection's
            // Start reads it; here the connection exists before the bus has asked for it, so the
            // order of the two Starts is not the app's.
            connection.listenForTFMessages = false;
            CurrentConnection = connection;
        }

        // ---- waiting ---------------------------------------------------------------------------

        /// <summary>Wait until <paramref name="condition"/> holds; fail with <paramref name="what"/> after the timeout (real seconds).</summary>
        public static IEnumerator Until(Func<bool> condition, float timeoutSeconds, string what)
        {
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail($"Timed out after {timeoutSeconds:0.#} s waiting for: {what}");
                yield return null;
            }
        }

        /// <summary>As <see cref="Until"/>, but a timeout only sets <paramref name="timedOut"/>.</summary>
        public static IEnumerator Until(Func<bool> condition, float timeoutSeconds, Action<bool> timedOut)
        {
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) { timedOut(true); yield break; }
                yield return null;
            }
            timedOut(false);
        }

        public static IEnumerator Seconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        // ---- services --------------------------------------------------------------------------

        /// <summary>Call a service on a robot's bus and wait for the answer.</summary>
        public static IEnumerator Call<TRequest, TResponse>(IRosBus bus, string service, TRequest request,
            Action<TResponse> got, float timeoutSeconds = 15f)
            where TRequest : Message where TResponse : Message, new()
        {
            bool done = false;
            TResponse response = null;
            bus.RegisterRosService<TRequest, TResponse>(service);
            bus.SendServiceMessage<TResponse>(service, request, r => { response = r; done = true; });
            yield return Until(() => done, timeoutSeconds, $"the answer of service '{bus.ResolveName(service)}'");
            got(response);
        }

        /// <summary>
        /// A robot's MoveIt planning scene, straight from <c>get_planning_scene</c>: world objects
        /// with geometry, attached bodies, the robot state and the frames MoveIt knows.
        /// </summary>
        public static IEnumerator PlanningScene(RobotInstance robot, Action<PlanningSceneMsg> got)
        {
            uint components = PlanningSceneComponentsMsg.WORLD_OBJECT_NAMES
                            | PlanningSceneComponentsMsg.WORLD_OBJECT_GEOMETRY
                            | PlanningSceneComponentsMsg.ROBOT_STATE_ATTACHED_OBJECTS
                            | PlanningSceneComponentsMsg.ROBOT_STATE
                            | PlanningSceneComponentsMsg.TRANSFORMS;
            yield return Call<GetPlanningSceneRequest, GetPlanningSceneResponse>(robot.Ros, "get_planning_scene",
                new GetPlanningSceneRequest(new PlanningSceneComponentsMsg(components)), r => got(r?.scene));
        }

        /// <summary>
        /// The pose of a world object in <paramref name="targetFrame"/>, worked out from the frame
        /// the object is reported in and the frames MoveIt knows (<c>fixed_frame_transforms</c>,
        /// which come from that robot's TF). MoveIt keeps objects in its own planning frame (the
        /// robot model's root), whatever frame they were sent in.
        /// </summary>
        public static void PoseIn(PlanningSceneMsg scene, string targetFrame, CollisionObjectMsg co, out Vector3 position, out Quaternion rotation)
        {
            FrameInModel(scene, co.header.frame_id, out Vector3 sp, out Quaternion sq);
            FrameInModel(scene, targetFrame, out Vector3 tp, out Quaternion tq);
            Invert(tp, tq, out Vector3 itp, out Quaternion itq);
            Compose(sp, sq, Vec(co.pose.position), Quat(co.pose.orientation), out Vector3 mp, out Quaternion mq);
            Compose(itp, itq, mp, mq, out position, out rotation);
        }

        /// <summary>The frame MoveIt plans in (the root of its robot model), as this planning scene reports it.</summary>
        public static string ModelFrame(PlanningSceneMsg scene)
        {
            // MoveIt lists each frame it knows as { header.frame_id = that frame, child_frame_id =
            // its own planning frame, transform = the frame's pose in the planning frame }.
            foreach (var t in scene?.fixed_frame_transforms ?? Array.Empty<TransformStampedMsg>())
                if (!string.IsNullOrEmpty(t.child_frame_id)) return t.child_frame_id.TrimStart('/');
            return (scene?.robot_state?.joint_state?.header?.frame_id ?? "").TrimStart('/');
        }

        /// <summary>The pose of a frame in MoveIt's planning frame, from the frames MoveIt knows through TF.</summary>
        public static void FrameInModel(PlanningSceneMsg scene, string frame, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            frame = (frame ?? "").TrimStart('/');
            if (frame.Length == 0 || frame == ModelFrame(scene)) return;
            foreach (var t in scene?.fixed_frame_transforms ?? Array.Empty<TransformStampedMsg>())
            {
                if ((t.header?.frame_id ?? "").TrimStart('/') != frame) continue;
                position = Vec(t.transform.translation);
                rotation = Quat(t.transform.rotation);
                return;
            }
            Assert.Fail($"MoveIt does not know the frame '{frame}' (planning frame '{ModelFrame(scene)}', known: " +
                        string.Join(", ", Array.ConvertAll(scene?.fixed_frame_transforms ?? Array.Empty<TransformStampedMsg>(), t => t.header?.frame_id)) + ").");
        }

        /// <summary>One world object of a robot's MoveIt planning scene; null when it is not there.</summary>
        public static IEnumerator WorldObject(RobotInstance robot, string id, Action<CollisionObjectMsg> got)
        {
            PlanningSceneMsg scene = null;
            yield return PlanningScene(robot, s => scene = s);
            got(Find(scene, id));
        }

        public static CollisionObjectMsg Find(PlanningSceneMsg scene, string id)
        {
            foreach (CollisionObjectMsg co in scene?.world?.collision_objects ?? Array.Empty<CollisionObjectMsg>())
                if (co.id == id) return co;
            return null;
        }

        public static AttachedCollisionObjectMsg FindAttached(PlanningSceneMsg scene, string id)
        {
            foreach (AttachedCollisionObjectMsg aco in scene?.robot_state?.attached_collision_objects ?? Array.Empty<AttachedCollisionObjectMsg>())
                if (aco.@object != null && aco.@object.id == id) return aco;
            return null;
        }

        /// <summary>Poll a robot's planning scene until <paramref name="accept"/> likes what it holds for <paramref name="id"/> (null = absent).</summary>
        public static IEnumerator UntilWorldObject(RobotInstance robot, string id, Func<CollisionObjectMsg, bool> accept,
            float timeoutSeconds, string what, Action<CollisionObjectMsg> got = null)
        {
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            CollisionObjectMsg last = null;
            while (true)
            {
                yield return WorldObject(robot, id, co => last = co);
                if (accept(last)) break;
                if (Time.realtimeSinceStartup > end)
                    Assert.Fail($"Timed out after {timeoutSeconds:0.#} s waiting for: {what} (planning scene of '{robot.Id}' holds: {Describe(last)})");
                yield return Seconds(0.2f);
            }
            got?.Invoke(last);
        }

        // ---- poses -----------------------------------------------------------------------------

        /// <summary>
        /// A Unity world pose as the ROS pose it stands for in a planning frame, computed
        /// independently of the app's own conversion: ROS (x, y, z) is Unity (x, z, y) in the
        /// frame, and swapping two axes mirrors a rotation (x, y, z, w) into (−x, −z, −y, w).
        /// </summary>
        public static void ExpectedRosPose(Transform frame, Vector3 worldPosition, Quaternion worldRotation,
            out Vector3 rosPosition, out Quaternion rosRotation)
        {
            Vector3 p = frame != null ? frame.InverseTransformPoint(worldPosition) : worldPosition;
            Quaternion q = frame != null ? Quaternion.Inverse(frame.rotation) * worldRotation : worldRotation;
            rosPosition = new Vector3(p.x, p.z, p.y);
            rosRotation = new Quaternion(-q.x, -q.z, -q.y, q.w);
        }

        public static Vector3 Vec(PointMsg p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
        public static Vector3 Vec(Vector3Msg p) => new Vector3((float)p.x, (float)p.y, (float)p.z);

        public static Quaternion Quat(QuaternionMsg q) =>
            q == null || (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0)
                ? Quaternion.identity
                : new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);

        /// <summary>
        /// Compose two ROS poses as plain right-handed transforms (parent ∘ child). Unity's
        /// quaternion product is the Hamilton product, which is all this needs.
        /// </summary>
        public static void Compose(Vector3 parentPosition, Quaternion parentRotation, Vector3 childPosition, Quaternion childRotation,
            out Vector3 position, out Quaternion rotation)
        {
            position = parentPosition + Rotate(parentRotation, childPosition);
            rotation = parentRotation * childRotation;
        }

        /// <summary>The inverse of a ROS pose.</summary>
        public static void Invert(Vector3 position, Quaternion rotation, out Vector3 inversePosition, out Quaternion inverseRotation)
        {
            inverseRotation = Quaternion.Inverse(rotation);
            inversePosition = -Rotate(inverseRotation, position);
        }

        // q v q* written out, so no engine handedness convention is involved.
        public static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.x, q.y, q.z);
            return v + 2f * Vector3.Cross(u, Vector3.Cross(u, v) + q.w * v);
        }

        public static string Describe(CollisionObjectMsg co)
        {
            if (co == null) return "(absent)";
            var p = co.pose?.position;
            var q = co.pose?.orientation;
            string dims = "";
            foreach (var primitive in co.primitives ?? Array.Empty<RosMessageTypes.Shape.SolidPrimitiveMsg>())
                dims += $" P{primitive.type}[{string.Join(",", Array.ConvertAll(primitive.dimensions ?? Array.Empty<double>(), d => d.ToString("F3", CultureInfo.InvariantCulture)))}]";
            return string.Format(CultureInfo.InvariantCulture,
                "'{0}' frame={1} p=({2:F4},{3:F4},{4:F4}) q=({5:F4},{6:F4},{7:F4},{8:F4}){9} meshes={10}",
                co.id, co.header?.frame_id, p?.x, p?.y, p?.z, q?.x, q?.y, q?.z, q?.w, dims, co.meshes?.Length ?? 0);
        }

        // ---- traffic ---------------------------------------------------------------------------

        /// <summary>Counts what ROS carries on a topic while it is alive (a second subscription through the endpoint).</summary>
        public sealed class TopicCounter<T> : IDisposable where T : Message
        {
            private readonly IRosBus bus;
            private readonly string topic;
            private readonly Action<T> handler;
            public readonly List<T> Messages = new();
            public int Count => Messages.Count;

            public TopicCounter(IRosBus bus, string topic)
            {
                this.bus = bus;
                this.topic = topic;
                handler = m => Messages.Add(m);
                bus.Subscribe(topic, handler);
            }

            public void Clear() => Messages.Clear();
            public void Dispose() => bus.Unsubscribe(topic, handler);
        }
    }
}
