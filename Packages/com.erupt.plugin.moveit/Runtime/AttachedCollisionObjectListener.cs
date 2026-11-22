using Erupt.Ros;
using Erupt.Environment;
using Erupt.Interaction;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using RosMessageTypes.Moveit;
using Erupt.Robot;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// Mirrors MoveIt's attach state onto Unity GameObjects. The ROS-side
    /// planning_scene_watcher publishes an AttachedCollisionObject on the robot's
    /// attached_collision_objects_ros whenever an object is attached to (op ADD) or
    /// detached from (op REMOVE) a robot link. On attach the mirrored GameObject is
    /// reparented under the link Transform at the link-relative pose from the message, so it
    /// follows the gripper; on detach it is reparented back at its current world pose and the
    /// ADD that follows on collision_objects_ros sets the exact place pose.
    /// While attached the object is locked: nothing is sent for its id to the planning scene of
    /// the robot carrying it, physics cannot move it, and the user cannot grab it. There is one
    /// listener per robot: it hears that robot's attach notices and resolves link names on that
    /// robot only. The other robots' planning scenes keep being told where the object is.
    /// </summary>
    public class AttachedCollisionObjectListener : MonoBehaviour
    {
        [Header("ROS")]
        [Tooltip("Relative to the robot's namespace.")]
        public string topic = "attached_collision_objects_ros";

        [Header("References")]
        [SerializeField] private CollisionObjectsListenerSimple sceneListener;
        [SerializeField] private EnvironmentRegistry registry;
        [SerializeField] private GameObject worldOrigin; // parent restored on detach when the original is gone
        [Tooltip("Selection dropped when the selected object is attached. Found in the scene if empty.")]
        [SerializeField] private SelectionService selection;

        [Header("Timing")]
        [Tooltip("Seconds to wait after a detach for the re-ADD carrying the place pose before " +
                 "letting the object publish again anyway.")]
        [SerializeField] private float resumeTimeout = 2f;
        [Tooltip("Same wait after a detach applied from the executing solution's step feedback " +
                 "(see ApplyExpected): the watcher's own detach and re-ADD are still on their way " +
                 "and may lag the robot by several seconds when the planning scene is large.")]
        [SerializeField] private float expectedDetachResumeTimeout = 20f;

        [Header("Attach placement")]
        [Tooltip("An attach notice whose link-relative pose would move the object further than this (metres) " +
                 "does not match where the Unity hand is; the object stays put until the hand gets there.")]
        [SerializeField] private float maxAttachJump = 0.25f;
        [Tooltip("How long (seconds) an inconsistent attach notice is re-checked against the moving hand before it is given up.")]
        [SerializeField] private float pendingAttachSeconds = 3f;
        [SerializeField, Tooltip("Log attach notices that only restate an existing carry.")]
        private bool verboseAttachLog = false;

        /// <summary>
        /// Brings the mirrored robot up to date before an attach is placed. Set by whoever drives
        /// the robot from live joint states, which are otherwise applied once per frame.
        /// </summary>
        [System.NonSerialized] public System.Action syncRobot;

        /// <summary>
        /// The robot has let go of this object and its publisher is live again at the pose
        /// MoveIt reported (or Unity's own, after the timeout). Fired once per detach, after the
        /// place pose has been applied, so listeners may move the object from here on.
        /// </summary>
        public event System.Action<GameObject> Released;

        // The robot whose links objects are attached to; the MoveIt plugin hands it over.
        private RobotInstance robot;

        // Every enabled listener, so a robot's own can be found without a scene search.
        private static readonly List<AttachedCollisionObjectListener> listeners = new();

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => listeners.Clear();

        /// <summary>The robot whose attach notices this listener mirrors; null before it is given one.</summary>
        public RobotInstance Robot => robot;

        /// <summary>
        /// The attach mirror of a robot: the listener that was given that robot, else the one
        /// listener that was given none (a scene set up without robots). Never another robot's.
        /// </summary>
        public static AttachedCollisionObjectListener For(RobotInstance target)
        {
            AttachedCollisionObjectListener unassigned = null;
            int unassignedCount = 0;
            foreach (AttachedCollisionObjectListener listener in listeners)
            {
                if (listener == null) continue;
                if (!ReferenceEquals(target, null) && ReferenceEquals(listener.robot, target)) return listener;
                if (ReferenceEquals(listener.robot, null))
                {
                    unassigned = listener;
                    unassignedCount++;
                }
            }
            return unassignedCount == 1 ? unassigned : null;
        }

        void OnEnable()
        {
            if (!listeners.Contains(this)) listeners.Add(this);
        }

        void OnDisable() => listeners.Remove(this);

        // Namespace of the planning scene the carried objects are silent in; null without a
        // robot, and then they are silent everywhere.
        private string CarrierNamespace => robot != null ? robot.Namespace : null;

        /// <summary>
        /// True while this robot holds an object, and after it let one go until the pose it put
        /// it down at has arrived (that pose is expressed in the robot's planning frame).
        /// </summary>
        public bool IsCarrying => attached.Count > 0 || pendingResume.Count > 0;

        /// <summary>True between this robot's detach of an object and the planning-scene ADD that carries its place pose.</summary>
        public bool IsAwaitingPlacePose(string id) => pendingResume.ContainsKey(id);

        // Frame id of a notice that poses the object in the planning frame instead of the link's.
        private string PlanningFrameId => robot != null ? robot.PlanningFrameId : RobotProfile.DefaultPlanningFrame;

        // Everything changed on attach, so detach can put it back.
        private class AttachState
        {
            public GameObject go;
            public Transform link;          // link MoveIt has it attached to, once resolved
            public bool reparented;
            public bool placedFromMessage;  // the link-relative pose came from a trusted notice
            public Transform originalParent;
            public Vector3 originalLocalScale;
            public readonly List<(Rigidbody rb, bool wasKinematic)> bodies = new();
            public readonly List<(XRGrabInteractable grab, bool wasEnabled)> grabs = new();
            public readonly List<SelectableGrabController> grabControllers = new();
        }

        private readonly Dictionary<string, AttachState> attached = new();
        // Attach notices whose pose did not match where the object stands, re-tried each frame
        // until the hand reaches the object: id -> (notice, deadline)
        private readonly Dictionary<string, (AttachedCollisionObjectMsg msg, float deadline)> pendingAttach = new();
        // Detached ids whose publishers stay silent until the authoritative post-detach pose
        // arrives (so Unity's possibly-stale pose isn't pushed back into MoveIt): id -> deadline
        private readonly Dictionary<string, float> pendingResume = new();
        private readonly List<string> expired = new();
        // Ids released through ApplyExpected whose watcher notices have not arrived yet. The
        // watcher's attach for such an id describes a step the robot has already finished and
        // must not put the object back into the hand; its detach then only ends the wait.
        private readonly HashSet<string> expectedDetached = new();

        private IRosBus ros;
        private bool started;
        private bool connected;

        /// <summary>
        /// The robot attach notices refer to: link names are looked up on its model, through its
        /// name rule when the ROS robot names its links differently.
        /// </summary>
        public void SetRobot(RobotInstance target) => robot = target;

        /// <summary>
        /// Hands over the ROS transport (the robot's namespaced bus). The listener is idle until
        /// this is called (the MoveIt plugin does it at registration); it then subscribes in
        /// Start, or at once when Start has already run.
        /// </summary>
        public void Initialise(IRosBus bus)
        {
            if (bus == null) throw new System.ArgumentNullException(nameof(bus));
            if (ros == bus) return;
            if (ros != null) throw new System.InvalidOperationException("AttachedCollisionObjectListener already has a ROS bus.");
            ros = bus;
            if (started) Connect();
        }

        void Start()
        {
            started = true;
            if (sceneListener == null) sceneListener = FindFirstObjectByType<CollisionObjectsListenerSimple>();
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
            if (selection == null) selection = FindFirstObjectByType<SelectionService>();

            if (sceneListener != null)
            {
                sceneListener.OnObjectUpdated += OnObjectUpdated;
                sceneListener.OnRegistryCleared += OnRegistryCleared;
            }

            if (ros != null) Connect();
        }

        private void Connect()
        {
            ros.Subscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);
            connected = true;
        }

        /// <summary>
        /// Let go of the bus and the robot: every object still held goes back under its own
        /// parent with its publisher live, the subscription ends, and the listener is idle again,
        /// ready for another robot.
        /// </summary>
        public void Release()
        {
            ReleaseAll();
            if (connected) ros.Unsubscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);
            connected = false;
            ros = null;
            robot = null;
        }

        /// <summary>Use this mirror (the MoveIt plugin hands the scene's to the listeners it creates for further robots).</summary>
        public void SetSceneListener(CollisionObjectsListenerSimple mirror)
        {
            if (ReferenceEquals(sceneListener, mirror)) return;
            if (sceneListener != null && started)
            {
                sceneListener.OnObjectUpdated -= OnObjectUpdated;
                sceneListener.OnRegistryCleared -= OnRegistryCleared;
            }
            sceneListener = mirror;
            if (sceneListener != null && started)
            {
                sceneListener.OnObjectUpdated += OnObjectUpdated;
                sceneListener.OnRegistryCleared += OnRegistryCleared;
            }
        }

        void OnDestroy()
        {
            if (connected)
                ros.Unsubscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);

            if (sceneListener != null)
            {
                sceneListener.OnObjectUpdated -= OnObjectUpdated;
                sceneListener.OnRegistryCleared -= OnRegistryCleared;
            }
            OnRegistryCleared(tearingDown: true);
        }

        void Update()
        {
            if (pendingAttach.Count > 0)
            {
                foreach (var kv in new List<KeyValuePair<string, (AttachedCollisionObjectMsg msg, float deadline)>>(pendingAttach))
                {
                    if (Time.time > kv.Value.deadline)
                    {
                        pendingAttach.Remove(kv.Key);
                        Debug.LogWarning($"[ACO Listener] The hand never came to '{kv.Key}' within {pendingAttachSeconds:0.#} s of its attach notice; it stays where it is.");
                        continue;
                    }
                    Attach(kv.Value.msg, retrying: true);
                }
            }
            if (pendingResume.Count == 0) return;

            expired.Clear();
            foreach (var kv in pendingResume)
                if (Time.time >= kv.Value) expired.Add(kv.Key);

            foreach (string id in expired)
            {
                Debug.LogWarning($"[ACO Listener] No re-ADD for '{id}' in time after its detach; resuming its publisher.");
                Resume(id);
            }
        }

        void OnAttachedCollisionObject(AttachedCollisionObjectMsg msg)
        {
            string id = msg.@object?.id;
            if (string.IsNullOrEmpty(id)) return;

            if (msg.@object.operation == CollisionObjectMsg.REMOVE)
            {
                Detach(id, expected: false);
                return;
            }
            if (expectedDetached.Contains(id))
            {
                // The robot already put this down (ApplyExpected); the watcher is catching up.
                Debug.Log($"[ACO Listener] Ignoring the watcher's late attach of '{id}'; it was already released from the executing solution.");
                return;
            }
            Attach(msg);
        }

        /// <summary>
        /// Puts every object still held under a robot link back under its own parent, at once,
        /// with its publisher live. The caller clears MoveIt's side itself
        /// (<see cref="PlanningSceneEndpoint.DetachAllFromRobot"/>): no re-ADD pose is awaited,
        /// and the watcher's later detach notices find nothing to do. Returns how many were released.
        /// </summary>
        public int ReleaseAll()
        {
            int n = 0;
            foreach (var kv in attached)
            {
                if (sceneListener != null)
                {
                    sceneListener.attachedIds.Remove(kv.Key);
                    sceneListener.awaitingDetachPose.Remove(kv.Key);
                }
                if (kv.Value.go == null) continue;
                Restore(kv.Key, kv.Value, keepSilent: false);
                n++;
                Debug.Log($"[ACO Listener] Released '{kv.Key}'.");
            }
            attached.Clear();
            pendingAttach.Clear();
            pendingResume.Clear();
            expectedDetached.Clear();
            return n;
        }

        /// <summary>
        /// Applies an attach (operation ADD) or detach (REMOVE) that the executing solution says
        /// has just happened: the server reports each solution step done the moment MoveIt applies
        /// its scene diff, which reaches Unity well before the planning-scene watcher's notice on
        /// <see cref="topic"/> when the scene is large. The watcher's later messages for the same
        /// step are then no-ops; its post-detach re-ADD still sets the exact place pose.
        /// </summary>
        public void ApplyExpected(AttachedCollisionObjectMsg msg)
        {
            string id = msg?.@object?.id;
            if (string.IsNullOrEmpty(id)) return;

            if (msg.@object.operation == CollisionObjectMsg.REMOVE)
            {
                Detach(id, expected: true);
                return;
            }
            expectedDetached.Remove(id); // picked up again: the next watcher attach is genuine
            Attach(msg);
        }

        /// <summary>Forget earlier expected detaches, e.g. when a new execution starts.</summary>
        public void ForgetExpected() => expectedDetached.Clear();

        // Idempotent: a repeated attach (new link or relative pose) re-places the object and
        // keeps the state saved by the first one.
        void Attach(AttachedCollisionObjectMsg msg, bool retrying = false)
        {
            if (sceneListener == null) return;
            string id = msg.@object.id;
            if (!retrying) pendingAttach.Remove(id); // a fresh notice supersedes a pending one
            syncRobot?.Invoke();

            if (!sceneListener.TryGetObject(id, out var go))
            {
                // Unity joined mid-carry: build it from the geometry in the attach message.
                go = sceneListener.SpawnFromAttachedObject(msg.@object);
                if (go == null) return;
                Debug.Log($"[ACO Listener] Built '{id}' from its attach message (not seen on the scene topic yet).");
            }

            pendingResume.Remove(id);
            sceneListener.awaitingDetachPose.Remove(id);
            sceneListener.attachedIds.Add(id);

            if (!attached.TryGetValue(id, out var state) || state.go != go)
            {
                state = new AttachState
                {
                    go = go,
                    originalParent = go.transform.parent,
                    originalLocalScale = go.transform.localScale,
                };
                attached[id] = state;
                Lock(state);
            }

            Transform link = ResolveLink(msg.link_name);
            if (link == null)
            {
                // Still locked and silent on /collision_object (MoveIt has it attached either
                // way), but left visible where it is.
                Debug.LogWarning($"[ACO Listener] No link transform for '{msg.link_name}'; '{id}' stays where it is while attached.");
                return;
            }

            if (state.link != link)
            {
                state.link = link;
                if (registry != null) registry.Attach(id, link);
            }

            // A carry does not change the grasp: once the object rides a link, later attach notices
            // for the same link (every executed step's scene diff, the watcher's own echoes) only
            // restate it, with link-relative poses that differ by a few centimetres between sources.
            // Keep the first placement so the object does not jitter in the hand; re-place only
            // when it moves to another link.
            if (state.reparented && go.transform.parent == link && state.placedFromMessage)
            {
                if (verboseAttachLog) Debug.Log($"[ACO Listener] '{id}' already rides '{link.name}'; attach notice restates it.");
                return;
            }

            // The hand closed on the object where it is, so the message's link-relative pose must
            // land it close by. A notice whose pose would fling it far from where it stands is
            // early (the Unity hand is not there yet) or expressed in some other frame.
            Vector3 worldPos = go.transform.position;
            Quaternion worldRot = go.transform.rotation;
            Transform parentBefore = go.transform.parent;
            string frame = msg.@object.header?.frame_id ?? "";
            Transform origin = PlanningFrame;
            if (frame == PlanningFrameId && origin != null && msg.@object.pose != null)
            {
                // A notice may pose a re-attached object in the planning frame rather than in the
                // link frame: place it in the world, then let it ride.
                Vector3 p = origin.TransformPoint(RosUnityConversion.RosToUnityPosition(msg.@object.pose.position));
                Quaternion r = origin.rotation * RosUnityConversion.RosToUnityQuaternion(msg.@object.pose.orientation);
                go.transform.SetPositionAndRotation(p, r);
                go.transform.SetParent(link, worldPositionStays: true);
            }
            else
            {
                AttachedObjectPlacement.Place(go.transform, link, msg.@object.pose);
            }
            float jump = Vector3.Distance(worldPos, go.transform.position);
            var pm = msg.@object.pose?.position;
            if (jump > maxAttachJump)
            {
                // The object stays put, where the user sees it, until a notice consistent with its
                // position arrives. It is still locked and silent, as MoveIt has it attached.
                go.transform.SetParent(parentBefore, worldPositionStays: true);
                go.transform.SetPositionAndRotation(worldPos, worldRot);
                go.transform.localScale = state.originalLocalScale;
                if (!retrying)
                {
                    Debug.LogWarning($"[ACO Listener] Attach notice for '{id}' on '{link.name}' (frame '{frame}', pose ({pm?.x:F3},{pm?.y:F3},{pm?.z:F3})) " +
                                     $"would move it {jump * 1000f:F0} mm; it stays where it is until the hand gets there.");
                    pendingAttach[id] = (msg, Time.time + pendingAttachSeconds);
                }
                state.reparented = false;
                state.placedFromMessage = false;
                return;
            }
            if (retrying)
            {
                pendingAttach.Remove(id);
                Debug.Log($"[ACO Listener] The hand reached '{id}' {jump * 1000f:F0} mm from its notice's pose; riding now.");
            }
            Debug.Log($"[ACO Listener] Attached '{id}' to link '{link.name}' (frame '{frame}', pose ({pm?.x:F3},{pm?.y:F3},{pm?.z:F3}), moved {jump * 1000f:F1} mm onto the notice's pose).");
            state.reparented = true;
            state.placedFromMessage = true;
        }

        // expected: the detach comes from the executing solution (ApplyExpected), not the watcher.
        void Detach(string id, bool expected)
        {
            if (sceneListener == null) return;
            pendingAttach.Remove(id);
            bool wasAttached = sceneListener.attachedIds.Remove(id);

            if (!attached.TryGetValue(id, out var state))
            {
                if (!expected && expectedDetached.Remove(id))
                {
                    // The watcher's notice of a detach already applied from the solution: only
                    // its re-ADD with the place pose is still outstanding, on the usual timeout.
                    if (pendingResume.ContainsKey(id)) pendingResume[id] = Time.time + resumeTimeout;
                    return;
                }
                if (wasAttached) Debug.LogWarning($"[ACO Listener] Detach for '{id}' with no saved attach state.");
                return;
            }
            attached.Remove(id);
            if (state.go == null) return;

            Restore(id, state, keepSilent: true);

            // The re-ADD that follows on the scene topic carries the exact place pose; the
            // publishers stay silent until it lands. Resumed in OnObjectUpdated.
            sceneListener.awaitingDetachPose.Add(id);
            pendingResume[id] = Time.time + (expected ? expectedDetachResumeTimeout : resumeTimeout);
            if (expected) expectedDetached.Add(id);
            Debug.Log($"[ACO Listener] Detached '{id}'{(expected ? " (from the executing solution)" : "")}.");
        }

        void OnObjectUpdated(string id)
        {
            if (pendingResume.ContainsKey(id)) Resume(id);
        }

        void Resume(string id)
        {
            pendingResume.Remove(id);
            if (sceneListener == null) return;
            sceneListener.awaitingDetachPose.Remove(id);
            if (sceneListener.attachedIds.Contains(id)) return; // picked up again meanwhile
            if (sceneListener.TryGetObject(id, out var go))
            {
                SetAttachedToRobot(go, false);
                Released?.Invoke(go);
            }
        }

        // The mirrored objects are being forgotten: leave nothing parented under a robot link.
        // On teardown the original parent may itself be mid-destroy (Unity refuses to reparent
        // under it), and nothing is left to track the object, so it is removed from the link
        // by destroying it; it is rebuilt from the next ADD if a listener comes back.
        void OnRegistryCleared(bool tearingDown)
        {
            // When the whole Unity scene is unloading the objects die with it; reparenting
            // them mid-teardown only raises errors.
            if (gameObject.scene.isLoaded)
                foreach (var kv in attached)
                {
                    AttachState state = kv.Value;
                    if (state.go == null) continue;
                    if (!tearingDown)
                    {
                        Restore(kv.Key, state, keepSilent: false);
                        continue;
                    }
                    // attachedToRobot keeps its REMOVE off /collision_object.
                    if (registry != null) registry.Unregister(kv.Key, RemovalOrigin.Remote);
                    Destroy(state.go);
                }

            attached.Clear();
            pendingResume.Clear();
            pendingAttach.Clear();
            expectedDetached.Clear();
            // Links resolved for attaches may be gone with the objects that hung under them.
            if (robot != null && robot.Model is DirectArticulationIKController controller && controller != null)
                controller.ClearLinkCache();
        }

        private Transform WorldOrigin =>
            worldOrigin != null ? worldOrigin.transform : (registry != null ? registry.WorldOrigin : null);

        // The Unity pose of the frame this robot's planning scene poses objects in.
        private Transform PlanningFrame =>
            robot != null && robot.PlanningFrame != null ? robot.PlanningFrame : WorldOrigin;

        // The link as the Unity robot names it, then through the robot's ROS name rule.
        Transform ResolveLink(string linkName)
        {
            IRobotModel model = robot != null ? robot.Model : null;
            if (model == null || string.IsNullOrEmpty(linkName)) return null;
            if (model.TryFindLinkTransform(linkName, out var link)) return link;

            string localName = robot.Names.ToLocal(linkName);
            if (localName != linkName && model.TryFindLinkTransform(localName, out link))
                return link;

            return null;
        }

        void Lock(AttachState state)
        {
            var go = state.go;
            SetAttachedToRobot(go, true);

            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                state.bodies.Add((rb, rb.isKinematic));
                rb.isKinematic = true;
            }

            // SelectableGrabController re-enables its interactable on selection, so it needs its
            // own lock; interactables without one are simply disabled.
            foreach (var grab in go.GetComponentsInChildren<XRGrabInteractable>(true))
            {
                if (grab.TryGetComponent<SelectableGrabController>(out var controller))
                {
                    state.grabControllers.Add(controller);
                    controller.SetLocked(true);
                }
                else
                {
                    state.grabs.Add((grab, grab.enabled));
                    grab.enabled = false;
                }
            }

            // Drop the selection too, so its verbs cannot edit or delete the object mid-carry.
            GameObject selected = selection != null ? selection.Current?.GameObject : null;
            if (selected != null && selected.transform.IsChildOf(go.transform))
                selection.ClearSelection();
        }

        void Restore(string id, AttachState state, bool keepSilent)
        {
            var go = state.go;
            if (state.reparented)
            {
                Transform parent = state.originalParent ? state.originalParent : WorldOrigin;
                go.transform.SetParent(parent, worldPositionStays: true);
                go.transform.localScale = state.originalLocalScale;
            }
            if (state.link != null && registry != null) registry.Detach(id);

            foreach (var (rb, wasKinematic) in state.bodies)
                if (rb != null) rb.isKinematic = wasKinematic;
            foreach (var (grab, wasEnabled) in state.grabs)
                if (grab != null) grab.enabled = wasEnabled;
            foreach (var controller in state.grabControllers)
                if (controller != null) controller.SetLocked(false);

            if (!keepSilent) SetAttachedToRobot(go, false);
        }

        void SetAttachedToRobot(GameObject go, bool value)
        {
            string carrier = CarrierNamespace;
            foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
            {
                pub.attachedToRobot = value;
                pub.attachedNamespace = value ? carrier : null;
                if (value) continue;
                // Coming back: the pose this robot's planning scene just set is already known
                // to it, so no MOVE goes back there; the other planning scenes get the last stretch.
                if (sceneListener != null && sceneListener.Fanout != null) sceneListener.Fanout.SyncAfterCarry(pub, carrier);
                pub.MarkTransformAsPublished();
            }
        }
    }
}
