using System;
using Erupt.Robot;
using UnityEngine;

namespace Erupt.Editor
{
    /// <summary>
    /// The ROS names of a robot, in the <c>.ros.json</c> format the ERUPT ROS workspace exports
    /// (<c>Tools/robot-descriptions</c>). Keys this class does not list are ignored.
    /// </summary>
    [Serializable]
    public class RosNamesFile
    {
        [Serializable]
        public class GripperController
        {
            public string name = "";
            public string type = "";
            public string[] joints = Array.Empty<string>();
        }

        public string robot = "";
        public string planning_frame = "";
        public string arm_group = "";
        public string[] arm_joints = Array.Empty<string>();
        public string joint_states_topic = "";
        public string trajectory_command_topic = "";
        public string base_link = "";
        public GripperController gripper_controller = new GripperController();
        public string gripper_group = "";
        public string end_effector_link = "";
        public string initial_pose = "";
        public string gripper_open_pose = "";
        public string gripper_closed_pose = "";

        /// <exception cref="FormatException">The text is not a JSON object.</exception>
        public static RosNamesFile Parse(string json)
        {
            try
            {
                var names = JsonUtility.FromJson<RosNamesFile>(json);
                if (names == null) throw new FormatException("The ROS-names file is empty.");
                names.arm_joints ??= Array.Empty<string>();
                names.gripper_controller ??= new GripperController();
                names.gripper_controller.joints ??= Array.Empty<string>();
                return names;
            }
            catch (ArgumentException e)
            {
                throw new FormatException("The ROS-names file is not valid JSON: " + e.Message, e);
            }
        }
    }

    /// <summary>
    /// The input of Robot Setup. The three files say most of it; every other field is a value
    /// typed in by the user, and a typed value wins over what the files say. An empty field
    /// means "take it from the files, or work it out".
    /// </summary>
    [Serializable]
    public class RobotSetupSettings
    {
        [Tooltip("The robot's URDF. Meshes it names as package://<package>/... are looked for in a folder <package> next to it.")]
        public string urdfPath = "";
        [Tooltip("Optional. The MoveIt SRDF: planning groups, named poses, end effector, passive joints.")]
        public string srdfPath = "";
        [Tooltip("Optional. A .ros.json file with the robot's ROS names.")]
        public string rosNamesPath = "";

        [Tooltip("The robot's name as people read it, e.g. \"Franka Research 3\".")]
        public string displayName = "";
        [Tooltip("File name of the prefab and the profile, e.g. \"FR3\". Empty = made from the display name.")]
        public string assetName = "";
        [Tooltip("Project folder (under Assets or Packages) everything is written to.")]
        public string outputFolder = "";

        [Header("Frames and links")]
        [Tooltip("ROS frame the robot plans in. Empty = from the files, else \"world\".")]
        public string planningFrame = "";
        public string baseLink = "";
        [Tooltip("Link the end-effector handle follows and IK solves for.")]
        public string endEffectorLink = "";

        [Header("Planning groups")]
        public string armGroup = "";
        [Tooltip("The arm's joints in order from the base. Empty = the arm group's joints, else a guess.")]
        public string[] armJoints = Array.Empty<string>();
        public string gripperGroup = "";
        [Tooltip("Every joint of the gripper, followers included. Empty = the gripper group's joints, else a guess.")]
        public string[] gripperJoints = Array.Empty<string>();

        [Header("Gripper")]
        [Tooltip("The robot has no gripper: do not look for one.")]
        public bool noGripper;
        [Tooltip("The gripper joint that is commanded. Empty = from the files, else a guess.")]
        public string gripperDriverJoint = "";
        [Tooltip("Use Open / Closed below instead of the named poses.")]
        public bool setGripperPositions;
        [Tooltip("Driver joint position when open (radians, or metres for a prismatic joint).")]
        public float gripperOpen;
        [Tooltip("Driver joint position when closed (radians, or metres for a prismatic joint).")]
        public float gripperClosed;
        [Tooltip("Gap between the fingertips when open, in metres. 0 = measured from the URDF.")]
        public float gripperOpenWidth;

        [Header("Initial pose")]
        [Tooltip("Name of the pose the robot starts in. Empty = from the ROS-names file.")]
        public string initialPose = "";
        [Tooltip("Joint values of the initial pose, for a robot without an SRDF. Saved as a pose under the name above (\"initial\" when that is empty).")]
        public JointValue[] initialPoseValues = Array.Empty<JointValue>();

        [Header("ROS names (relative to the robot's namespace)")]
        public string jointStatesTopic = "";
        [Tooltip("<controller>/joint_trajectory")]
        public string trajectoryCommandTopic = "";

        /// <summary>The asset name in use: <see cref="assetName"/>, or the display name without what a file name cannot hold.</summary>
        public string ResolvedAssetName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(assetName)) return assetName.Trim();
                var name = new System.Text.StringBuilder();
                foreach (char c in displayName ?? "")
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '-') name.Append(c);
                return name.Length > 0 ? name.ToString() : "Robot";
            }
        }
    }
}
