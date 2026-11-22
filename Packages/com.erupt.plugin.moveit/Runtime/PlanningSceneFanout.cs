using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// One planning scene the environment is shared with: a robot's MoveIt, in that robot's ROS
    /// namespace and planning frame. Holds what was last sent there for every object id (the
    /// ledger), so echoes can be told from changes.
    /// </summary>
    public sealed class PlanningSceneEndpoint
    {
        private readonly PlanningSceneFanout fanout;
        private readonly Transform ownFrame;
        private readonly string ownFrameId;
        internal readonly Dictionary<string, PlanningSceneLedgerEntry> ledger = new();
        // Objects that could not be sent when this planning scene joined (paused, carried, no
        // geometry yet); retried by PlanningSceneFanout.Tick.
        internal readonly HashSet<CollisionObjectPublisher> pending = new();
        internal Action<CollisionObjectMsg> inboundHandler;
        internal bool listening;

        /// <summary>ROS namespace of this planning scene; empty for the root namespace.</summary>
        public string Namespace { get; }
        /// <summary>The robot whose planning scene this is; null for a planning scene mirrored without a robot.</summary>
        public RobotInstance Robot { get; }
        /// <summary>The bus scoped to <see cref="Namespace"/>: relative names land in this planning scene.</summary>
        public IRosBus Ros { get; }
        /// <summary>
        /// This robot's attach mirror, when it has one (the MoveIt plugin sets it). It says which
        /// place poses this planning scene still owes, so another planning scene's message is
        /// not taken for one.
        /// </summary>
        public AttachedCollisionObjectListener Attached { get; set; }

        /// <summary>The Unity pose of this planning scene's planning frame; null = Unity world space.</summary>
        public Transform Frame => Robot != null ? Robot.PlanningFrame : ownFrame;
        /// <summary>The frame id poses sent here are stamped with.</summary>
        public string FrameId => Robot != null ? Robot.PlanningFrameId : ownFrameId;

        internal PlanningSceneEndpoint(PlanningSceneFanout fanout, RobotInstance robot, IRosBus bus, string rosNamespace,
            Transform frame, string frameId)
        {
            this.fanout = fanout;
            Robot = robot;
            Ros = bus;
            Namespace = rosNamespace ?? "";
            ownFrame = frame;
            ownFrameId = string.IsNullOrEmpty(frameId) ? RobotProfile.DefaultPlanningFrame : frameId;
        }

        internal PlanningSceneLedgerEntry Entry(string id)
        {
            if (!ledger.TryGetValue(id, out PlanningSceneLedgerEntry entry)) ledger[id] = entry = new PlanningSceneLedgerEntry();
            return entry;
        }

        /// <summary>A Unity world pose expressed in this planning scene's frame, in ROS axes.</summary>
        public PoseMsg ToRos(Vector3 position, Quaternion rotation)
        {
            Transform frame = Frame;
            if (frame != null)
            {
                position = frame.InverseTransformPoint(position);
                rotation = Quaternion.Inverse(frame.rotation) * rotation;
            }
            return new PoseMsg
            {
                position = RosUnityConversion.UnityToRosPosition(position),
                orientation = RosUnityConversion.UnityToRosQuaternion(rotation)
            };
        }

        /// <summary>A pose reported by this planning scene as a Unity world pose.</summary>
        public void ToUnity(PoseMsg pose, out Vector3 position, out Quaternion rotation)
        {
            position = pose != null ? RosUnityConversion.RosToUnityPosition(pose.position) : Vector3.zero;
            rotation = pose != null ? RosUnityConversion.RosToUnityQuaternion(pose.orientation) : Quaternion.identity;
            Transform frame = Frame != null ? Frame : fanout.DefaultFrame;
            if (frame == null) return;
            position = frame.TransformPoint(position);
            rotation = frame.rotation * rotation;
        }

        // Frames this planning scene reported poses in that could not be placed; each is said once.
        private readonly HashSet<string> unknownFrames = new();

        /// <summary>
        /// The Unity pose of a ROS frame this planning scene reports poses in. MoveIt keeps and
        /// reports its world in its own planning frame, the root of the robot model (the robot's
        /// base link, say), whatever frame the objects were sent in; that is this planning scene's
        /// <see cref="FrameId"/> only when the robot stands at that frame's origin. Known frames:
        /// the planning frame itself and the links of this planning scene's robot.
        /// </summary>
        /// <returns>False for a frame that is neither; the caller then has nothing better than the planning frame.</returns>
        public bool TryResolveFrame(string frameId, out Vector3 position, out Quaternion rotation)
        {
            Transform frame = Frame != null ? Frame : fanout.DefaultFrame;
            position = frame != null ? frame.position : Vector3.zero;
            rotation = frame != null ? frame.rotation : Quaternion.identity;
            string name = TrimFrame(frameId);
            if (name.Length == 0 || name == TrimFrame(FrameId)) return true;

            IRobotModel model = Robot != null ? Robot.Model : null;
            if (model == null) return false;
            if (!model.TryFindLinkTransform(name, out Transform link))
            {
                string local = Robot.Names.ToLocal(name);
                if (local == name || !model.TryFindLinkTransform(local, out link)) return false;
            }
            position = link.position;
            rotation = AttachedObjectPlacement.WorldConventionRotation(link);
            return true;
        }

        private static string TrimFrame(string frameId) => (frameId ?? "").Trim().TrimStart('/');

        /// <summary>
        /// An inbound object with its pose expressed in this planning scene's <see cref="FrameId"/>,
        /// whatever frame the message names. The message itself is left as it is; a copy is
        /// returned when the pose had to be converted.
        /// </summary>
        internal CollisionObjectMsg InPlanningFrame(CollisionObjectMsg co)
        {
            string reported = TrimFrame(co.header?.frame_id);
            if (co.pose == null || co.operation == CollisionObjectMsg.REMOVE || reported.Length == 0 || reported == TrimFrame(FrameId)) return co;
            if (!TryResolveFrame(reported, out Vector3 framePosition, out Quaternion frameRotation))
            {
                if (unknownFrames.Add(reported))
                    Debug.LogWarning($"[CO Listener] Planning scene '{Namespace}' reports poses in frame '{reported}', which is neither its planning frame " +
                                     $"'{FrameId}' nor a link of its robot; they are read as if in '{FrameId}' (said once per frame).");
                return co;
            }

            Vector3 position = framePosition + frameRotation * RosUnityConversion.RosToUnityPosition(co.pose.position);
            Quaternion rotation = frameRotation * RosUnityConversion.RosToUnityQuaternion(co.pose.orientation);
            return new CollisionObjectMsg
            {
                header = new HeaderMsg { frame_id = FrameId, stamp = co.header.stamp },
                pose = ToRos(position, rotation),
                id = co.id,
                type = co.type,
                primitives = co.primitives,
                primitive_poses = co.primitive_poses,
                meshes = co.meshes,
                mesh_poses = co.mesh_poses,
                planes = co.planes,
                plane_poses = co.plane_poses,
                subframe_names = co.subframe_names,
                subframe_poses = co.subframe_poses,
                operation = co.operation
            };
        }

        /// <summary>
        /// Remove one object no Unity object owns from this planning scene, for example one an
        /// earlier session left behind.
        /// </summary>
        public void PublishRemove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            fanout.SendRemove(this, id);
        }

        private const string ApplyPlanningSceneService = "apply_planning_scene";

        /// <summary>
        /// Detaches every body from this robot in its planning scene with one robot-state diff
        /// on <c>apply_planning_scene</c> (an empty id and an empty link name mean "all attached
        /// bodies"). An execution cancelled between its attach and its detach leaves the object in
        /// MoveIt's hand, and every later grasp then fails its IK until it is detached. The bodies
        /// land in the world at the gripper's pose; their next ADD moves them. This is not a
        /// remove-all: world objects are untouched, and so are the other robots.
        /// <paramref name="done"/> gets the service's success flag; false also covers "ROS not connected".
        /// </summary>
        public void DetachAllFromRobot(Action<bool> done = null)
        {
            if (Ros == null || Ros.HasConnectionError)
            {
                Debug.LogWarning("[PlanningScene] Cannot detach from the robot: ROS not connected.");
                done?.Invoke(false);
                return;
            }

            var detachAll = new AttachedCollisionObjectMsg
            {
                link_name = "",
                @object = new CollisionObjectMsg
                {
                    id = "",
                    operation = CollisionObjectMsg.REMOVE,
                    header = new HeaderMsg { frame_id = FrameId, stamp = CollisionObjectPublisher.GetRosTimestamp() }
                }
            };
            var scene = new PlanningSceneMsg { is_diff = true };
            scene.robot_state.is_diff = true;
            scene.robot_state.attached_collision_objects = new[] { detachAll };
            try
            {
                Ros.RegisterRosService<ApplyPlanningSceneRequest, ApplyPlanningSceneResponse>(ApplyPlanningSceneService);
                Ros.SendServiceMessage<ApplyPlanningSceneResponse>(ApplyPlanningSceneService,
                    new ApplyPlanningSceneRequest(scene), response =>
                    {
                        bool ok = response != null && response.success;
                        Debug.Log($"[PlanningScene] Detached every body from the robot in planning scene '{Namespace}': success={ok}");
                        done?.Invoke(ok);
                    });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlanningScene] Detaching from the robot failed: {e.Message}");
                done?.Invoke(false);
            }
        }
    }

    /// <summary>What one planning scene was last told, or last told us, about one object id.</summary>
    internal sealed class PlanningSceneLedgerEntry
    {
        private const int MaxSent = 8;
        private const float PositionTolerance = 1e-3f;
        private const float RotationToleranceDegrees = 0.1f;
        // How long after a REMOVE was sent an inbound REMOVE still counts as its echo.
        private const float RemoveEchoSeconds = 5f;

        /// <summary>That planning scene holds the object, as far as Unity knows.</summary>
        public bool Known;
        /// <summary>Signature of the geometry it holds; null when Unity never saw or sent it.</summary>
        public string Geometry;
        /// <summary>A change from a planning scene that does not own the object was ignored; said once.</summary>
        public bool WarnedConflict;

        private bool hasCurrent;
        private PoseMsg current;
        // Poses sent since the last change that came from that planning scene, oldest first:
        // their echoes may still be on the way.
        private readonly List<PoseMsg> sent = new();
        private float removeSentAt = float.NegativeInfinity;

        public bool HasPose => hasCurrent;
        public PoseMsg Pose => current;

        public void RecordSent(PoseMsg pose, string geometry)
        {
            Known = true;
            hasCurrent = pose != null;
            current = pose;
            if (pose != null)
            {
                if (sent.Count == MaxSent) sent.RemoveAt(0);
                sent.Add(pose);
            }
            if (geometry != null) Geometry = geometry;
            removeSentAt = float.NegativeInfinity;
        }

        public void RecordReceived(PoseMsg pose, string geometry)
        {
            Known = true;
            hasCurrent = true;
            current = pose;
            sent.Clear();
            if (geometry != null) Geometry = geometry;
            removeSentAt = float.NegativeInfinity;
        }

        public void RecordRemoveSent(float now)
        {
            Known = false;
            hasCurrent = false;
            Geometry = null;
            sent.Clear();
            removeSentAt = now;
        }

        public void Forget()
        {
            Known = false;
            hasCurrent = false;
            Geometry = null;
            sent.Clear();
            removeSentAt = float.NegativeInfinity;
        }

        /// <summary>True (once) for the REMOVE that echoes one Unity sent a moment ago.</summary>
        public bool ConsumeRemoveEcho(float now)
        {
            if (now - removeSentAt > RemoveEchoSeconds) return false;
            removeSentAt = float.NegativeInfinity;
            return true;
        }

        /// <summary>
        /// Whether an inbound object says only what Unity already knows that planning scene to
        /// hold: the state last sent or received, or an earlier send whose echo is still arriving.
        /// </summary>
        public bool IsEcho(PoseMsg pose, string geometry)
        {
            if (!Known) return false;
            if (geometry != null && Geometry != null && geometry != Geometry) return false;
            if (hasCurrent && SamePose(current, pose)) return true;
            for (int i = sent.Count - 1; i >= 0; i--)
            {
                if (!SamePose(sent[i], pose)) continue;
                // Echoes arrive in the order the poses were sent: the earlier ones are done.
                sent.RemoveRange(0, i + 1);
                return true;
            }
            return false;
        }

        /// <summary>Whether that planning scene already holds this pose and geometry.</summary>
        public bool Holds(PoseMsg pose, string geometry) =>
            Known && hasCurrent && SamePose(current, pose) && (geometry == null || geometry == Geometry);

        public static bool SamePose(PoseMsg a, PoseMsg b)
        {
            Vector3 pa = Position(a), pb = Position(b);
            if ((pa - pb).sqrMagnitude > PositionTolerance * PositionTolerance) return false;
            return Quaternion.Angle(Rotation(a), Rotation(b)) <= RotationToleranceDegrees;
        }

        private static Vector3 Position(PoseMsg pose) =>
            pose?.position == null ? Vector3.zero : new Vector3((float)pose.position.x, (float)pose.position.y, (float)pose.position.z);

        private static Quaternion Rotation(PoseMsg pose)
        {
            QuaternionMsg q = pose?.orientation;
            // An unset (all-zero) quaternion means identity.
            if (q == null || (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0)) return Quaternion.identity;
            return new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);
        }
    }

    /// <summary>
    /// Shares Unity's one environment with every robot's own planning scene. Unity has a single
    /// set of obstacles; each robot's MoveIt has its own planning scene, in its own ROS namespace
    /// and planning frame, and the planners know nothing of each other.
    /// </summary>
    /// <remarks>
    /// Outbound: every <see cref="CollisionObjectPublisher"/> reports its object's changes here,
    /// and each change goes to every planning scene's <c>collision_object</c>, with the pose in
    /// that robot's planning frame. A planning scene that joins later receives the whole current
    /// environment; one that leaves is dropped.
    ///
    /// Inbound: each planning scene's <c>collision_objects_ros</c> is heard separately, and there
    /// is still one Unity object per id (the <see cref="CollisionObjectsListenerSimple"/> mirror).
    /// A ledger keeps, per namespace and id, what was last sent there, and each id has an owner:
    /// Unity, or the planning scene it first arrived from.
    /// <list type="bullet">
    /// <item>A message that matches what that namespace was last sent is an echo: dropped.</item>
    /// <item>An unknown id is adopted: built in Unity from that robot's planning frame, and sent to every other planning scene.</item>
    /// <item>A change from the owning planning scene is applied and sent to the others.</item>
    /// <item>A change from a planning scene that does not own the id is ignored, with one warning.</item>
    /// <item>A REMOVE Unity did not send removes the Unity object and is sent to the others, never back.</item>
    /// <item>An id a robot is carrying is ignored; that robot's place pose after the detach is applied whoever owns the id.</item>
    /// </list>
    /// Frames: everything is sent stamped with the robot's planning frame id. What comes back is
    /// in whatever frame that MoveIt keeps its world in (the root of its robot model, usually the
    /// robot's base link), and the message says which; it is brought into the planning frame
    /// before anything is compared or applied (<see cref="PlanningSceneEndpoint.InPlanningFrame"/>).
    ///
    /// With one robot in the root namespace there is one planning scene: <c>/collision_object</c>
    /// out, <c>/collision_objects_ros</c> in, the robot's planning frame, and no forwarding.
    /// </remarks>
    public sealed class PlanningSceneFanout : ICollisionObjectSink, IDisposable
    {
        private readonly List<PlanningSceneEndpoint> endpoints = new();
        private readonly List<CollisionObjectPublisher> publishers = new();
        // Which planning scene an id first arrived from. Ids that are not in here are Unity's
        // (when the mirror says so) or not known yet.
        private readonly Dictionary<string, PlanningSceneEndpoint> owners = new();
        // The last message with geometry for each id a planning scene owns, for planning scenes
        // that join later and for objects that have no publisher of their own.
        private readonly Dictionary<string, CollisionObjectMsg> remoteGeometry = new();
        private CollisionObjectsListenerSimple mirror;
        private bool disposed;

        /// <summary>The planning scenes the environment is shared with, in the order they joined.</summary>
        public IReadOnlyList<PlanningSceneEndpoint> Endpoints => endpoints;
        /// <summary>The Unity side of the mirror; null = outbound only.</summary>
        public CollisionObjectsListenerSimple Mirror => mirror;

        internal Transform DefaultFrame => mirror != null ? mirror.DefaultFrame : null;

        /// <param name="mirror">Builds the Unity objects for what the planning scenes report; null shares Unity's objects without listening.</param>
        public PlanningSceneFanout(CollisionObjectsListenerSimple mirror = null)
        {
            foreach (CollisionObjectPublisher publisher in CollisionObjectPublisher.Live) Bind(publisher);
            CollisionObjectPublisher.Started += Bind;
            if (mirror != null) SetMirror(mirror);
        }

        /// <summary>Start mirroring what the planning scenes report into Unity through this component.</summary>
        public void SetMirror(CollisionObjectsListenerSimple target)
        {
            if (ReferenceEquals(mirror, target)) return;
            if (mirror != null)
            {
                mirror.Ready -= ListenEverywhere;
                if (ReferenceEquals(mirror.Fanout, this)) mirror.Fanout = null;
                foreach (PlanningSceneEndpoint endpoint in endpoints) StopListening(endpoint);
            }
            mirror = target;
            if (mirror == null) return;
            mirror.Fanout = this;
            if (mirror.HasStarted) ListenEverywhere();
            else mirror.Ready += ListenEverywhere;
        }

        internal void MirrorDestroyed(CollisionObjectsListenerSimple destroyed)
        {
            if (ReferenceEquals(mirror, destroyed)) SetMirror(null);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CollisionObjectPublisher.Started -= Bind;
            // The objects stop being part of the planning scenes with the fan-out: each one that
            // would have reported its own destruction is removed now, while there is still
            // somewhere to send it. (Objects a planning scene owns stay there: they are the ROS
            // side's, and only their Unity copies go.)
            departing = true;
            foreach (CollisionObjectPublisher publisher in publishers.ToArray())
            {
                if (publisher == null || !ReferenceEquals(publisher.Sink, this)) continue;
                if (!publisher.suppressRemoveOnDestroy && publisher.hasBeenPublished) ObjectDestroyed(publisher);
                publisher.Bind(null);
            }
            publishers.Clear();
            SetMirror(null);
            foreach (PlanningSceneEndpoint endpoint in endpoints) StopListening(endpoint);
            endpoints.Clear();
            owners.Clear();
            remoteGeometry.Clear();
        }

        // ---- planning scenes ---------------------------------------------------------------

        /// <summary>The planning scene of a robot; null when the robot has none here.</summary>
        public PlanningSceneEndpoint EndpointFor(RobotInstance robot)
        {
            foreach (PlanningSceneEndpoint endpoint in endpoints)
                if (ReferenceEquals(endpoint.Robot, robot)) return endpoint;
            return null;
        }

        /// <summary>The planning scene in a namespace; null when there is none.</summary>
        public PlanningSceneEndpoint EndpointIn(string rosNamespace)
        {
            foreach (PlanningSceneEndpoint endpoint in endpoints)
                if (endpoint.Namespace == (rosNamespace ?? "")) return endpoint;
            return null;
        }

        /// <summary>
        /// Share the environment with a robot's planning scene (its namespace, its planning
        /// frame). The planning scene receives every object Unity has now. A second robot in a
        /// namespace that already has a planning scene shares that one: it is one MoveIt.
        /// </summary>
        public PlanningSceneEndpoint AddRobot(RobotInstance robot)
        {
            if (robot == null) throw new ArgumentNullException(nameof(robot));
            if (robot.Ros == null) throw new InvalidOperationException($"Robot '{robot.Id}' has no ROS bus.");
            PlanningSceneEndpoint existing = EndpointFor(robot) ?? EndpointIn(robot.Namespace);
            if (existing != null)
            {
                if (!ReferenceEquals(existing.Robot, robot))
                    Debug.LogWarning($"[PlanningScene] Robot '{robot.Id}' is in namespace '{robot.Namespace}', whose planning scene is already " +
                                     $"shared through '{(existing.Robot != null ? existing.Robot.Id : "no robot")}'; it uses that one.");
                return existing;
            }
            return Add(new PlanningSceneEndpoint(this, robot, robot.Ros, robot.Namespace, null, null));
        }

        /// <summary>Share the environment with a planning scene that belongs to no robot in the scene.</summary>
        /// <param name="bus">A bus scoped to the planning scene's namespace.</param>
        /// <param name="frame">The Unity pose of its planning frame; null = Unity world space.</param>
        public PlanningSceneEndpoint AddEndpoint(IRosBus bus, string rosNamespace = "", Transform frame = null, string frameId = null)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            PlanningSceneEndpoint existing = EndpointIn(rosNamespace);
            if (existing != null) return existing;
            return Add(new PlanningSceneEndpoint(this, null, bus, rosNamespace, frame, frameId));
        }

        private PlanningSceneEndpoint Add(PlanningSceneEndpoint endpoint)
        {
            endpoint.Ros.RegisterPublisher<CollisionObjectMsg>(CollisionObjectPublisher.Topic, CollisionObjectPublisher.QueueSize);
            // A planning scene that joins a running app starts with what the others were told.
            // Publishers that have not appeared anywhere yet report themselves on their next update.
            foreach (CollisionObjectPublisher publisher in publishers)
                if (publisher != null && publisher.hasBeenPublished && endpoints.Count > 0)
                    endpoint.pending.Add(publisher);
                else if (publisher != null && publisher.hasBeenPublished)
                    endpoint.Entry(publisher.objectId).Known = true;   // the first planning scene takes "already published" at its word
            endpoints.Add(endpoint);
            SendPending(endpoint);
            if (endpoints.Count > 1) SendRemoteObjects(endpoint);
            if (mirror != null && mirror.HasStarted) Listen(endpoint);
            return endpoint;
        }

        /// <summary>Stop sharing with a robot's planning scene. Nothing is sent to it; it keeps what it has.</summary>
        public void RemoveRobot(RobotInstance robot) => Remove(EndpointFor(robot));

        /// <summary>Stop sharing with a planning scene. Nothing is sent to it; it keeps what it has.</summary>
        public void Remove(PlanningSceneEndpoint endpoint)
        {
            if (endpoint == null || !endpoints.Remove(endpoint)) return;
            StopListening(endpoint);
            endpoint.pending.Clear();
            // Objects that came from it stay in Unity and in the other planning scenes; the next
            // planning scene to report one of them becomes its owner.
            var orphaned = new List<string>();
            foreach (var kv in owners)
                if (ReferenceEquals(kv.Value, endpoint)) orphaned.Add(kv.Key);
            foreach (string id in orphaned) owners.Remove(id);
        }

        /// <summary>
        /// Leave a planning scene for good because the Unity scene it was shared from is going
        /// away (a scene reset, the app closing): what Unity put into it is taken out, then the
        /// sharing stops. Objects that planning scene owns are the ROS side's and stay. Use
        /// <see cref="Remove"/> for a robot that leaves while the scene goes on.
        /// </summary>
        public void Leave(PlanningSceneEndpoint endpoint)
        {
            if (endpoint == null || !endpoints.Contains(endpoint)) return;
            foreach (CollisionObjectPublisher publisher in publishers)
            {
                // Destroyed publishers are still read here (their fields outlive the Unity
                // object): on a scene unload every object is disabled before any is destroyed.
                if (ReferenceEquals(publisher, null) || publisher.suppressRemoveOnDestroy || !publisher.hasBeenPublished) continue;
                if (owners.ContainsKey(publisher.objectId) || IsCarriedIn(publisher, endpoint)) continue;
                if (!endpoint.Entry(publisher.objectId).Known) continue;
                SendRemove(endpoint, publisher.objectId);
                departed[DepartedKey(endpoint, publisher.objectId)] = Time.realtimeSinceStartup;
            }
            Remove(endpoint);
        }

        /// <summary>Retry what could not be sent when a planning scene joined (objects that were paused or carried then). Cheap when nothing is waiting.</summary>
        public void Tick()
        {
            // While nothing can be delivered the objects wait, as their own changes do.
            if (Suspended) return;
            foreach (PlanningSceneEndpoint endpoint in endpoints)
                if (endpoint.pending.Count > 0) SendPending(endpoint);
        }

        private readonly List<CollisionObjectPublisher> sentNow = new();

        private void SendPending(PlanningSceneEndpoint endpoint)
        {
            if (endpoint.pending.Count == 0) return;
            sentNow.Clear();
            foreach (CollisionObjectPublisher publisher in endpoint.pending)
            {
                if (publisher == null) { sentNow.Add(publisher); continue; }
                if (publisher.pausePublishing || IsCarriedIn(publisher, endpoint)) continue;
                if (endpoint.Entry(publisher.objectId).Known || SendObject(endpoint, publisher, withGeometry: true))
                    sentNow.Add(publisher);
            }
            foreach (CollisionObjectPublisher publisher in sentNow) endpoint.pending.Remove(publisher);
        }

        // Objects a planning scene owns that have no publisher in Unity (planes) reach a planning
        // scene that joins later from the message they arrived in.
        private void SendRemoteObjects(PlanningSceneEndpoint endpoint)
        {
            foreach (var kv in remoteGeometry)
            {
                if (endpoint.Entry(kv.Key).Known || HasPublisher(kv.Key)) continue;
                if (!owners.TryGetValue(kv.Key, out PlanningSceneEndpoint owner) || ReferenceEquals(owner, endpoint)) continue;
                Forward(kv.Value, owner, only: endpoint);
            }
        }

        private bool HasPublisher(string id)
        {
            foreach (CollisionObjectPublisher publisher in publishers)
                if (publisher != null && publisher.objectId == id) return true;
            return false;
        }

        // ---- Unity → planning scenes ---------------------------------------------------------

        private void Bind(CollisionObjectPublisher publisher)
        {
            if (disposed || publisher == null || publishers.Contains(publisher)) return;
            publishers.Add(publisher);
            publisher.Bind(this);
            // An object that says it is already published (one mirrored from a planning scene)
            // is taken at its word; what a planning scene really holds is corrected by the
            // first geometry sent there.
            if (publisher.hasBeenPublished)
                foreach (PlanningSceneEndpoint endpoint in endpoints)
                    endpoint.Entry(publisher.objectId).Known = true;
        }

        private static bool IsCarriedIn(CollisionObjectPublisher publisher, PlanningSceneEndpoint endpoint) =>
            publisher.attachedToRobot && (publisher.attachedNamespace == null || publisher.attachedNamespace == endpoint.Namespace);

        /// <summary>
        /// True while nothing can be delivered (the ROS endpoint is away, or the connection is
        /// only just up and has not registered its topics yet). Object changes then stay pending
        /// in their publishers, and the planning scenes are not asked what they hold; the MoveIt
        /// plugin clears it and calls <see cref="Resync()"/> when ROS is there.
        /// </summary>
        public bool Suspended { get; set; }

        bool ICollisionObjectSink.ObjectChanged(CollisionObjectPublisher source, bool geometryChanged)
        {
            // With nowhere to send it the change stays pending in the publisher.
            if (endpoints.Count == 0 || Suspended) return false;
            foreach (PlanningSceneEndpoint endpoint in endpoints)
            {
                // The robot carrying the object has it as an attached body; a world object with
                // the same id would corrupt that planning scene. The others see it move.
                if (IsCarriedIn(source, endpoint)) continue;
                bool withGeometry = geometryChanged || !endpoint.Entry(source.objectId).Known;
                if (!SendObject(endpoint, source, withGeometry)) return false;
                endpoint.pending.Remove(source);
            }
            return true;
        }

        void ICollisionObjectSink.ObjectDestroyed(CollisionObjectPublisher source) => ObjectDestroyed(source);

        // True while the fan-out itself is going away (the scene is unloaded, the plugin unregistered).
        private bool departing;

        // REMOVEs sent while a scene was going away, by "namespace\nid", with the time. Their
        // echoes arrive after the next scene has set up its own fan-out, which has never heard of
        // them: without this memory it would take each one for a removal made on the ROS side and
        // delete the object the new scene has just published under the same id.
        private static readonly Dictionary<string, float> departed = new();
        private const float DepartedEchoSeconds = 5f;

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => departed.Clear();

        private static string DepartedKey(PlanningSceneEndpoint endpoint, string id) => endpoint.Namespace + "\n" + id;

        private static bool ConsumeDepartedEcho(PlanningSceneEndpoint endpoint, string id)
        {
            if (departed.Count == 0) return false;
            string key = DepartedKey(endpoint, id);
            if (!departed.TryGetValue(key, out float sentAt)) return false;
            departed.Remove(key);
            return Time.realtimeSinceStartup - sentAt <= DepartedEchoSeconds;
        }

        private void ObjectDestroyed(CollisionObjectPublisher source)
        {
            publishers.Remove(source);
            // An object that goes because its scene is unloaded (a scene reset, the app closing)
            // is not a deletion the user made. What Unity put into the planning scenes is taken
            // out again; an object a planning scene owns is the ROS side's and stays.
            bool unloading = !source.gameObject.scene.isLoaded;
            bool leaving = departing || unloading;
            if (leaving && owners.ContainsKey(source.objectId))
            {
                foreach (PlanningSceneEndpoint endpoint in endpoints) endpoint.pending.Remove(source);
                return;
            }
            foreach (PlanningSceneEndpoint endpoint in endpoints)
            {
                endpoint.pending.Remove(source);
                if (IsCarriedIn(source, endpoint)) continue;
                SendRemove(endpoint, source.objectId);
                // Only a scene that is unloaded is followed by another that will hear the echo.
                if (unloading) departed[DepartedKey(endpoint, source.objectId)] = Time.realtimeSinceStartup;
            }
            if (!HasPublisher(source.objectId))
            {
                owners.Remove(source.objectId);
                remoteGeometry.Remove(source.objectId);
            }
        }

        // ADD (with the object's shapes) or MOVE (pose only) for one publisher's object.
        private bool SendObject(PlanningSceneEndpoint endpoint, CollisionObjectPublisher source, bool withGeometry)
        {
            Transform t = source.transform;
            var msg = new CollisionObjectMsg
            {
                id = source.objectId,
                header = Header(endpoint),
                operation = CollisionObjectMsg.MOVE,
                pose = endpoint.ToRos(t.position, t.rotation)
            };

            bool sendsMesh = false;
            if (withGeometry)
            {
                msg.operation = CollisionObjectMsg.ADD;
                if (!source.TryBuildGeometry(msg, out sendsMesh)) return false;
            }
            if (!Publish(endpoint, msg, sendsMesh)) return false;
            endpoint.Entry(source.objectId).RecordSent(msg.pose, withGeometry ? Signature(msg) : null);
            return true;
        }

        internal void SendRemove(PlanningSceneEndpoint endpoint, string id)
        {
            var msg = new CollisionObjectMsg { id = id, header = Header(endpoint), operation = CollisionObjectMsg.REMOVE };
            if (Publish(endpoint, msg, false)) endpoint.Entry(id).RecordRemoveSent(Time.unscaledTime);
        }

        private static bool Publish(PlanningSceneEndpoint endpoint, CollisionObjectMsg msg, bool large)
        {
            try
            {
                // Meshes are large: skip the trailing pad the connector adds to small messages.
                if (large) endpoint.Ros.Publish(CollisionObjectPublisher.Topic, msg, false);
                else endpoint.Ros.Publish(CollisionObjectPublisher.Topic, msg);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to publish collision object '{msg.id}' to planning scene '{endpoint.Namespace}': {e.Message}");
                return false;
            }
        }

        private static HeaderMsg Header(PlanningSceneEndpoint endpoint) =>
            new HeaderMsg { frame_id = endpoint.FrameId, stamp = CollisionObjectPublisher.GetRosTimestamp() };

        /// <summary>
        /// A robot has let go of an object: bring every planning scene up to date with where it
        /// is now. The robot's own planning scene already has the pose it reported; the others
        /// were told while it was carried, but not about the last stretch.
        /// </summary>
        internal void SyncAfterCarry(CollisionObjectPublisher publisher, string carrierNamespace)
        {
            if (publisher == null || publisher.pausePublishing) return;
            Transform t = publisher.transform;
            foreach (PlanningSceneEndpoint endpoint in endpoints)
            {
                PlanningSceneLedgerEntry entry = endpoint.Entry(publisher.objectId);
                PoseMsg pose = endpoint.ToRos(t.position, t.rotation);
                bool carrier = carrierNamespace == null || endpoint.Namespace == carrierNamespace;
                if (carrier && entry.Known)
                {
                    // The pose comes from that planning scene itself, or (after a release-all)
                    // from Unity with nothing to send, exactly as with a single planning scene.
                    entry.RecordReceived(pose, null);
                    continue;
                }
                // An object that never reached this planning scene only reaches it once it has
                // appeared at all; until then its own first report covers every planning scene.
                if (!entry.Known && !publisher.hasBeenPublished) continue;
                if (entry.Holds(pose, null)) continue;
                SendObject(endpoint, publisher, withGeometry: !entry.Known);
            }
        }

        // ---- planning scenes → Unity ---------------------------------------------------------

        private void ListenEverywhere()
        {
            foreach (PlanningSceneEndpoint endpoint in endpoints) Listen(endpoint);
        }

        private void Listen(PlanningSceneEndpoint endpoint)
        {
            if (endpoint.listening || mirror == null) return;
            // Subscribe before requesting the snapshot so an update that occurs while the
            // service request is in flight cannot be missed.
            endpoint.inboundHandler = msg => OnInbound(endpoint, msg);
            endpoint.Ros.Subscribe(mirror.topic, endpoint.inboundHandler);
            endpoint.listening = true;
            // While suspended the request would go nowhere; the resync that follows asks.
            if (mirror.requestInitialPlanningScene && !Suspended) RequestInitialPlanningScene(endpoint);
        }

        private void StopListening(PlanningSceneEndpoint endpoint)
        {
            if (!endpoint.listening) return;
            endpoint.listening = false;
            if (mirror != null) endpoint.Ros.Unsubscribe(mirror.topic, endpoint.inboundHandler);
            endpoint.inboundHandler = null;
        }

        /// <summary>
        /// Start over with every planning scene after the connection to ROS was down (the app
        /// started before ROS did, the endpoint or the whole ROS side restarted). Whatever was
        /// "sent" meanwhile went nowhere, and a restarted MoveIt has forgotten the rest: the
        /// ledger is cleared, every Unity object is sent again with its geometry, and each
        /// planning scene is asked what it holds. For the objects a planning scene owns, that
        /// answer is the truth: one that is no longer in it is removed from Unity too.
        /// </summary>
        public void Resync()
        {
            if (disposed) return;
            foreach (PlanningSceneEndpoint endpoint in endpoints.ToArray()) Resync(endpoint);
        }

        /// <summary>As <see cref="Resync()"/>, for one planning scene (its MoveIt came up late or was restarted).</summary>
        public void Resync(PlanningSceneEndpoint endpoint)
        {
            if (disposed || endpoint == null || !endpoints.Contains(endpoint)) return;
            endpoint.ledger.Clear();
            endpoint.pending.Clear();
            foreach (CollisionObjectPublisher publisher in publishers)
            {
                if (publisher == null || owners.ContainsKey(publisher.objectId) || !publisher.hasBeenPublished) continue;
                // Sent again in full to this planning scene alone; the others hold it already.
                if (publisher.pausePublishing || IsCarriedIn(publisher, endpoint) || !SendObject(endpoint, publisher, withGeometry: true))
                    endpoint.pending.Add(publisher);
            }
            if (endpoints.Count > 1) SendRemoteObjects(endpoint);
            if (mirror != null && mirror.requestInitialPlanningScene && endpoint.listening)
                RequestInitialPlanningScene(endpoint, authoritative: true);
        }

        /// <summary>
        /// The frame of a planning scene is somewhere else in Unity: its robot's base was put
        /// down at another place (<see cref="RobotInstance.PlaceBase(Pose)"/>) and the robot's
        /// planning frame went with it. Every pose that planning scene holds was expressed in
        /// that frame, so:
        /// <list type="bullet">
        /// <item>what that planning scene owns (objects its ROS side put there) is fixed in its
        /// frame: the Unity objects move with the frame, and the other planning scenes are told;</item>
        /// <item>everything else stayed where it was in Unity, which is another pose in the
        /// moved frame: each object is sent to this planning scene again (pose only where it
        /// already has the shapes). The other planning scenes are sent nothing.</item>
        /// </list>
        /// An object that cannot be sent now (paused, or ROS is away) is sent in full as soon as
        /// it can be. Objects the robot of this planning scene carries go with its links.
        /// </summary>
        public void FrameMoved(PlanningSceneEndpoint endpoint)
        {
            if (disposed || endpoint == null || !endpoints.Contains(endpoint)) return;

            if (mirror != null)
            {
                var owned = new List<string>();
                foreach (var kv in owners)
                    if (ReferenceEquals(kv.Value, endpoint)) owned.Add(kv.Key);
                foreach (string id in owned)
                {
                    PlanningSceneLedgerEntry entry = endpoint.Entry(id);
                    if (!entry.HasPose || mirror.attachedIds.Contains(id)) continue;
                    if (!mirror.TryGetObject(id, out GameObject go) || go == null) continue;
                    mirror.ApplyPoseFromRos(go, entry.Pose, endpoint.Frame);
                    Forward(new CollisionObjectMsg { id = id, header = Header(endpoint), operation = CollisionObjectMsg.MOVE, pose = entry.Pose }, endpoint);
                    mirror.RaiseObjectUpdated(id);
                }
            }

            foreach (CollisionObjectPublisher publisher in publishers)
            {
                if (publisher == null || !publisher.hasBeenPublished) continue;
                if (owners.TryGetValue(publisher.objectId, out PlanningSceneEndpoint owner) && ReferenceEquals(owner, endpoint)) continue;
                if (IsCarriedIn(publisher, endpoint)) continue;
                PlanningSceneLedgerEntry entry = endpoint.Entry(publisher.objectId);
                Transform t = publisher.transform;
                if (!Suspended && !publisher.pausePublishing)
                {
                    if (entry.Known && entry.Holds(endpoint.ToRos(t.position, t.rotation), null)) continue;
                    if (SendObject(endpoint, publisher, withGeometry: !entry.Known)) continue;
                }
                // Not deliverable now: what this planning scene holds is wrong, so it gets the
                // object in full when it can be sent (Tick).
                entry.Forget();
                endpoint.pending.Add(publisher);
            }

            // Objects another planning scene owns that have no publisher in Unity (planes).
            foreach (var kv in remoteGeometry)
            {
                if (HasPublisher(kv.Key)) continue;
                if (!owners.TryGetValue(kv.Key, out PlanningSceneEndpoint owner) || ReferenceEquals(owner, endpoint)) continue;
                Forward(kv.Value, owner, only: endpoint);
            }
        }

        private void RequestInitialPlanningScene(PlanningSceneEndpoint endpoint, bool authoritative = false)
        {
            try
            {
                string service = mirror.planningSceneServiceTopic;
                endpoint.Ros.RegisterRosService<GetPlanningSceneRequest, GetPlanningSceneResponse>(service);

                uint components = PlanningSceneComponentsMsg.WORLD_OBJECT_NAMES
                                | PlanningSceneComponentsMsg.WORLD_OBJECT_GEOMETRY;
                var request = new GetPlanningSceneRequest(new PlanningSceneComponentsMsg(components));
                endpoint.Ros.SendServiceMessage<GetPlanningSceneResponse>(service, request, response =>
                {
                    CollisionObjectMsg[] objects = response?.scene?.world?.collision_objects;
                    if (objects == null)
                    {
                        Debug.LogWarning("[CO Listener] Initial planning-scene response contained no world object list.");
                        return;
                    }
                    // A request the endpoint could not deliver comes back as an empty message,
                    // which reads like an empty planning scene. MoveIt always names its robot
                    // model; an answer that does not is no answer, and nothing is concluded from it.
                    if (objects.Length == 0 && string.IsNullOrEmpty(response.scene.robot_model_name))
                    {
                        Debug.LogWarning($"[CO Listener] Planning scene '{endpoint.Namespace}' did not answer {service}; what it holds is not known yet.");
                        return;
                    }
                    if (!endpoint.listening) return;
                    Debug.Log($"[CO Listener] Applying {(authoritative ? "the" : "initial")} planning-scene snapshot ({objects.Length} objects).");
                    foreach (CollisionObjectMsg collisionObject in objects) OnInbound(endpoint, collisionObject);
                    if (authoritative) RemoveWhatIsGone(endpoint, objects);
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CO Listener] Could not request the initial planning scene: {e.Message}");
            }
        }

        // After a resync: an object this planning scene owned and no longer holds is gone, in
        // Unity and in the other planning scenes (they may never have heard of its removal).
        private void RemoveWhatIsGone(PlanningSceneEndpoint endpoint, CollisionObjectMsg[] held)
        {
            var present = new HashSet<string>();
            foreach (CollisionObjectMsg co in held) present.Add(co.id);
            var gone = new List<string>();
            foreach (var kv in owners)
                if (ReferenceEquals(kv.Value, endpoint) && !present.Contains(kv.Key) && !mirror.attachedIds.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string id in gone)
            {
                // The others are told whatever the ledger says they hold: it has just been
                // cleared, and they may still have the object from before the outage.
                foreach (PlanningSceneEndpoint other in endpoints)
                    if (!ReferenceEquals(other, endpoint)) other.Entry(id).Known = true;
                OnInbound(endpoint, new CollisionObjectMsg { id = id, header = Header(endpoint), operation = CollisionObjectMsg.REMOVE });
            }
        }

        // One planning scene reports an object. See the class remarks for the rules.
        internal void OnInbound(PlanningSceneEndpoint endpoint, CollisionObjectMsg co)
        {
            if (mirror == null || co == null) return;
            string id = co.id;
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning("[CO Listener] Empty id; ignoring.");
                return;
            }

            // MoveIt reports in its own planning frame (the robot model's root); from here on
            // the pose is in the frame this planning scene is addressed in.
            co = endpoint.InPlanningFrame(co);

            // A robot is carrying this object: MoveIt takes it out of the world while it is
            // attached, and nothing on this topic may move, rebuild or delete it meanwhile.
            if (mirror.attachedIds.Contains(id))
            {
                Debug.Log($"[CO Listener] Ignoring op={co.operation} for attached object id={id}");
                return;
            }

            PlanningSceneLedgerEntry entry = endpoint.Entry(id);
            bool remove = co.operation == CollisionObjectMsg.REMOVE;
            if (remove && (entry.ConsumeRemoveEcho(Time.unscaledTime) || ConsumeDepartedEcho(endpoint, id))) return;

            PoseMsg pose = co.pose ?? IdentityPose;
            string geometry = HasGeometry(co) ? Signature(co) : null;
            bool unityOwned = mirror.TryGetUnityOwnedObject(id, out GameObject unityObject);

            // The robot of this planning scene has just put the object down: the pose it reports
            // is where the object is, whoever owns the id.
            if (!remove && AwaitsPlacePose(endpoint, id))
            {
                mirror.awaitingDetachPose.Remove(id);
                if (unityOwned)
                {
                    if (co.pose == null) return;
                    // Keep the Unity-owned geometry; only the pose is the robot's.
                    mirror.ApplyPoseFromRos(unityObject, co.pose, endpoint.Frame);
                    entry.RecordReceived(pose, null);
                    foreach (CollisionObjectPublisher publisher in unityObject.GetComponentsInChildren<CollisionObjectPublisher>(true))
                        SyncAfterCarry(publisher, endpoint.Namespace);
                }
                else
                {
                    mirror.Apply(co, endpoint.Frame);
                    entry.RecordReceived(pose, geometry);
                    Remember(co);
                    Forward(co, endpoint);
                }
                mirror.RaiseObjectUpdated(id);
                return;
            }

            if (unityOwned)
            {
                if (remove)
                {
                    // A REMOVE Unity did not send: the object goes, here and in the other planning scenes.
                    mirror.DestroyUnityOwned(id, unityObject);
                    entry.Forget();
                    ForwardRemove(id, endpoint);
                    return;
                }
                // What that planning scene held before Unity told it anything (an earlier
                // session's copy) is not a change: Unity's object replaces it when it is sent.
                if (!entry.Known) Debug.Log($"[CO Listener] Skipping '{id}' from planning scene '{endpoint.Namespace}': Unity owns that id.");
                else if (!entry.IsEcho(pose, geometry)) WarnConflict(endpoint, entry, id, "Unity owns it");
                return;
            }

            if (!remove && mirror.IsStaleClaimedId(id))
            {
                Debug.LogWarning($"[CO Listener] '{id}' has a claimed id prefix ({mirror.ClaimedPrefixes}) but no live Unity object owns it; removing it from the planning scene.");
                mirror.RemoveMirrored(id);
                owners.Remove(id);
                remoteGeometry.Remove(id);
                SendRemove(endpoint, id);
                return;
            }

            if (remove)
            {
                Debug.Log($"[CO Listener] Removing object id={id}");
                mirror.RemoveMirrored(id);
                entry.Forget();
                owners.Remove(id);
                remoteGeometry.Remove(id);
                ForwardRemove(id, endpoint);
                return;
            }

            if (owners.TryGetValue(id, out PlanningSceneEndpoint owner) && !ReferenceEquals(owner, endpoint))
            {
                string ownedBy = $"planning scene '{owner.Namespace}' owns it";
                if (!entry.IsEcho(pose, geometry)) WarnConflict(endpoint, entry, id, ownedBy);
                return;
            }

            // The owner repeating what Unity already has (the echo of a move made in Unity, or
            // the same state again) changes nothing.
            if (owner != null && entry.IsEcho(pose, geometry)) return;

            Debug.Log($"[CO Listener] Adding/Appending/Moving object id={id}");
            owners[id] = endpoint;
            mirror.Apply(co, endpoint.Frame);
            entry.RecordReceived(pose, geometry);
            Remember(co);
            Forward(co, endpoint);
            mirror.RaiseObjectUpdated(id);
        }

        private bool AwaitsPlacePose(PlanningSceneEndpoint endpoint, string id) =>
            mirror.awaitingDetachPose.Contains(id) && (endpoint.Attached == null || endpoint.Attached.IsAwaitingPlacePose(id));

        private void Remember(CollisionObjectMsg co)
        {
            if (HasGeometry(co)) remoteGeometry[co.id] = co;
        }

        private static void WarnConflict(PlanningSceneEndpoint endpoint, PlanningSceneLedgerEntry entry, string id, string why)
        {
            if (entry.WarnedConflict) return;
            entry.WarnedConflict = true;
            Debug.LogWarning($"[CO Listener] Planning scene '{endpoint.Namespace}' reports a change to '{id}', but {why}; ignored (said once per object).");
        }

        // What one planning scene reported, sent to the others in their own frames.
        private void Forward(CollisionObjectMsg co, PlanningSceneEndpoint from, PlanningSceneEndpoint only = null)
        {
            if (endpoints.Count < 2) return;
            from.ToUnity(co.pose ?? IdentityPose, out Vector3 position, out Quaternion rotation);
            bool hasGeometry = HasGeometry(co);
            string geometry = hasGeometry ? Signature(co) : null;

            foreach (PlanningSceneEndpoint other in endpoints)
            {
                if (ReferenceEquals(other, from) || (only != null && !ReferenceEquals(other, only))) continue;
                PlanningSceneLedgerEntry entry = other.Entry(co.id);
                PoseMsg pose = other.ToRos(position, rotation);
                if (entry.Holds(pose, geometry)) continue;

                CollisionObjectMsg source = co;
                if (!hasGeometry && !entry.Known)
                {
                    // A pose-only update for an object that planning scene never got: it needs the shapes too.
                    if (!remoteGeometry.TryGetValue(co.id, out source)) continue;
                }
                bool sendsGeometry = HasGeometry(source);
                var msg = new CollisionObjectMsg
                {
                    id = co.id,
                    header = Header(other),
                    pose = pose,
                    // An APPEND only makes sense where the object already is.
                    operation = !sendsGeometry ? CollisionObjectMsg.MOVE
                              : entry.Known && co.operation == CollisionObjectMsg.APPEND ? CollisionObjectMsg.APPEND
                              : CollisionObjectMsg.ADD,
                    type = source.type
                };
                if (sendsGeometry)
                {
                    msg.primitives = source.primitives;
                    msg.primitive_poses = source.primitive_poses;
                    msg.meshes = source.meshes;
                    msg.mesh_poses = source.mesh_poses;
                    msg.planes = source.planes;
                    msg.plane_poses = source.plane_poses;
                    msg.subframe_names = source.subframe_names;
                    msg.subframe_poses = source.subframe_poses;
                }
                if (Publish(other, msg, (source.meshes?.Length ?? 0) > 0 && sendsGeometry))
                    entry.RecordSent(pose, sendsGeometry ? Signature(msg) : null);
            }
        }

        private void ForwardRemove(string id, PlanningSceneEndpoint from)
        {
            foreach (PlanningSceneEndpoint other in endpoints)
                if (!ReferenceEquals(other, from) && other.Entry(id).Known) SendRemove(other, id);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static readonly PoseMsg IdentityPose = new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1));

        private static bool HasGeometry(CollisionObjectMsg co) =>
            (co.primitives?.Length ?? 0) > 0 || (co.meshes?.Length ?? 0) > 0 || (co.planes?.Length ?? 0) > 0;

        // What the shapes of an object are, in a form that survives the trip through a planning
        // scene: shape poses that are missing or identity read the same, since MoveIt fills them in.
        internal static string Signature(CollisionObjectMsg co)
        {
            var sb = new StringBuilder();
            foreach (SolidPrimitiveMsg primitive in co.primitives ?? Array.Empty<SolidPrimitiveMsg>())
            {
                sb.Append('P').Append(primitive.type);
                foreach (double d in primitive.dimensions ?? Array.Empty<double>())
                    sb.Append(':').Append(d.ToString("F4", CultureInfo.InvariantCulture));
            }
            foreach (MeshMsg mesh in co.meshes ?? Array.Empty<MeshMsg>())
                sb.Append('M').Append(mesh.vertices?.Length ?? 0).Append(':').Append(mesh.triangles?.Length ?? 0);
            sb.Append("PL").Append(co.planes?.Length ?? 0);
            AppendPoses(sb, co.primitive_poses);
            AppendPoses(sb, co.mesh_poses);
            AppendPoses(sb, co.plane_poses);
            return sb.ToString();
        }

        private static void AppendPoses(StringBuilder sb, PoseMsg[] poses)
        {
            sb.Append('|');
            if (poses == null) return;
            for (int i = 0; i < poses.Length; i++)
            {
                PoseMsg p = poses[i];
                if (p == null || PlanningSceneLedgerEntry.SamePose(p, IdentityPose)) continue;
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0}@{1:F4},{2:F4},{3:F4},{4:F3},{5:F3},{6:F3},{7:F3};",
                    i, p.position.x, p.position.y, p.position.z,
                    p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w);
            }
        }
    }
}
