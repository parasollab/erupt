using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Ros;

namespace Erupt.Robot
{
    /// <summary>
    /// One robot in the scene, on the robot's root object: which model it is
    /// (<see cref="Profile"/>), which ROS namespace it talks to, and the components that drive
    /// it. It joins the <see cref="RobotRegistry"/> while enabled, which is where plugins find it.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class RobotInstance : MonoBehaviour
    {
        [Tooltip("Unique among the robots in the scene. Empty = this object's name.")]
        [SerializeField] private string id = "";
        [Tooltip("ROS namespace of this robot's topics, services and actions. Empty = the root namespace.")]
        [SerializeField] private string rosNamespace = "";
        [SerializeField] private RobotProfile profile;

        [Header("Components (found under this object if empty)")]
        [SerializeField] private DirectArticulationIKController controller;
        [SerializeField] private RobotInteractionController interaction;
        [SerializeField] private JointTrajectoryPlayer trajectoryPlayer;
        [SerializeField] private RobotGhosts ghosts;

        [Header("Frames")]
        [Tooltip("The Unity transform that stands for this robot's ROS planning frame (\"world\" on its ROS side), with ERUPT's axes: " +
                 "ROS x, y, z = this transform's x, z, y. A robot prefab points it at its own PlanningFrame child, so the robot's ROS " +
                 "stack believes it stands at the origin of its own world wherever the robot is in the scene, and nothing has to be " +
                 "configured on the ROS side. Assign one shared transform to several robots when their ROS stacks share one world " +
                 "(each stack then needs its robot's pose in that world). Empty = the scene's world origin.")]
        [SerializeField] private Transform planningFrame;

        private IRobotModel modelOverride;
        private RobotRegistry assignedRegistry;
        private RobotRegistry registry;
        private string registeredId;

        // The namespaced bus is built once per (registry bus, namespace) so that components
        // handed this robot's bus twice see the same object.
        private IRosBus scopedBus;
        private IRosBus scopedFrom;
        private string scopedNamespace;

        /// <summary>Unique among the registered robots; the object's name unless one was set.</summary>
        public string Id => registeredId ?? (string.IsNullOrEmpty(id) ? name : id);

        /// <summary>ROS namespace of everything this robot publishes, subscribes or calls; empty for the root namespace.</summary>
        public string Namespace => rosNamespace ?? "";

        /// <summary>The model description; null for a robot that has none (joint roles are then guessed).</summary>
        public RobotProfile Profile => profile;

        /// <summary>Joints, IK and link lookup.</summary>
        public IRobotModel Model => modelOverride ?? (Controller != null ? controller : null);

        /// <summary>The robot's gripper; null for a robot that has none.</summary>
        public IRobotGripper Gripper => Model is IRobotGripper gripper && gripper.HasGripper ? gripper : null;

        /// <summary>The articulation controller behind <see cref="Model"/>; null when the model was supplied by code.</summary>
        public DirectArticulationIKController Controller
        {
            get
            {
                if (controller == null) controller = GetComponentInChildren<DirectArticulationIKController>(true);
                return controller;
            }
        }

        public RobotInteractionController Interaction
        {
            get
            {
                if (interaction == null) interaction = GetComponentInChildren<RobotInteractionController>(true);
                return interaction;
            }
        }

        /// <summary>Plays joint trajectories on this robot (plan previews).</summary>
        public JointTrajectoryPlayer TrajectoryPlayer
        {
            get
            {
                if (trajectoryPlayer == null) trajectoryPlayer = GetComponentInChildren<JointTrajectoryPlayer>(true);
                return trajectoryPlayer;
            }
        }

        /// <summary>Spawns this robot's translucent start and goal copies.</summary>
        public RobotGhosts Ghosts
        {
            get
            {
                if (ghosts == null) ghosts = GetComponentInChildren<RobotGhosts>(true);
                return ghosts;
            }
        }

        /// <summary>The registry this robot is in; null while it is disabled.</summary>
        public IRobotRegistry Registry => registry;

        /// <summary>
        /// The ROS bus seen from this robot's namespace: relative names (<c>joint_states</c>)
        /// resolve to <c>/&lt;namespace&gt;/joint_states</c>, absolute names pass through. Null
        /// until the robot is registered and its registry has been given the bus.
        /// </summary>
        public IRosBus Ros
        {
            get
            {
                IRosBus bus = registry?.Bus;
                if (bus == null) return null;
                if (scopedBus == null || !ReferenceEquals(scopedFrom, bus) || scopedNamespace != Namespace)
                {
                    scopedFrom = bus;
                    scopedNamespace = Namespace;
                    scopedBus = bus.Scoped(scopedNamespace);
                }
                return scopedBus;
            }
        }

        /// <summary>How this robot's joint and link names translate to the ROS side; identity without a profile.</summary>
        public RobotNameMap Names => profile != null ? profile.Names : RobotNameMap.Identity;

        /// <summary>
        /// The Unity transform that stands for this robot's ROS planning frame: by default the
        /// robot prefab's own <c>PlanningFrame</c> child (the origin of the world this robot's
        /// ROS stack plans in, which moves with the robot), a frame shared with other robots
        /// when the scene assigns one, and the scene's world origin for a robot that names none.
        /// </summary>
        public Transform PlanningFrame =>
            planningFrame != null ? planningFrame : registry?.DefaultPlanningFrame;

        /// <summary>The ROS frame id this robot plans in.</summary>
        public string PlanningFrameId =>
            profile != null && !string.IsNullOrEmpty(profile.planningFrame) ? profile.planningFrame : RobotProfile.DefaultPlanningFrame;

        /// <summary>Relative name of this robot's joint state topic.</summary>
        public string JointStatesTopic =>
            profile != null && !string.IsNullOrEmpty(profile.jointStatesTopic) ? profile.jointStatesTopic : RobotProfile.DefaultJointStatesTopic;

        /// <summary>Relative name of the topic planned trajectories are published on for execution; empty when the profile names none.</summary>
        public string TrajectoryCommandTopic => profile != null ? profile.trajectoryCommandTopic ?? "" : "";

        /// <summary>
        /// Set this robot up from code. Call it before the object is enabled (or while it is
        /// disabled): the id and namespace are fixed once the robot is registered.
        /// </summary>
        public void Configure(string robotId, string rosNamespace, RobotProfile robotProfile, IRobotModel model = null)
        {
            if (registry != null)
                throw new InvalidOperationException($"Robot '{Id}' is registered; configure it before enabling it.");
            id = robotId ?? "";
            this.rosNamespace = rosNamespace ?? "";
            profile = robotProfile;
            modelOverride = model;
        }

        /// <summary>Join this registry instead of <see cref="RobotRegistry.Default"/>. Call it before the object is enabled.</summary>
        public void UseRegistry(RobotRegistry target)
        {
            if (registry != null)
                throw new InvalidOperationException($"Robot '{Id}' is registered; choose its registry before enabling it.");
            assignedRegistry = target;
        }

        /// <summary>Use this transform as the robot's planning frame; null = the scene's world origin.</summary>
        public void SetPlanningFrame(Transform frame) => planningFrame = frame;

        // ---- the base ---------------------------------------------------------------------

        // Whatever needs the base to stay where it is, asked in order; see AddBaseGuard.
        private readonly List<Func<string>> baseGuards = new();
        private bool warnedSharedFrame;

        /// <summary>Where the robot's base is: the world pose of this object.</summary>
        public Pose BasePose => new Pose(transform.position, transform.rotation);

        /// <summary>
        /// The robot's base was put somewhere else with <see cref="PlaceBase(Pose)"/>; the second
        /// argument is the base pose it had before. The robot's own planning frame moves with
        /// it, so every pose that was expressed in that frame for the robot's ROS side (the
        /// objects in its planning scene, a goal, a plan) is stale: whoever sent one sends it
        /// again or drops it.
        /// </summary>
        public event Action<RobotInstance, Pose> BaseMoved;

        /// <summary>
        /// Register something that needs the base to stay put for a while (an executing
        /// trajectory, a carried object). <paramref name="whyNot"/> returns null while the base
        /// may move, and otherwise the reason, as a sentence for the user.
        /// </summary>
        public void AddBaseGuard(Func<string> whyNot)
        {
            if (whyNot != null && !baseGuards.Contains(whyNot)) baseGuards.Add(whyNot);
        }

        public void RemoveBaseGuard(Func<string> whyNot) => baseGuards.Remove(whyNot);

        /// <summary>Whether the base may be moved now; <paramref name="reason"/> says why not.</summary>
        public bool CanPlaceBase(out string reason)
        {
            foreach (Func<string> guard in baseGuards)
            {
                reason = guard();
                if (!string.IsNullOrEmpty(reason)) return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Put the robot's base at a world pose. The joints keep their positions. Refused
        /// (false, nothing moves) while something needs the base where it is; on success
        /// <see cref="BaseMoved"/> is raised.
        /// </summary>
        public bool PlaceBase(Pose pose, out string reason)
        {
            if (!CanPlaceBase(out reason)) return false;
            Pose before = BasePose;
            ArticulationRootMover.MoveRoot(gameObject, pose.position, pose.rotation.normalized);
            // A planning frame that does not move with the robot is a frame the robot's ROS
            // side was started with a pose in; that pose is wrong from here on.
            Transform frame = PlanningFrame;
            if (frame != null && !frame.IsChildOf(transform) && !warnedSharedFrame)
            {
                warnedSharedFrame = true;
                Debug.LogWarning($"[RobotInstance] The base of robot '{Id}' was moved, but its planning frame '{frame.name}' is not part of the robot " +
                                 "and stayed where it was: the robot's ROS side still has the robot at its old pose in that frame. Use the robot's " +
                                 "own PlanningFrame child, or restart the ROS side with the new pose.", this);
            }
            BaseMoved?.Invoke(this, before);
            return true;
        }

        /// <summary>As <see cref="PlaceBase(Pose, out string)"/>, for a caller that does not show the reason.</summary>
        public bool PlaceBase(Pose pose) => PlaceBase(pose, out _);

        /// <summary>
        /// Problems with this robot: the profile's own (<see cref="RobotProfile.Validate"/>) and
        /// joints or links the profile names that are missing under this object.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (profile == null)
            {
                problems.Add("No robot profile is assigned.");
                return problems;
            }

            problems.AddRange(profile.Validate());

            var linksByName = new Dictionary<string, Transform>();
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (!linksByName.ContainsKey(t.name)) linksByName[t.name] = t;

            foreach (JointSpec joint in profile.joints ?? Array.Empty<JointSpec>())
            {
                if (joint == null || string.IsNullOrEmpty(joint.childLink)) continue;
                if (!linksByName.TryGetValue(joint.childLink, out Transform link))
                    problems.Add($"Joint '{joint.name}': no link named '{joint.childLink}' under '{name}'.");
                else if (link.GetComponent<ArticulationBody>() == null)
                    problems.Add($"Joint '{joint.name}': link '{joint.childLink}' has no ArticulationBody.");
            }

            if (!string.IsNullOrEmpty(profile.baseLink) && !linksByName.ContainsKey(profile.baseLink))
                problems.Add($"No base link named '{profile.baseLink}' under '{name}'.");
            if (!string.IsNullOrEmpty(profile.endEffectorLink) && !linksByName.ContainsKey(profile.endEffectorLink))
                problems.Add($"No end-effector link named '{profile.endEffectorLink}' under '{name}'.");

            return problems;
        }

        private void OnEnable()
        {
            RobotRegistry target = assignedRegistry ?? RobotRegistry.Default;
            // The id is fixed while registered, so a later rename cannot make the registry's lookup miss.
            registeredId = string.IsNullOrEmpty(id) ? name : id;
            // The registry is known before it announces the robot, so whoever hears
            // IRobotRegistry.Added (a plugin creating the robot's session) already finds Ros.
            registry = target;
            try
            {
                target.Register(this);
            }
            catch (InvalidOperationException e)
            {
                registry = null;
                registeredId = null;
                Debug.LogError($"[RobotInstance] {e.Message}", this);
            }
        }

        private void OnDisable()
        {
            if (registry == null) return;
            registry.Unregister(this);
            registry = null;
            registeredId = null;
        }
    }
}
