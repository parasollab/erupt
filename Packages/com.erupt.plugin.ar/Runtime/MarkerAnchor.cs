using UnityEngine;
using Erupt.Robot;

namespace Erupt.Plugins.Ar
{
    /// <summary>
    /// Binds a robot to a printed AprilTag: which tag, how large it is, and where the robot's
    /// base stands relative to it. Put it on the robot or on an object under it. While the tag
    /// is in view the AR plugin shows where the robot would go, and the user confirms with
    /// <c>Place here</c> beside the proposed base.
    /// </summary>
    /// <remarks>
    /// Two layouts use the same component. One tag per robot: each robot's anchor names its
    /// own tag id. One tag for the whole work cell: every robot's anchor names the same id
    /// (and the same size) with its own offset.
    ///
    /// The offset is the pose of the robot's base (the robot's root object) in the tag's
    /// marker frame: the origin at the tag's centre, x towards the tag's right edge, y out of
    /// the tag (up, for a tag lying on a table) and z towards the tag's top edge, as printed.
    /// A robot with no offset stands on the tag's centre and faces the tag's top edge (an
    /// imported robot looks along its z axis, which is ROS x).
    ///
    /// The anchor binds to the robot above it. An anchor with no robot above it binds to the
    /// only robot below it, so a scene can hold the anchor on an empty object and have a robot
    /// prefab dropped onto that object (the AR sample scene does).
    /// </remarks>
    [DisallowMultipleComponent]
    public class MarkerAnchor : MonoBehaviour
    {
        [Tooltip("tagStandard41h12 id (0-2114) of the printed tag this robot is placed from. Several robots may name the same tag.")]
        [SerializeField, Min(0)] private int tagId;

        [Tooltip(AprilTagTracker.SizeRule)]
        [SerializeField, Min(0.001f)] private float tagSize = 0.10f;

        [Header("Robot base relative to the tag")]
        [Tooltip("Position of the robot's base in metres from the tag's centre: x towards the tag's right edge, y out of the tag " +
                 "(up, for a tag on a table), z towards the tag's top edge as printed.")]
        [SerializeField] private Vector3 basePosition;

        [Tooltip("Rotation of the robot's base in degrees. With (0, 0, 0) the robot faces the tag's top edge; y turns it clockwise seen from above.")]
        [SerializeField] private Vector3 baseEuler;

        [Tooltip("Treat the tag as lying flat: only its position and heading are used, and the robot stands upright. The tilt a " +
                 "detection reports for a flat tag is mostly noise, and it would lean the robot and swing a long offset. " +
                 "Switch off for a tag on a wall or a slope.")]
        [SerializeField] private bool tagLiesFlat = true;

        public int TagId => tagId;
        public float TagSize => tagSize;
        public bool TagLiesFlat => tagLiesFlat;

        /// <summary>The pose of the robot's base in the tag's marker frame (see the class remarks).</summary>
        public Pose BaseInMarker => new Pose(basePosition, Quaternion.Euler(baseEuler));

        /// <summary>Set the anchor up from code.</summary>
        public void Configure(int id, float size, Vector3 position, Vector3 euler, bool flat = true)
        {
            tagId = id;
            tagSize = size;
            basePosition = position;
            baseEuler = euler;
            tagLiesFlat = flat;
        }

        /// <summary>
        /// The robot this anchor places: the <see cref="RobotInstance"/> above it, else the only
        /// one below it. Null when there is none, or several below and none above.
        /// </summary>
        public RobotInstance Robot
        {
            get
            {
                RobotInstance above = GetComponentInParent<RobotInstance>(true);
                if (above != null) return above;
                RobotInstance[] below = GetComponentsInChildren<RobotInstance>(true);
                return below.Length == 1 ? below[0] : null;
            }
        }

        /// <summary>Where this anchor puts the robot's base for a tag seen at <paramref name="tagPose"/> (world, tag frame).</summary>
        public Pose ProposedBase(Pose tagPose) => MarkerMath.ProposeBase(tagPose, BaseInMarker, tagLiesFlat);

        /// <summary>The anchor of a robot: the first one on it or below it, else the first above it whose robot it is. Null without one.</summary>
        public static MarkerAnchor Of(RobotInstance robot)
        {
            if (robot == null) return null;
            MarkerAnchor below = robot.GetComponentInChildren<MarkerAnchor>(true);
            if (below != null) return below;
            foreach (MarkerAnchor above in robot.GetComponentsInParent<MarkerAnchor>(true))
                if (ReferenceEquals(above.Robot, robot)) return above;
            return null;
        }

        // Where the printed tag lies for the robot as it stands in the scene: the square the
        // detector measures, an arrow to its top edge, and a line to the robot's base.
        private void OnDrawGizmosSelected()
        {
            RobotInstance robot = Robot;
            Transform at = robot != null ? robot.transform : transform;
            Pose inMarker = BaseInMarker;
            Quaternion markerRotation = at.rotation * Quaternion.Inverse(inMarker.rotation);
            Vector3 markerPosition = at.position - markerRotation * inMarker.position;

            Gizmos.color = new Color(0f, 0.75f, 1f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(markerPosition, markerRotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(tagSize, 0f, tagSize));
            // The whole printed pattern is 9 cells for a detection square of 5.
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(tagSize * 9f / 5f, 0f, tagSize * 9f / 5f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * tagSize);
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawLine(markerPosition, at.position);
        }
    }
}
