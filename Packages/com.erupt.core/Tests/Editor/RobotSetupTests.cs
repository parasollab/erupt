using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Erupt.Robot;

namespace Erupt.Editor.Tests
{
    /// <summary>
    /// Robot Setup on small robots written here: a URDF whose root is a <c>world</c> link, a
    /// continuous joint, a revolute gripper with mimic joints, a prismatic two-finger gripper,
    /// with and without an SRDF and a ROS-names file, and running it again on the same output.
    /// The geometry is primitive, so no mesh files are needed.
    /// </summary>
    public class RobotSetupTests
    {
        private const string OutputRoot = "Assets/__RobotSetupTests";
        private string source;

        [SetUp]
        public void SetUp()
        {
            source = FileUtil.GetUniqueTempPathInProject();
            Directory.CreateDirectory(source);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(OutputRoot);
            if (Directory.Exists(source)) Directory.Delete(source, true);
        }

        // ---------- descriptions ----------

        private static string Link(string name, string size = "0.05 0.05 0.1") =>
            $@"<link name=""{name}"">
                 <visual><geometry><box size=""{size}""/></geometry></visual>
                 <collision><geometry><box size=""{size}""/></geometry></collision>
                 <inertial><mass value=""1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial>
               </link>";

        private static string Joint(string name, string type, string parent, string child, string xyz, string axis = null, string limit = null, string mimic = null) =>
            $@"<joint name=""{name}"" type=""{type}"">
                 <parent link=""{parent}""/><child link=""{child}""/><origin xyz=""{xyz}"" rpy=""0 0 0""/>
                 {(axis != null ? $@"<axis xyz=""{axis}""/>" : "")}{limit}{mimic}
               </joint>";

        // world -> base -> l1 (revolute) -> l2 (revolute) -> l3 (continuous) -> hand, and on the
        // hand a revolute gripper: "grip" drives the left finger, the right finger and the left
        // tip mimic it. An accessory joint hangs off the base and belongs to no group.
        private const string MimicArmUrdf_Name = "mimic_arm";
        private static string MimicArmUrdf(string j1Upper = "2.0") => $@"<?xml version=""1.0""?>
<robot name=""{MimicArmUrdf_Name}"">
  <link name=""world""/>
  {Link("base", "0.2 0.2 0.1")}
  {Joint("world_to_base", "fixed", "world", "base", "0 0 0.05")}
  {Link("l1")}
  {Joint("j1", "revolute", "base", "l1", "0 0 0.1", "0 0 1", $@"<limit lower=""-2.0"" upper=""{j1Upper}"" velocity=""1.5"" effort=""10""/>")}
  {Link("l2")}
  {Joint("j2", "revolute", "l1", "l2", "0 0 0.2", "0 1 0", @"<limit lower=""-1.5"" upper=""1.5"" velocity=""1.0"" effort=""10""/>")}
  {Link("l3")}
  {Joint("j3", "continuous", "l2", "l3", "0 0 0.2", "0 0 1", @"<limit velocity=""2.0"" effort=""10""/>")}
  {Link("hand", "0.1 0.05 0.02")}
  {Joint("l3_to_hand", "fixed", "l3", "hand", "0 0 0.1")}
  {Link("lf", "0.01 0.02 0.05")}
  {Joint("grip", "revolute", "hand", "lf", "0.04 0 0.02", "0 -1 0", @"<limit lower=""0"" upper=""0.8"" velocity=""0.5"" effort=""5""/>")}
  {Link("rf", "0.01 0.02 0.05")}
  {Joint("grip_r", "revolute", "hand", "rf", "-0.04 0 0.02", "0 -1 0", @"<limit lower=""-0.8"" upper=""0"" velocity=""0.5"" effort=""5""/>", @"<mimic joint=""grip"" multiplier=""-1""/>")}
  {Link("ltip", "0.01 0.02 0.02")}
  {Joint("tip", "revolute", "lf", "ltip", "0 0 0.05", "0 -1 0", @"<limit lower=""-0.8"" upper=""0"" velocity=""0.5"" effort=""5""/>", @"<mimic joint=""grip"" multiplier=""-1"" offset=""0""/>")}
  {Link("rpad", "0.01 0.02 0.02")}
  {Joint("rf_to_rpad", "fixed", "rf", "rpad", "0 0 0.05")}
  {Link("fan", "0.02 0.02 0.02")}
  {Joint("fan_joint", "continuous", "base", "fan", "0.1 0 0.05", "0 0 1")}
</robot>";

        private const string MimicArmSrdf = @"<?xml version=""1.0""?>
<robot name=""mimic_arm"">
  <virtual_joint name=""fixed_base"" type=""fixed"" parent_frame=""world"" child_link=""world""/>
  <group name=""arm""><chain base_link=""base"" tip_link=""hand""/></group>
  <group name=""gripper""><link name=""lf""/><link name=""rf""/><link name=""ltip""/><link name=""rpad""/></group>
  <group name=""arm_and_gripper""><group name=""arm""/><group name=""gripper""/></group>
  <end_effector name=""claw"" parent_link=""hand"" group=""gripper"" parent_group=""arm""/>
  <passive_joint name=""grip_r""/>
  <passive_joint name=""tip""/>
  <group_state name=""ready"" group=""arm""><joint name=""j1"" value=""0.3""/><joint name=""j2"" value=""-0.4""/><joint name=""j3"" value=""1.0""/></group_state>
  <group_state name=""far"" group=""arm""><joint name=""j1"" value=""3.0""/><joint name=""j2"" value=""0""/><joint name=""j3"" value=""7.0""/></group_state>
  <group_state name=""open"" group=""gripper""><joint name=""grip"" value=""0""/></group_state>
  <group_state name=""shut"" group=""gripper""><joint name=""grip"" value=""0.7""/></group_state>
  <group_state name=""both"" group=""arm_and_gripper""><joint name=""j1"" value=""0""/></group_state>
  <disable_collisions link1=""lf"" link2=""rf"" reason=""Never""/>
</robot>";

        private const string MimicArmRosNames = @"{
  ""robot"": ""mimic_arm"", ""planning_frame"": ""world"", ""arm_group"": ""arm"", ""arm_joints"": [""j1"", ""j2"", ""j3""],
  ""joint_states_topic"": ""joint_states"", ""trajectory_command_topic"": ""arm_controller/joint_trajectory"",
  ""named_poses"": {""arm"": [""ready"", ""far""], ""gripper"": [""open"", ""shut""]},
  ""base_link"": ""base"", ""gripper_controller"": {""name"": ""claw_controller"", ""type"": ""x/Y"", ""joints"": [""grip""]},
  ""gripper_group"": ""gripper"", ""end_effector_link"": ""hand"", ""initial_pose"": ""ready"",
  ""gripper_open_pose"": ""open"", ""gripper_closed_pose"": ""shut""
}";

        // base_link -> a1 (revolute) -> a2 (revolute) -> palm, with two prismatic fingers, the
        // second following the first. No world link, and no SRDF or ROS-names file to go with it.
        private const string SliderArmUrdf = @"<?xml version=""1.0""?>
<robot name=""slider_arm"">
  <link name=""base_link""><visual><geometry><cylinder radius=""0.1"" length=""0.1""/></geometry></visual><collision><geometry><cylinder radius=""0.1"" length=""0.1""/></geometry></collision><inertial><mass value=""1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <link name=""a1""><visual><geometry><box size=""0.05 0.05 0.2""/></geometry></visual><collision><geometry><box size=""0.05 0.05 0.2""/></geometry></collision><inertial><mass value=""1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <joint name=""shoulder"" type=""revolute""><parent link=""base_link""/><child link=""a1""/><origin xyz=""0 0 0.1""/><axis xyz=""0 0 1""/><limit lower=""-3"" upper=""3"" velocity=""2"" effort=""10""/></joint>
  <link name=""a2""><visual><geometry><box size=""0.05 0.05 0.2""/></geometry></visual><collision><geometry><box size=""0.05 0.05 0.2""/></geometry></collision><inertial><mass value=""1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <joint name=""elbow"" type=""revolute""><parent link=""a1""/><child link=""a2""/><origin xyz=""0 0 0.2""/><axis xyz=""0 1 0""/><limit lower=""-2"" upper=""2"" velocity=""2"" effort=""10""/></joint>
  <link name=""palm""><visual><geometry><box size=""0.1 0.05 0.02""/></geometry></visual><collision><geometry><box size=""0.1 0.05 0.02""/></geometry></collision><inertial><mass value=""0.5""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <joint name=""a2_to_palm"" type=""fixed""><parent link=""a2""/><child link=""palm""/><origin xyz=""0 0 0.2""/></joint>
  <link name=""f1""><visual><geometry><box size=""0.01 0.02 0.05""/></geometry></visual><collision><geometry><box size=""0.01 0.02 0.05""/></geometry></collision><inertial><mass value=""0.1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <joint name=""finger1"" type=""prismatic""><parent link=""palm""/><child link=""f1""/><origin xyz=""0 0 0.03""/><axis xyz=""1 0 0""/><limit lower=""0"" upper=""0.04"" velocity=""0.2"" effort=""20""/></joint>
  <link name=""f2""><visual><geometry><box size=""0.01 0.02 0.05""/></geometry></visual><collision><geometry><box size=""0.01 0.02 0.05""/></geometry></collision><inertial><mass value=""0.1""/><inertia ixx=""0.01"" ixy=""0"" ixz=""0"" iyy=""0.01"" iyz=""0"" izz=""0.01""/></inertial></link>
  <joint name=""finger2"" type=""prismatic""><parent link=""palm""/><child link=""f2""/><origin xyz=""0 0 0.03""/><axis xyz=""-1 0 0""/><limit lower=""0"" upper=""0.04"" velocity=""0.2"" effort=""20""/><mimic joint=""finger1""/></joint>
</robot>";

        private string Write(string name, string text)
        {
            string path = Path.Combine(source, name);
            File.WriteAllText(path, text);
            return path;
        }

        private RobotSetupSettings MimicArm(string output = OutputRoot + "/MimicArm") => new RobotSetupSettings
        {
            urdfPath = Write("mimic_arm.urdf", MimicArmUrdf()),
            srdfPath = Write("mimic_arm.srdf", MimicArmSrdf),
            rosNamesPath = Write("mimic_arm.ros.json", MimicArmRosNames),
            displayName = "Mimic Arm",
            outputFolder = output,
        };

        private static string[] Problems(RobotSetupReport report) => report.Texts(RobotSetupLevel.Problem).ToArray();
        private static string[] Warnings(RobotSetupReport report) => report.Texts(RobotSetupLevel.Warning).ToArray();

        // ---------- describing (nothing is written) ----------

        [Test]
        public void Describe_WithSrdfAndRosNames_TakesEverythingFromTheFiles()
        {
            var report = new RobotSetupReport();
            ResolvedRobot robot = RobotSetup.Describe(MimicArm(), report);

            CollectionAssert.IsEmpty(Problems(report));
            Assert.AreEqual("Mimic Arm", robot.displayName);
            Assert.AreEqual("world", robot.planningFrame);
            Assert.AreEqual("world", robot.planningFrameLink, "The planning frame is the URDF's world link.");
            Assert.AreEqual("base", robot.baseLink);
            Assert.AreEqual("hand", robot.endEffectorLink);
            Assert.AreEqual("arm", robot.armGroup);
            Assert.AreEqual("gripper", robot.gripperGroup);
            Assert.AreEqual("claw", robot.endEffectorName);
            Assert.AreEqual("joint_states", robot.jointStatesTopic);
            Assert.AreEqual("arm_controller/joint_trajectory", robot.trajectoryCommandTopic);

            // Roles: the arm from the ROS-names file, the gripper from the SRDF group (its
            // "passive" followers included), and the joint in neither is not driven.
            CollectionAssert.AreEqual(new[] { "j1", "j2", "j3" }, robot.ArmJoints);
            CollectionAssert.AreEquivalent(new[] { "grip", "grip_r", "tip" }, robot.GripperJoints);
            Assert.AreEqual(JointRole.Passive, robot.joints.Single(j => j.name == "fan_joint").role);
            Assert.AreEqual(7, robot.joints.Count, "Every movable joint of the URDF, and no fixed one.");

            // Kinds, limits, velocity limits and mimic rules from the URDF.
            JointSpec j1 = robot.joints.Single(j => j.name == "j1");
            Assert.AreEqual(JointKind.Revolute, j1.kind);
            Assert.AreEqual("l1", j1.childLink);
            Assert.AreEqual(-2f, j1.urdfLower, 1e-6f);
            Assert.AreEqual(2f, j1.urdfUpper, 1e-6f);
            Assert.AreEqual(1.5f, j1.maxVelocity, 1e-6f);
            JointSpec j3 = robot.joints.Single(j => j.name == "j3");
            Assert.AreEqual(JointKind.Continuous, j3.kind);
            Assert.IsFalse(j3.HasLimits);
            Assert.AreEqual(2f, j3.maxVelocity, 1e-6f);
            JointSpec right = robot.joints.Single(j => j.name == "grip_r");
            Assert.AreEqual("grip", right.mimic.leader);
            Assert.AreEqual(-1f, right.mimic.multiplier, 1e-6f);
            Assert.AreEqual(-0.8f, right.urdfLower, 1e-6f);
            Assert.IsFalse(robot.joints.Single(j => j.name == "grip").IsMimic);

            // Named poses of the arm and the gripper group, not of the group made of both.
            CollectionAssert.AreEqual(new[] { "ready", "far", "open", "shut" }, robot.poses.Select(p => p.name));
            Assert.AreEqual("ready", robot.initialPose);
            // A pose outside the limits is stored at the limit and reported; a continuous joint has none.
            NamedPose far = robot.poses.Single(p => p.name == "far");
            Assert.AreEqual(2f, far.joints.Single(v => v.joint == "j1").value, 1e-6f);
            Assert.AreEqual(7f, far.joints.Single(v => v.joint == "j3").value, 1e-6f);
            Assert.IsTrue(Warnings(report).Any(w => w.Contains("'far'") && w.Contains("'j1'") && w.Contains("stored as 2")), string.Join("\n", Warnings(report)));

            // The gripper: the controller's joint drives, open and closed from the named poses,
            // the opening measured between the fingertips.
            Assert.AreEqual(GripperKind.Revolute, robot.gripper.kind);
            Assert.AreEqual("grip", robot.gripper.driverJoint);
            Assert.AreEqual(0f, robot.gripper.open, 1e-6f);
            Assert.AreEqual(0.7f, robot.gripper.closed, 1e-6f);
            // Each fingertip swings 5 cm x sin(0.7) towards the other.
            Assert.AreEqual(2f * 0.05f * Mathf.Sin(0.7f), robot.gripper.openWidthMetres, 1e-4f);

            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            try
            {
                robot.ApplyTo(profile);
                CollectionAssert.IsEmpty(profile.Validate(), "What Robot Setup describes is a valid profile.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void Describe_WithoutSrdfOrRosNames_GuessesWhatIsSound_AndSaysWhatItCouldNot()
        {
            var report = new RobotSetupReport();
            ResolvedRobot robot = RobotSetup.Describe(new RobotSetupSettings { urdfPath = Write("slider_arm.urdf", SliderArmUrdf) }, report);

            CollectionAssert.IsEmpty(Problems(report));
            Assert.AreEqual("slider_arm", robot.displayName, "The URDF's robot name when none was typed.");
            Assert.AreEqual("world", robot.planningFrame);
            Assert.AreEqual("base_link", robot.planningFrameLink, "Without a world link the planning frame sits on the root link.");
            Assert.AreEqual("base_link", robot.baseLink);
            Assert.AreEqual("palm", robot.endEffectorLink, "The link the gripper is built on.");
            CollectionAssert.AreEqual(new[] { "shoulder", "elbow" }, robot.ArmJoints);
            CollectionAssert.AreEquivalent(new[] { "finger1", "finger2" }, robot.GripperJoints);

            Assert.AreEqual(GripperKind.Prismatic, robot.gripper.kind);
            Assert.AreEqual("finger1", robot.gripper.driverJoint);
            Assert.AreEqual(0.04f, robot.gripper.open, 1e-6f, "The fingers are apart at the upper limit.");
            Assert.AreEqual(0f, robot.gripper.closed, 1e-6f);
            Assert.AreEqual(0.08f, robot.gripper.openWidthMetres, 1e-5f, "Both fingers' travel.");

            Assert.AreEqual("", robot.initialPose);
            Assert.AreEqual("", robot.trajectoryCommandTopic);
            Assert.AreEqual("joint_states", robot.jointStatesTopic);
            string[] warnings = Warnings(report);
            Assert.IsTrue(warnings.Any(w => w.Contains("No initial pose")), string.Join("\n", warnings));
            Assert.IsTrue(warnings.Any(w => w.Contains("No trajectory command topic")), string.Join("\n", warnings));
            Assert.IsTrue(warnings.Any(w => w.Contains("open and closed positions were guessed")), string.Join("\n", warnings));
        }

        [Test]
        public void Describe_TypedValues_WinOverTheFiles_AndFillWhatIsMissing()
        {
            var settings = new RobotSetupSettings
            {
                urdfPath = Write("slider_arm.urdf", SliderArmUrdf),
                displayName = "Slider",
                endEffectorLink = "f1",
                trajectoryCommandTopic = "slider_controller/joint_trajectory",
                jointStatesTopic = "states",
                initialPoseValues = new[] { new JointValue { joint = "shoulder", value = 0.5f }, new JointValue { joint = "elbow", value = -0.5f } },
                setGripperPositions = true, gripperOpen = 0.03f, gripperClosed = 0.005f, gripperOpenWidth = 0.05f,
            };
            var report = new RobotSetupReport();
            ResolvedRobot robot = RobotSetup.Describe(settings, report);

            Assert.AreEqual("f1", robot.endEffectorLink);
            Assert.IsTrue(Warnings(report).Any(w => w.Contains("moved by the gripper joint")), "A handle on a finger is worth a warning.");
            Assert.AreEqual("slider_controller/joint_trajectory", robot.trajectoryCommandTopic);
            Assert.AreEqual("states", robot.jointStatesTopic);
            Assert.AreEqual("initial", robot.initialPose);
            NamedPose initial = robot.poses.Single(p => p.name == "initial");
            Assert.AreEqual(0.5f, initial.joints.Single(v => v.joint == "shoulder").value, 1e-6f);
            Assert.AreEqual(0.03f, robot.gripper.open, 1e-6f);
            Assert.AreEqual(0.005f, robot.gripper.closed, 1e-6f);
            Assert.AreEqual(0.05f, robot.gripper.openWidthMetres, 1e-6f);

            // The same robot told it has no gripper.
            report = new RobotSetupReport();
            robot = RobotSetup.Describe(new RobotSetupSettings { urdfPath = settings.urdfPath, noGripper = true, armJoints = new[] { "shoulder", "elbow" } }, report);
            Assert.AreEqual(GripperKind.None, robot.gripper.kind);
            CollectionAssert.IsEmpty(robot.GripperJoints);
            Assert.AreEqual(2, robot.joints.Count(j => j.role == JointRole.Passive), "The fingers are there but not driven.");
        }

        [Test]
        public void Describe_SaysClearlyWhatIsWrong()
        {
            // Not a URDF.
            var report = new RobotSetupReport();
            Assert.IsNull(RobotSetup.Describe(new RobotSetupSettings { urdfPath = Write("x.urdf", "<robot name='x'><xacro:macro/></robot>") }, report));
            Assert.IsTrue(Problems(report).Any(p => p.Contains("not valid XML")), string.Join("\n", Problems(report)));

            report = new RobotSetupReport();
            Assert.IsNull(RobotSetup.Describe(new RobotSetupSettings { urdfPath = Path.Combine(source, "missing.urdf") }, report));
            Assert.IsTrue(Problems(report).Any(p => p.Contains("No URDF file")));

            // Names that are not in the URDF, a missing file, a pose that is not there.
            RobotSetupSettings settings = MimicArm();
            settings.armJoints = new[] { "j1", "nope" };
            settings.endEffectorLink = "no_such_link";
            settings.initialPose = "no_such_pose";
            settings.gripperDriverJoint = "j2";
            settings.srdfPath = Path.Combine(source, "missing.srdf");
            report = new RobotSetupReport();
            RobotSetup.Describe(settings, report);
            string all = string.Join("\n", Problems(report));
            StringAssert.Contains("No SRDF file", all);
            StringAssert.Contains("The arm joint 'nope' is not a joint of the URDF", all);
            StringAssert.Contains("The end-effector link 'no_such_link' is not a link of the URDF", all);
            StringAssert.Contains("The initial pose 'no_such_pose' is not one of the named poses", all);
            StringAssert.Contains("The gripper driver joint 'j2' is not one of the gripper joints", all);

            // A robot with nothing that looks like a gripper.
            report = new RobotSetupReport();
            string plain = SliderArmUrdf.Replace("type=\"prismatic\"", "type=\"fixed\"");
            ResolvedRobot robot = RobotSetup.Describe(new RobotSetupSettings { urdfPath = Write("plain.urdf", plain) }, report);
            Assert.AreEqual(GripperKind.None, robot.gripper.kind);
            Assert.IsTrue(Warnings(report).Any(w => w.Contains("No gripper was identified")), string.Join("\n", Warnings(report)));
            CollectionAssert.AreEqual(new[] { "shoulder", "elbow" }, robot.ArmJoints, "Without a gripper the arm is the longest chain of joints.");
        }

        [Test]
        public void SettingsFromArguments_ReadsEveryFlag()
        {
            string[] args =
            {
                "Unity", "-urdf", "a.urdf", "-srdf", "a.srdf", "-rosNames", "a.ros.json", "-out", "Assets/A", "-name", "A Robot", "-assetName", "A",
                "-endEffectorLink", "tool", "-baseLink", "base", "-planningFrame", "map", "-armGroup", "arm", "-armJoints", "j1, j2",
                "-gripperGroup", "hand", "-gripperJoints", "g1,g2", "-gripperDriver", "g1", "-gripperOpen", "0.04", "-gripperClosed", "0",
                "-gripperOpenWidth", "0.08", "-initialPose", "ready", "-initialPoseValues", "j1=0.5,j2=-1", "-jointStatesTopic", "js", "-trajectoryTopic", "c/joint_trajectory",
            };
            RobotSetupSettings s = RobotSetup.SettingsFromArguments(args, new RobotSetupSettings());

            Assert.AreEqual("a.urdf", s.urdfPath);
            Assert.AreEqual("a.srdf", s.srdfPath);
            Assert.AreEqual("a.ros.json", s.rosNamesPath);
            Assert.AreEqual("Assets/A", s.outputFolder);
            Assert.AreEqual("A Robot", s.displayName);
            Assert.AreEqual("A", s.ResolvedAssetName);
            Assert.AreEqual("tool", s.endEffectorLink);
            Assert.AreEqual("map", s.planningFrame);
            CollectionAssert.AreEqual(new[] { "j1", "j2" }, s.armJoints);
            CollectionAssert.AreEqual(new[] { "g1", "g2" }, s.gripperJoints);
            Assert.IsTrue(s.setGripperPositions);
            Assert.AreEqual(0.04f, s.gripperOpen, 1e-6f);
            Assert.AreEqual(0.08f, s.gripperOpenWidth, 1e-6f);
            Assert.AreEqual(-1f, s.initialPoseValues[1].value, 1e-6f);
            Assert.AreEqual("c/joint_trajectory", s.trajectoryCommandTopic);
            Assert.IsFalse(s.noGripper);
            Assert.AreEqual("MyRobot2", new RobotSetupSettings { displayName = "My Robot 2!" }.ResolvedAssetName);
        }

        // ---------- running (assets are written) ----------

        [Test]
        public void Run_MakesTheWiredPrefab_TheGhost_TheProfile_AndTheReport()
        {
            Assume.That(RobotImport.Available, "The URDF Importer package is not installed.");
            RobotSetupResult result = RobotSetup.Run(MimicArm());

            Assert.IsTrue(result.Succeeded, result.Report.ToMarkdown());
            CollectionAssert.IsEmpty(Problems(result.Report), result.Report.ToMarkdown());
            Assert.AreEqual(OutputRoot + "/MimicArm/MimicArm.prefab", result.PrefabPath);

            // Everything is in the output folder, the description included.
            foreach (string file in new[] { "MimicArm.prefab", "MimicArmGhost.prefab", "MimicArm.asset", "MimicArm.setup.md", "mimic_arm.urdf", "mimic_arm.srdf", "mimic_arm.ros.json" })
                Assert.IsTrue(File.Exists($"{OutputRoot}/MimicArm/{file}"), file);
            StringAssert.Contains("# Robot Setup: Mimic Arm", File.ReadAllText(result.ReportPath));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(result.ProfilePath);
            var ghost = AssetDatabase.LoadAssetAtPath<GameObject>(result.GhostPath);

            // The profile.
            CollectionAssert.IsEmpty(profile.Validate());
            Assert.AreEqual("Mimic Arm", profile.displayName);
            Assert.AreSame(ghost, profile.ghostPrefab);
            Assert.AreEqual("ready", profile.initialPose);

            // The wiring: the same components and references as a robot made by hand.
            var instance = prefab.GetComponent<RobotInstance>();
            Assert.IsNotNull(instance);
            Assert.AreSame(profile, instance.Profile);
            CollectionAssert.IsEmpty(instance.Validate());
            DirectArticulationIKController controller = instance.Controller;
            Assert.AreSame(prefab.transform, controller.Root);
            Assert.AreEqual("hand", controller.EndEffector.name);
            Assert.AreSame(controller, new SerializedObject(instance).FindProperty("controller").objectReferenceValue);
            var interaction = prefab.GetComponentInChildren<RobotInteractionController>(true);
            var binding = prefab.GetComponentInChildren<RobotInteractionRouterBinding>(true);
            var player = prefab.GetComponentInChildren<JointTrajectoryPlayer>(true);
            var ghosts = prefab.GetComponentInChildren<RobotGhosts>(true);
            Assert.AreSame(interaction, new SerializedObject(instance).FindProperty("interaction").objectReferenceValue);
            Assert.AreSame(player, new SerializedObject(instance).FindProperty("trajectoryPlayer").objectReferenceValue);
            Assert.AreSame(ghosts, new SerializedObject(instance).FindProperty("ghosts").objectReferenceValue);
            Assert.AreSame(controller, new SerializedObject(interaction).FindProperty("ikController").objectReferenceValue);
            Assert.AreSame(controller.EndEffector, new SerializedObject(interaction).FindProperty("endEffector").objectReferenceValue);
            Assert.AreSame(interaction, new SerializedObject(binding).FindProperty("robotInteraction").objectReferenceValue);
            Assert.AreSame(controller, new SerializedObject(player).FindProperty("ikController").objectReferenceValue);
            Assert.AreSame(prefab, ghosts.realRobot);
            Transform handle = interaction.Handle;
            Assert.AreEqual(RobotSetup.EndEffectorHandleName, handle.name);
            Assert.IsTrue(handle.GetComponent<SphereCollider>().isTrigger);
            Assert.Less(Vector3.Distance(handle.position, controller.EndEffector.position), 1e-4f);

            // The planning-frame anchor: on the world link, with ERUPT's axes (a quarter turn
            // from the importer's), and the RobotInstance points at it.
            Transform frame = prefab.transform.Find(RobotSetup.PlanningFrameObjectName);
            Assert.IsNotNull(frame);
            Assert.AreSame(frame, new SerializedObject(instance).FindProperty("planningFrame").objectReferenceValue);
            Transform world = prefab.transform.Find("world");
            Assert.IsNotNull(world, "The URDF's world link is the first link under the robot.");
            Assert.Less(Vector3.Distance(frame.position, world.position), 1e-5f);
            // ROS x of the robot (the importer's z) is the frame's x; ROS y (the importer's -x) is the frame's z.
            Assert.Less(Vector3.Distance(frame.right, world.forward), 1e-4f);
            Assert.Less(Vector3.Distance(frame.forward, -world.right), 1e-4f);
            Assert.Less(Vector3.Distance(frame.up, world.up), 1e-4f);

            // The imported joints are all there; the importer's own drivers are not.
            Assert.AreEqual(7, prefab.GetComponentsInChildren<ArticulationBody>(true).Count(b => b.jointType != ArticulationJointType.FixedJoint));
            Assert.IsFalse(prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(b => b.GetType().Name == "UrdfMimicJoint" || b.GetType().Name == "Controller"),
                "ERUPT's controller drives the joints, followers included.");

            // The ghost: the bare articulated model.
            Assert.IsEmpty(ghost.GetComponentsInChildren<MonoBehaviour>(true));
            Assert.IsEmpty(ghost.GetComponentsInChildren<Collider>(true));
            Assert.IsNotEmpty(ghost.GetComponentsInChildren<Renderer>(true));
            Assert.IsNull(ghost.transform.Find(RobotSetup.InteractionObjectName));
            CollectionAssert.AreEquivalent(
                prefab.GetComponentsInChildren<ArticulationBody>(true).Select(b => b.name),
                ghost.GetComponentsInChildren<ArticulationBody>(true).Select(b => b.name), "The ghost has the robot's links, by name.");

            // What the report found.
            string found = string.Join("\n", result.Report.Texts(RobotSetupLevel.Found));
            StringAssert.Contains("Joint limits in the import agree with the URDF", found);
            StringAssert.Contains("Link frames in the import agree with the URDF", found);
        }

        [Test]
        public void Run_Again_UpdatesInPlace_KeepsGuidsAndFileIds_AndWhatWasTunedByHand()
        {
            Assume.That(RobotImport.Available, "The URDF Importer package is not installed.");
            RobotSetupSettings settings = MimicArm();
            RobotSetupResult first = RobotSetup.Run(settings);
            Assert.IsTrue(first.Succeeded, first.Report.ToMarkdown());

            var identities = new Dictionary<string, (string guid, long id)>();
            void Remember(string label, Object asset)
            {
                Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id), label);
                identities[label] = (guid, id);
            }
            void Collect(RobotSetupResult result)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
                Remember("prefab root", prefab);
                Remember("root transform", prefab.transform);
                Remember("RobotInstance", prefab.GetComponent<RobotInstance>());
                Remember("interaction", prefab.GetComponentInChildren<RobotInteractionController>(true));
                Remember("router binding", prefab.GetComponentInChildren<RobotInteractionRouterBinding>(true));
                Remember("planning frame", prefab.transform.Find(RobotSetup.PlanningFrameObjectName));
                Remember("a link", RobotSetup.FindLink(prefab.transform, "l2"));
                // Objects the importer names alike on every link keep their identity too.
                Remember("a visual", RobotSetup.FindLink(prefab.transform, "l2").GetComponentInChildren<Renderer>(true));
                Remember("a collider", RobotSetup.FindLink(prefab.transform, "lf").GetComponentInChildren<Collider>(true));
                Remember("profile", AssetDatabase.LoadAssetAtPath<RobotProfile>(result.ProfilePath));
                Remember("ghost", AssetDatabase.LoadAssetAtPath<GameObject>(result.GhostPath));
            }
            Collect(first);
            var before = new Dictionary<string, (string guid, long id)>(identities);

            // Tune by hand what Robot Setup does not describe.
            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(first.ProfilePath);
            profile.limitMarginRadians = 0.05f;
            profile.limitUserVelocity = true;
            profile.names.rosPrefix = "ros_";
            profile.gripper.speed = 0.25f;
            profile.TryGetJoint("j2", out JointSpec j2);
            j2.overrideLimits = true;
            j2.lower = -1f;
            j2.upper = 1f;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // The description changes: a wider limit on j1.
            File.WriteAllText(settings.urdfPath, MimicArmUrdf(j1Upper: "2.5"));
            RobotSetupResult second = RobotSetup.Run(settings);
            Assert.IsTrue(second.Succeeded, second.Report.ToMarkdown());
            CollectionAssert.IsEmpty(Problems(second.Report), second.Report.ToMarkdown());

            Collect(second);
            foreach (KeyValuePair<string, (string guid, long id)> pair in before)
                Assert.AreEqual(pair.Value, identities[pair.Key], $"{pair.Key}: a scene that uses the robot refers to it by GUID and file ID.");

            profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(second.ProfilePath);
            profile.TryGetJoint("j1", out JointSpec j1);
            Assert.AreEqual(2.5f, j1.urdfUpper, 1e-6f, "The description's change arrived.");
            Assert.AreEqual(0.05f, profile.limitMarginRadians, 1e-6f);
            Assert.IsTrue(profile.limitUserVelocity);
            Assert.AreEqual("ros_", profile.names.rosPrefix);
            Assert.AreEqual(0.25f, profile.gripper.speed, 1e-6f);
            profile.TryGetJoint("j2", out j2);
            Assert.IsTrue(j2.overrideLimits);
            Assert.AreEqual(1f, j2.upper, 1e-6f);
            Assert.IsNotNull(profile.ghostPrefab);
            StringAssert.Contains("Updated in place", string.Join("\n", second.Report.Texts(RobotSetupLevel.Found)));
        }

        [Test]
        public void Run_PrismaticGripper_WithoutSrdf_AndARootThatIsNotWorld()
        {
            Assume.That(RobotImport.Available, "The URDF Importer package is not installed.");
            RobotSetupResult result = RobotSetup.Run(new RobotSetupSettings
            {
                urdfPath = Write("slider_arm.urdf", SliderArmUrdf),
                displayName = "Slider Arm",
                outputFolder = OutputRoot + "/Slider",
                trajectoryCommandTopic = "slider_controller/joint_trajectory",
                initialPoseValues = new[] { new JointValue { joint = "shoulder", value = 0.4f }, new JointValue { joint = "elbow", value = 0.6f } },
            });

            Assert.IsTrue(result.Succeeded, result.Report.ToMarkdown());
            CollectionAssert.IsEmpty(Problems(result.Report), result.Report.ToMarkdown());
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(result.ProfilePath);
            Assert.AreEqual("SliderArm", prefab.name);
            CollectionAssert.IsEmpty(prefab.GetComponent<RobotInstance>().Validate());
            Assert.AreEqual(GripperKind.Prismatic, profile.gripper.kind);
            Assert.AreEqual("initial", profile.initialPose);
            Assert.AreEqual(0.6f, profile.InitialPose.joints.Single(v => v.joint == "elbow").value, 1e-6f);

            // Without a world link the anchor sits on the root link.
            Transform frame = prefab.transform.Find(RobotSetup.PlanningFrameObjectName);
            Transform root = RobotSetup.FindLink(prefab.transform, "base_link");
            Assert.Less(Vector3.Distance(frame.position, root.position), 1e-5f);
            Assert.Less(Vector3.Distance(frame.right, root.forward), 1e-4f);
            Assert.AreEqual(ArticulationJointType.PrismaticJoint, RobotSetup.FindLink(prefab.transform, "f1").GetComponent<ArticulationBody>().jointType);
        }

        [Test]
        public void Run_StopsBeforeTheImport_WhenAMeshIsMissing_OrTheFolderIsOutsideTheProject()
        {
            string withMesh = SliderArmUrdf.Replace(@"<cylinder radius=""0.1"" length=""0.1""/></geometry></visual>", @"<mesh filename=""package://slider_description/meshes/base.dae""/></geometry></visual>");
            RobotSetupResult result = RobotSetup.Run(new RobotSetupSettings { urdfPath = Write("slider_arm.urdf", withMesh), outputFolder = OutputRoot + "/NoMesh" });
            Assert.IsFalse(result.Succeeded);
            string problems = string.Join("\n", Problems(result.Report));
            StringAssert.Contains("1 mesh file the URDF names could not be found", problems);
            StringAssert.Contains("package://slider_description/meshes/base.dae", problems);
            Assert.IsFalse(File.Exists(OutputRoot + "/NoMesh/slider_arm.prefab"));

            result = RobotSetup.Run(new RobotSetupSettings { urdfPath = Write("slider_arm.urdf", SliderArmUrdf), outputFolder = "/tmp/elsewhere" });
            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("not inside this project's Assets or Packages folder", string.Join("\n", Problems(result.Report)));
        }
    }
}
