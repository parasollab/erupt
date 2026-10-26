using System.Collections.Generic;
using UnityEngine;

namespace Factory
{
    /// <summary>
    /// Is the robot, as it stands right now, clear of the cell? While the user drags the base
    /// around, the arm keeps its resting pose, and a spot where that pose intersects the machine
    /// body or the stock stand plans nothing: MTC's first motion ("open hand") starts from a state
    /// in collision and fails instantly, although every reach marker is green. Each check boxes
    /// every robot collider (the links are convex meshes and boxes) and overlaps the box with the
    /// colliders under the obstacle roots; a hit names the link and the obstacle.
    /// </summary>
    public class RobotClearanceCheck : MonoBehaviour
    {
        [SerializeField, Tooltip("Robot root (fr3); its non-trigger colliders are the links.")] private GameObject robotRoot;
        [SerializeField, Tooltip("Everything the robot must stay clear of (the station, the machines).")] private Transform[] obstacleRoots = new Transform[0];
        [SerializeField, Tooltip("Colliders under these roots never count (the pedestal the robot stands on).")] private Transform[] excludeRoots = new Transform[0];
        [SerializeField, Tooltip("Each link's query box is shrunk by this much (metres) so a grazing contact is not a hit.")] private float margin = 0.01f;
        [SerializeField, Tooltip("Seconds between checks while enabled.")] private float checkInterval = 0.1f;

        public bool Clear { get; private set; } = true;
        public string BlockingLink { get; private set; }
        public string BlockingObstacle { get; private set; }
        public event System.Action<bool> Changed;

        private readonly List<Collider> _robot = new List<Collider>();
        private readonly HashSet<Collider> _obstacles = new HashSet<Collider>();
        private readonly Collider[] _hits = new Collider[32];
        private float _next;
        private bool _collected;

        /// <summary>(Re)collect the robot and obstacle colliders (after the scene is built or changed).</summary>
        public void Collect()
        {
            _robot.Clear();
            _obstacles.Clear();
            if (robotRoot != null)
                foreach (Collider c in robotRoot.GetComponentsInChildren<Collider>(true))
                    if (c != null && !c.isTrigger) _robot.Add(c);
            foreach (Transform root in obstacleRoots)
            {
                if (root == null) continue;
                foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || c.transform.IsChildOf(robotRoot != null ? robotRoot.transform : null)) continue;
                    bool excluded = false;
                    foreach (Transform ex in excludeRoots) if (ex != null && c.transform.IsChildOf(ex)) { excluded = true; break; }
                    if (!excluded) _obstacles.Add(c);
                }
            }
            _collected = true;
        }

        private void OnEnable()
        {
            if (!_collected) Collect();
            _next = 0f;
        }

        private void LateUpdate()
        {
            if (Time.time < _next) return;
            _next = Time.time + Mathf.Max(0.02f, checkInterval);
            Evaluate();
        }

        /// <summary>Runs the overlap test now and returns whether the robot is clear.</summary>
        public bool Evaluate()
        {
            if (!_collected) Collect();
            Physics.SyncTransforms();
            bool clear = true;
            string link = null, obstacle = null;
            foreach (Collider c in _robot)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (!QueryBox(c, out Vector3 centre, out Vector3 half, out Quaternion rot)) continue;
                int n = Physics.OverlapBoxNonAlloc(centre, half, _hits, rot, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    if (!_obstacles.Contains(_hits[i])) continue;
                    clear = false; link = c.name; obstacle = ObstacleName(_hits[i]);
                    break;
                }
                if (!clear) break;
            }
            bool changed = clear != Clear;
            Clear = clear;
            BlockingLink = link;
            BlockingObstacle = obstacle;
            if (changed) Changed?.Invoke(clear);
            return clear;
        }

        // The planning-scene id when the collider belongs to a published object ("vf2_body"), else the object's name.
        private static string ObstacleName(Collider c)
        {
            var pub = c.GetComponentInParent<CollisionObjectPublisher>();
            if (pub != null && !string.IsNullOrEmpty(pub.objectId)) return pub.objectId;
            return c.name == "Collision" && c.transform.parent != null ? c.transform.parent.name : c.name;
        }

        // Oriented box of a collider: a BoxCollider's own box, a mesh collider's local mesh bounds, else the world AABB.
        private bool QueryBox(Collider c, out Vector3 centre, out Vector3 half, out Quaternion rot)
        {
            Transform t = c.transform;
            Vector3 scale = t.lossyScale;
            if (c is BoxCollider b)
            {
                centre = t.TransformPoint(b.center);
                half = Vector3.Scale(b.size * 0.5f, Abs(scale));
                rot = t.rotation;
            }
            else if (c is MeshCollider m && m.sharedMesh != null)
            {
                Bounds lb = m.sharedMesh.bounds;
                centre = t.TransformPoint(lb.center);
                half = Vector3.Scale(lb.extents, Abs(scale));
                rot = t.rotation;
            }
            else
            {
                Bounds wb = c.bounds;
                centre = wb.center; half = wb.extents; rot = Quaternion.identity;
            }
            half = new Vector3(Mathf.Max(0.001f, half.x - margin), Mathf.Max(0.001f, half.y - margin), Mathf.Max(0.001f, half.z - margin));
            return true;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>
        /// Asks move_group whether its current robot state is collision-free in its planning scene
        /// (/check_state_validity) and reports the contact pairs. Ground truth for probes and logs;
        /// only meaningful once the scene has been (re)sent, i.e. after Confirm.
        /// </summary>
        public static void QueryRosStateValidity(string groupName, System.Action<bool, string> done)
        {
            Erupt.Ros.IRosBus bus = Erupt.Ros.RosBus.Instance;
            if (bus == null || bus.HasConnectionError) { done?.Invoke(false, "ROS not connected"); return; }
            try
            {
                const string service = "/check_state_validity";
                bus.RegisterRosService<RosMessageTypes.Moveit.GetStateValidityRequest, RosMessageTypes.Moveit.GetStateValidityResponse>(service);
                var request = new RosMessageTypes.Moveit.GetStateValidityRequest { group_name = groupName ?? "" };
                request.robot_state.is_diff = true; // the current state
                bus.SendServiceMessage<RosMessageTypes.Moveit.GetStateValidityResponse>(service, request, response =>
                {
                    if (response == null) { done?.Invoke(false, "no response"); return; }
                    var contacts = new List<string>();
                    if (response.contacts != null)
                        foreach (var c in response.contacts) contacts.Add($"{c.contact_body_1} <-> {c.contact_body_2} ({c.depth * 1000f:F0} mm)");
                    done?.Invoke(response.valid, response.valid ? "valid" : string.Join(", ", contacts));
                });
            }
            catch (System.Exception e) { done?.Invoke(false, "query failed: " + e.Message); }
        }
    }
}
