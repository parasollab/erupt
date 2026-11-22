using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Erupt.Robot;
using UnityEngine;

namespace Erupt.Editor
{
    /// <summary>
    /// A robot description with every question answered: what Robot Setup puts into the
    /// <see cref="RobotProfile"/> and uses to wire the prefab. Built by <see cref="Resolve"/>
    /// from the URDF, the optional SRDF and ROS-names file, and the values the user typed.
    /// A typed value wins over the ROS-names file, which wins over the SRDF, which wins over
    /// a guess from the URDF; the report says which one each answer came from.
    /// </summary>
    public sealed class ResolvedRobot
    {
        public string displayName = "";
        /// <summary>What the ROS side calls the robot model (the ROS-names file's <c>robot</c>); empty when no file says.</summary>
        public string rosRobotName = "";
        public string planningFrame = RobotProfile.DefaultPlanningFrame;
        /// <summary>The URDF link whose frame is the planning frame.</summary>
        public string planningFrameLink = "";
        public string baseLink = "";
        public string endEffectorLink = "";
        public string armGroup = "";
        public string gripperGroup = "";
        public string endEffectorName = "";
        public List<JointSpec> joints = new List<JointSpec>();
        public List<NamedPose> poses = new List<NamedPose>();
        public string initialPose = "";
        public GripperSpec gripper = new GripperSpec();
        public string jointStatesTopic = RobotProfile.DefaultJointStatesTopic;
        public string trajectoryCommandTopic = "";

        public List<string> ArmJoints => joints.Where(j => j.role == JointRole.Arm).Select(j => j.name).ToList();
        public List<string> GripperJoints => joints.Where(j => j.role == JointRole.Gripper).Select(j => j.name).ToList();

        /// <summary>
        /// Write this description into a profile. What the description does not cover (limit
        /// overrides and margins, the velocity switch, the ROS name rule, the gripper speed,
        /// the ghost prefab) is left as the profile has it, so a profile that was tuned by hand
        /// keeps its tuning when Robot Setup runs again.
        /// </summary>
        public void ApplyTo(RobotProfile profile)
        {
            var previous = new Dictionary<string, JointSpec>();
            foreach (JointSpec joint in profile.joints ?? Array.Empty<JointSpec>())
                if (joint != null && !string.IsNullOrEmpty(joint.name)) previous[joint.name] = joint;

            profile.displayName = displayName;
            if (!string.IsNullOrEmpty(rosRobotName)) profile.rosRobotName = rosRobotName;
            profile.baseLink = baseLink;
            profile.endEffectorLink = endEffectorLink;
            profile.planningFrame = planningFrame;
            profile.armGroup = armGroup;
            profile.gripperGroup = gripperGroup;
            profile.endEffectorName = endEffectorName;

            profile.joints = joints.Select(joint =>
            {
                var spec = new JointSpec
                {
                    name = joint.name, childLink = joint.childLink, kind = joint.kind, role = joint.role,
                    urdfLower = joint.urdfLower, urdfUpper = joint.urdfUpper, maxVelocity = joint.maxVelocity,
                    mimic = new MimicSpec { leader = joint.mimic.leader, multiplier = joint.mimic.multiplier, offset = joint.mimic.offset },
                };
                if (previous.TryGetValue(joint.name, out JointSpec old))
                {
                    spec.overrideLimits = old.overrideLimits;
                    spec.lower = old.lower;
                    spec.upper = old.upper;
                }
                return spec;
            }).ToArray();

            profile.poses = poses.Select(pose => new NamedPose
            {
                name = pose.name,
                joints = pose.joints.Select(v => new JointValue { joint = v.joint, value = v.value }).ToArray(),
            }).ToArray();
            profile.initialPose = initialPose;

            float speed = profile.gripper != null && profile.gripper.speed > 0f ? profile.gripper.speed : new GripperSpec().speed;
            profile.gripper = new GripperSpec
            {
                kind = gripper.kind, driverJoint = gripper.driverJoint, open = gripper.open, closed = gripper.closed,
                openWidthMetres = gripper.openWidthMetres, speed = speed,
            };

            profile.jointStatesTopic = jointStatesTopic;
            profile.trajectoryCommandTopic = trajectoryCommandTopic;
            profile.names ??= new RobotNameMap();
        }

        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Answer every question about the robot. Never throws on a description it cannot fully
        /// work out: what is missing or doubtful goes into <paramref name="report"/>.
        /// </summary>
        public static ResolvedRobot Resolve(UrdfModel urdf, SrdfModel srdf, RosNamesFile ros, RobotSetupSettings settings, RobotSetupReport report)
        {
            if (urdf == null) throw new ArgumentNullException(nameof(urdf));
            settings ??= new RobotSetupSettings();
            report ??= new RobotSetupReport();

            var robot = new ResolvedRobot();
            robot.displayName = !string.IsNullOrWhiteSpace(settings.displayName) ? settings.displayName.Trim()
                : !string.IsNullOrEmpty(urdf.RobotName) ? urdf.RobotName : settings.ResolvedAssetName;

            robot.rosRobotName = ros != null ? (ros.robot ?? "").Trim() : "";

            foreach (string problem in urdf.Problems) report.Problem("URDF: " + problem);
            if (srdf != null) foreach (string problem in srdf.Problems) report.Warn("SRDF: " + problem);

            List<UrdfJointInfo> movable = urdf.Joints.Where(j => j.IsMovable).ToList();
            report.Found($"URDF '{urdf.RobotName}': {urdf.Links.Count} links, {urdf.Joints.Count} joints, {movable.Count} of them movable; root link '{urdf.RootLink}'.");
            foreach (UrdfJointInfo joint in urdf.Joints)
                if (joint.type == UrdfJointType.Floating || joint.type == UrdfJointType.Planar)
                    report.Warn($"Joint '{joint.name}' is {joint.typeName}; ERUPT drives joints with one degree of freedom and leaves this one alone.");
            if (movable.Count == 0)
                report.Problem("The URDF has no revolute, continuous or prismatic joint: there is nothing to drive.");

            // ---- planning frame ----
            string virtualParent = srdf?.VirtualJoints.FirstOrDefault(v => !string.IsNullOrEmpty(v.parentFrame))?.parentFrame;
            robot.planningFrame = Pick(report, "Planning frame",
                (settings.planningFrame, "typed"), (ros?.planning_frame, "ROS-names file"),
                (virtualParent, "SRDF virtual joint"), (RobotProfile.DefaultPlanningFrame, "default"));

            SrdfVirtualJoint virtualJoint = srdf?.VirtualJoints.FirstOrDefault(v => v.parentFrame == robot.planningFrame && urdf.HasLink(v.childLink));
            if (urdf.HasLink(robot.planningFrame))
            {
                robot.planningFrameLink = robot.planningFrame;
                report.Found($"The planning frame '{robot.planningFrame}' is a link of the URDF; the planning-frame anchor sits on it.");
            }
            else if (virtualJoint != null)
            {
                robot.planningFrameLink = virtualJoint.childLink;
                report.Found($"The planning frame '{robot.planningFrame}' is not a link of the URDF; the SRDF attaches it to '{virtualJoint.childLink}' ({virtualJoint.type}), so the planning-frame anchor sits on that link.");
            }
            else
            {
                robot.planningFrameLink = urdf.RootLink;
                report.Found($"The planning frame '{robot.planningFrame}' is not a link of the URDF; the planning-frame anchor sits on the root link '{urdf.RootLink}'. " +
                             "That is right when the ROS side starts the robot at the origin of its planning frame.");
            }

            // ---- groups ----
            SrdfEndEffector srdfEndEffector = null;
            string guessedArmGroup = null, guessedGripperGroup = null;
            if (srdf != null)
            {
                // The arm is the simple group (not made of other groups) with the most driven
                // joints; the gripper is the group an end effector names.
                guessedArmGroup = srdf.Groups
                    .Where(g => g.subgroups.Count == 0)
                    .OrderByDescending(g => srdf.JointsOf(g.name, urdf).Count(n => urdf.TryGetJoint(n, out UrdfJointInfo j) && j.IsMovable && !j.IsMimic))
                    .Select(g => g.name).FirstOrDefault();
            }
            robot.armGroup = Pick(report, "Arm group",
                (settings.armGroup, "typed"), (ros?.arm_group, "ROS-names file"), (guessedArmGroup, "SRDF group with the most driven joints"));
            if (srdf != null && !string.IsNullOrEmpty(robot.armGroup) && !srdf.TryGetGroup(robot.armGroup, out _))
                report.Problem($"The arm group '{robot.armGroup}' is not a group of the SRDF ({GroupNames(srdf)}).");

            if (srdf != null)
            {
                srdfEndEffector = srdf.EndEffectors.FirstOrDefault(e => string.IsNullOrEmpty(e.parentGroup) || e.parentGroup == robot.armGroup)
                                  ?? srdf.EndEffectors.FirstOrDefault();
                guessedGripperGroup = srdfEndEffector?.group;
            }
            if (!settings.noGripper)
            {
                robot.gripperGroup = Pick(report, "Gripper group",
                    (settings.gripperGroup, "typed"), (ros?.gripper_group, "ROS-names file"), (guessedGripperGroup, "SRDF end effector"));
                if (srdf != null && !string.IsNullOrEmpty(robot.gripperGroup) && !srdf.TryGetGroup(robot.gripperGroup, out _))
                    report.Problem($"The gripper group '{robot.gripperGroup}' is not a group of the SRDF ({GroupNames(srdf)}).");
            }
            if (srdfEndEffector != null && (string.IsNullOrEmpty(robot.gripperGroup) || srdfEndEffector.group == robot.gripperGroup))
                robot.endEffectorName = srdfEndEffector.name;

            // ---- gripper joints ----
            var gripperJoints = new List<string>();
            string gripperSource = null;
            GripperGuess guess = settings.noGripper ? null : GuessGripper(urdf);
            if (settings.noGripper)
            {
                report.Found("No gripper (as set).");
            }
            else if (settings.gripperJoints != null && settings.gripperJoints.Length > 0)
            {
                gripperJoints.AddRange(settings.gripperJoints);
                gripperSource = "typed";
            }
            else if (srdf != null && srdf.TryGetGroup(robot.gripperGroup, out _))
            {
                gripperJoints.AddRange(srdf.JointsOf(robot.gripperGroup, urdf));
                gripperSource = $"SRDF group '{robot.gripperGroup}'";
            }
            else if (guess != null)
            {
                gripperJoints.AddRange(guess.joints);
                gripperSource = "guessed: " + guess.reason;
            }

            // ---- arm joints ----
            var armJoints = new List<string>();
            string armSource;
            if (settings.armJoints != null && settings.armJoints.Length > 0)
            {
                armJoints.AddRange(settings.armJoints);
                armSource = "typed";
            }
            else if (ros != null && ros.arm_joints.Length > 0)
            {
                armJoints.AddRange(ros.arm_joints);
                armSource = "ROS-names file";
            }
            else if (srdf != null && srdf.TryGetGroup(robot.armGroup, out _))
            {
                armJoints.AddRange(srdf.JointsOf(robot.armGroup, urdf));
                armSource = $"SRDF group '{robot.armGroup}'";
            }
            else
            {
                string typedTip = FirstSet(settings.endEffectorLink, ros?.end_effector_link);
                string tip = urdf.HasLink(typedTip) ? typedTip : guess != null ? guess.baseLink : DeepestLink(urdf);
                // A joint that belongs to the gripper is not the arm's, wherever the tip is.
                armJoints.AddRange(urdf.JointsTo(tip).Select(j => j.name).Where(name => !gripperJoints.Contains(name)));
                armSource = urdf.HasLink(typedTip) ? $"guessed: the joints between the root and '{tip}'"
                    : guess != null ? $"guessed: the joints between the root and the gripper's base '{tip}'"
                    : $"guessed: the longest chain of joints (it ends at '{tip}')";
            }

            // Keep what the URDF can drive: known, movable, and for the arm not a follower.
            armJoints = Driven(armJoints, urdf, report, "arm").Where(name =>
            {
                urdf.TryGetJoint(name, out UrdfJointInfo joint);
                if (!joint.IsMimic) return true;
                report.Warn($"Arm joint '{name}' mimics '{joint.mimic.joint}'; ERUPT's IK does not drive followers, so it is left out of the arm.");
                return false;
            }).ToList();
            gripperJoints = Driven(gripperJoints, urdf, report, "gripper").Where(name => !armJoints.Contains(name)).ToList();

            if (armJoints.Count == 0) report.Problem("No arm joints were found. Type the arm's joints in, or give an SRDF or a ROS-names file.");
            else report.Found($"Arm joints ({armSource}): {string.Join(", ", armJoints)}.");
            if (gripperJoints.Count > 0) report.Found($"Gripper joints ({gripperSource}): {string.Join(", ", gripperJoints)}.");

            // ---- joints ----
            var passive = new HashSet<string>(srdf != null ? srdf.PassiveJoints : new List<string>());
            IEnumerable<UrdfJointInfo> ordered = armJoints.Concat(gripperJoints).Select(name => { urdf.TryGetJoint(name, out UrdfJointInfo j); return j; })
                .Concat(movable.Where(j => !armJoints.Contains(j.name) && !gripperJoints.Contains(j.name)));
            foreach (UrdfJointInfo joint in ordered)
            {
                var spec = new JointSpec
                {
                    name = joint.name,
                    childLink = joint.child,
                    kind = joint.type == UrdfJointType.Continuous ? JointKind.Continuous
                         : joint.type == UrdfJointType.Prismatic ? JointKind.Prismatic : JointKind.Revolute,
                    role = armJoints.Contains(joint.name) ? JointRole.Arm : gripperJoints.Contains(joint.name) ? JointRole.Gripper : JointRole.Passive,
                    maxVelocity = joint.velocity,
                };
                if (joint.type != UrdfJointType.Continuous && joint.hasRange)
                {
                    spec.urdfLower = joint.lower;
                    spec.urdfUpper = joint.upper;
                    if (joint.lower > joint.upper)
                        report.Problem($"Joint '{joint.name}' has its lower limit ({Num(joint.lower)}) above its upper limit ({Num(joint.upper)}) in the URDF.");
                    else if (Mathf.Approximately(joint.lower, joint.upper))
                        report.Warn($"Joint '{joint.name}' has the same lower and upper limit ({Num(joint.lower)}) in the URDF; it is treated as a joint without position limits.");
                }
                else if (joint.type != UrdfJointType.Continuous)
                {
                    report.Warn($"Joint '{joint.name}' is {joint.typeName} but the URDF gives it no position limits; it moves without limits, like a continuous joint.");
                }
                if (joint.velocity <= 0f && spec.role != JointRole.Passive)
                    report.Warn($"Joint '{joint.name}' has no velocity limit in the URDF; user-driven motion of it cannot be velocity limited.");
                if (joint.IsMimic)
                    spec.mimic = new MimicSpec { leader = joint.mimic.joint, multiplier = joint.mimic.multiplier, offset = joint.mimic.offset };

                if (spec.role == JointRole.Passive)
                {
                    string why = passive.Contains(joint.name) ? "the SRDF calls it passive" : "it is in neither the arm nor the gripper";
                    report.Found($"Joint '{joint.name}' is not driven ({why}).");
                }
                robot.joints.Add(spec);
            }
            foreach (JointSpec spec in robot.joints)
            {
                if (spec.role != JointRole.Gripper || !spec.IsMimic) continue;
                if (!gripperJoints.Contains(spec.mimic.leader))
                    report.Warn($"Gripper joint '{spec.name}' mimics '{spec.mimic.leader}', which is not a gripper joint; it will not follow.");
            }

            // ---- links ----
            string srdfBase = srdf?.ChainBaseOf(robot.armGroup);
            string firstArmParent = armJoints.Count > 0 && urdf.TryGetJoint(armJoints[0], out UrdfJointInfo firstArm) ? firstArm.parent : null;
            robot.baseLink = Pick(report, "Base link",
                (settings.baseLink, "typed"), (ros?.base_link, "ROS-names file"), (srdfBase, "base of the SRDF arm chain"),
                (firstArmParent, "guessed: the link the first arm joint starts at"), (urdf.RootLink, "URDF root link"));
            if (!urdf.HasLink(robot.baseLink))
                report.Problem($"The base link '{robot.baseLink}' is not a link of the URDF.");

            string srdfTip = FirstSet(srdfEndEffector?.parentLink, srdf?.ChainTipOf(robot.armGroup));
            string lastArmChild = armJoints.Count > 0 && urdf.TryGetJoint(armJoints[armJoints.Count - 1], out UrdfJointInfo lastArm) ? lastArm.child : null;
            robot.endEffectorLink = Pick(report, "End-effector link",
                (settings.endEffectorLink, "typed"), (ros?.end_effector_link, "ROS-names file"), (srdfTip, "SRDF end effector"),
                (guess?.baseLink, "guessed: the link the gripper is built on"), (lastArmChild, "guessed: the link the last arm joint moves"));
            if (!urdf.HasLink(robot.endEffectorLink))
                report.Problem($"The end-effector link '{robot.endEffectorLink}' is not a link of the URDF.");
            else
            {
                if (string.IsNullOrWhiteSpace(settings.endEffectorLink) && !string.IsNullOrEmpty(ros?.end_effector_link) &&
                    !string.IsNullOrEmpty(srdfTip) && srdfTip != ros.end_effector_link)
                    report.Warn($"The ROS-names file names the end-effector link '{ros.end_effector_link}', the SRDF '{srdfTip}'. " +
                                $"'{robot.endEffectorLink}' is used: that is where the end-effector handle sits. Type the other one in if it should sit there.");
                if (lastArmChild != null && !urdf.IsAtOrBelow(robot.endEffectorLink, lastArmChild))
                    report.Warn($"The end-effector link '{robot.endEffectorLink}' is not moved by the last arm joint '{armJoints[armJoints.Count - 1]}'; IK would not move it with the whole arm.");
                foreach (string gripperJoint in gripperJoints)
                    if (urdf.TryGetJoint(gripperJoint, out UrdfJointInfo g) && urdf.IsAtOrBelow(robot.endEffectorLink, g.child))
                    {
                        report.Warn($"The end-effector link '{robot.endEffectorLink}' is moved by the gripper joint '{gripperJoint}'; the handle would move when the gripper does.");
                        break;
                    }
            }

            // ---- named poses ----
            ResolvePoses(robot, urdf, srdf, settings, report);
            robot.initialPose = Pick(report, "Initial pose", (settings.initialPose, "typed"), (ros?.initial_pose, "ROS-names file"));
            if (settings.initialPoseValues != null && settings.initialPoseValues.Length > 0)
            {
                if (string.IsNullOrEmpty(robot.initialPose)) robot.initialPose = "initial";
                robot.poses.RemoveAll(p => p.name == robot.initialPose);
                robot.poses.Add(new NamedPose
                {
                    name = robot.initialPose,
                    joints = settings.initialPoseValues.Where(v => v != null && !string.IsNullOrEmpty(v.joint))
                        .Select(v => new JointValue { joint = v.joint, value = v.value }).ToArray(),
                });
                report.Found($"Initial pose '{robot.initialPose}' from the typed joint values.");
            }
            if (string.IsNullOrEmpty(robot.initialPose))
                report.Warn("No initial pose: the robot starts with every joint at zero. Name one of the poses, or type joint values in.");
            else if (!robot.poses.Any(p => p.name == robot.initialPose))
                report.Problem($"The initial pose '{robot.initialPose}' is not one of the named poses ({string.Join(", ", robot.poses.Select(p => p.name))}).");

            // ---- gripper ----
            if (!settings.noGripper) ResolveGripper(robot, urdf, srdf, ros, settings, guess, report);

            // ---- topics ----
            robot.jointStatesTopic = Pick(report, "Joint states topic",
                (settings.jointStatesTopic, "typed"), (ros?.joint_states_topic, "ROS-names file"), (RobotProfile.DefaultJointStatesTopic, "default"));
            robot.trajectoryCommandTopic = Pick(report, "Trajectory command topic",
                (settings.trajectoryCommandTopic, "typed"), (ros?.trajectory_command_topic, "ROS-names file"));
            if (string.IsNullOrEmpty(robot.trajectoryCommandTopic))
                report.Warn("No trajectory command topic (<controller>/joint_trajectory): plans can be made and previewed but not executed until the profile names one.");

            return robot;
        }

        // The SRDF's named poses of the arm and gripper groups. Two groups may use the same
        // name; the profile's poses share one namespace, so the gripper's gets its group in front.
        private static void ResolvePoses(ResolvedRobot robot, UrdfModel urdf, SrdfModel srdf, RobotSetupSettings settings, RobotSetupReport report)
        {
            if (srdf == null) return;
            foreach (string group in new[] { robot.armGroup, robot.gripperGroup })
            {
                if (string.IsNullOrEmpty(group)) continue;
                foreach (SrdfGroupState state in srdf.States.Where(s => s.group == group))
                {
                    string name = state.name;
                    if (robot.poses.Any(p => p.name == name))
                    {
                        string renamed = $"{group}/{state.name}";
                        if (robot.poses.Any(p => p.name == renamed))
                        {
                            report.Warn($"The SRDF defines the pose '{state.name}' of group '{group}' more than once; the first is kept.");
                            continue;
                        }
                        report.Warn($"The pose name '{state.name}' is used by more than one group; the one of '{group}' is stored as '{renamed}'.");
                        name = renamed;
                    }

                    var values = new List<JointValue>();
                    foreach (KeyValuePair<string, float> value in state.joints)
                    {
                        if (!urdf.TryGetJoint(value.Key, out UrdfJointInfo joint) || !joint.IsMovable)
                        {
                            report.Warn($"Pose '{state.name}' names '{value.Key}', which is not a movable joint of the URDF; that value is left out.");
                            continue;
                        }
                        values.Add(new JointValue { joint = value.Key, value = value.Value });
                    }
                    robot.poses.Add(new NamedPose { name = name, joints = values.ToArray() });
                }
            }
            if (robot.poses.Count > 0)
                report.Found($"Named poses (SRDF): {string.Join(", ", robot.poses.Select(p => p.name))}.");

            // A pose outside the limits is stored at the limit: that is where the robot would stop.
            foreach (NamedPose pose in robot.poses)
                foreach (JointValue value in pose.joints)
                {
                    JointSpec joint = robot.joints.Find(j => j.name == value.joint);
                    if (joint == null || joint.Allows(value.value)) continue;
                    float limited = Mathf.Clamp(value.value, joint.Lower, joint.Upper);
                    report.Warn($"Pose '{pose.name}' puts '{value.joint}' at {Num(value.value)}, outside its limits [{Num(joint.Lower)}, {Num(joint.Upper)}]; it is stored as {Num(limited)}.");
                    value.value = limited;
                }
        }

        private static void ResolveGripper(ResolvedRobot robot, UrdfModel urdf, SrdfModel srdf, RosNamesFile ros,
                                           RobotSetupSettings settings, GripperGuess guess, RobotSetupReport report)
        {
            List<string> gripperJoints = robot.GripperJoints;
            if (gripperJoints.Count == 0)
            {
                report.Warn("No gripper was identified: the gripper verb will answer that the robot has none. " +
                            "If the robot has one, type its joints and its driver joint in; if it has none, tick \"No gripper\".");
                return;
            }

            // The driver: typed, the gripper controller's joint, the one gripper joint that
            // follows no other, or the joint most others follow.
            string controllerJoint = ros != null && ros.gripper_controller.joints.Length > 0 ? ros.gripper_controller.joints[0] : null;
            List<string> independent = gripperJoints.Where(n => !robot.joints.Find(j => j.name == n).IsMimic).ToList();
            string mostFollowed = gripperJoints
                .OrderByDescending(n => robot.joints.Count(j => j.role == JointRole.Gripper && j.IsMimic && j.mimic.leader == n))
                .FirstOrDefault(n => independent.Contains(n));
            string driverName = Pick(report, "Gripper driver joint",
                (settings.gripperDriverJoint, "typed"), (controllerJoint, "ROS-names file, gripper controller"),
                (independent.Count == 1 ? independent[0] : null, "the one gripper joint that follows no other"),
                (mostFollowed, "guessed: the gripper joint most others follow"));

            JointSpec driver = robot.joints.Find(j => j.name == driverName);
            if (driver == null || driver.role != JointRole.Gripper)
            {
                report.Problem($"The gripper driver joint '{driverName}' is not one of the gripper joints ({string.Join(", ", gripperJoints)}).");
                return;
            }
            if (driver.IsMimic)
                report.Warn($"The gripper driver joint '{driver.name}' itself mimics '{driver.mimic.leader}'.");
            foreach (string name in independent)
                if (name != driver.name)
                    report.Warn($"Gripper joint '{name}' neither is the driver nor mimics it; opening and closing the gripper does not move it.");

            robot.gripper.kind = driver.kind == JointKind.Prismatic ? GripperKind.Prismatic : GripperKind.Revolute;
            robot.gripper.driverJoint = driver.name;

            // Open and closed positions of the driver.
            bool haveOpen = false, haveClosed = false;
            if (settings.setGripperPositions)
            {
                robot.gripper.open = settings.gripperOpen;
                robot.gripper.closed = settings.gripperClosed;
                haveOpen = haveClosed = true;
                report.Found($"Gripper open {Num(robot.gripper.open)}, closed {Num(robot.gripper.closed)} (typed).");
            }
            else
            {
                haveOpen = DriverValueInPose(robot, driver, ros?.gripper_open_pose, "ROS-names file", report, out robot.gripper.open)
                           || DriverValueInPose(robot, driver, FindGripperPose(robot, srdf, "open", "opened"), "SRDF pose name", report, out robot.gripper.open);
                haveClosed = DriverValueInPose(robot, driver, ros?.gripper_closed_pose, "ROS-names file", report, out robot.gripper.closed)
                             || DriverValueInPose(robot, driver, FindGripperPose(robot, srdf, "close", "closed"), "SRDF pose name", report, out robot.gripper.closed);
            }

            var gripperSet = new HashSet<string>(gripperJoints);
            if (!haveOpen || !haveClosed)
            {
                if (!driver.HasLimits)
                {
                    report.Problem($"The gripper's open and closed positions are not known and the driver joint '{driver.name}' has no limits to take them from. Type them in.");
                    return;
                }
                // The end of the driver's range that leaves the wider gap between the fingertips is "open".
                FingertipPair(urdf, gripperSet, driver.name, out string tipA, out string tipB);
                float atLower = Separation(urdf, tipA, tipB, driver.name, driver.Lower);
                float atUpper = Separation(urdf, tipA, tipB, driver.name, driver.Upper);
                bool upperIsOpen = driver.kind == JointKind.Prismatic ? atUpper >= atLower : atUpper > atLower;
                float open = upperIsOpen ? driver.Upper : driver.Lower, closed = upperIsOpen ? driver.Lower : driver.Upper;
                if (!haveOpen) robot.gripper.open = open;
                if (!haveClosed) robot.gripper.closed = closed;
                report.Warn($"The gripper's {(haveOpen ? "closed position was" : haveClosed ? "open position was" : "open and closed positions were")} guessed from the driver joint's limits and the finger geometry: " +
                            $"open {Num(robot.gripper.open)}, closed {Num(robot.gripper.closed)}. Check them (the SRDF's gripper poses, named in a ROS-names file, are the better source).");
            }

            if (!driver.Allows(robot.gripper.open))
            {
                float limited = Mathf.Clamp(robot.gripper.open, driver.Lower, driver.Upper);
                report.Warn($"The gripper's open position {Num(robot.gripper.open)} is outside the limits of '{driver.name}' [{Num(driver.Lower)}, {Num(driver.Upper)}]; {Num(limited)} is used.");
                robot.gripper.open = limited;
            }
            if (!driver.Allows(robot.gripper.closed))
            {
                float limited = Mathf.Clamp(robot.gripper.closed, driver.Lower, driver.Upper);
                report.Warn($"The gripper's closed position {Num(robot.gripper.closed)} is outside the limits of '{driver.name}' [{Num(driver.Lower)}, {Num(driver.Upper)}]; {Num(limited)} is used.");
                robot.gripper.closed = limited;
            }
            if (Mathf.Approximately(robot.gripper.open, robot.gripper.closed))
                report.Problem($"The gripper's open and closed positions are the same ({Num(robot.gripper.open)}).");

            // The gap between the fingertips when open.
            if (settings.gripperOpenWidth > 0f)
            {
                robot.gripper.openWidthMetres = settings.gripperOpenWidth;
                report.Found($"Gripper open width {Num(robot.gripper.openWidthMetres)} m (typed).");
            }
            else
            {
                string between = "";
                if (FingertipPair(urdf, gripperSet, driver.name, out string tipA, out string tipB))
                {
                    robot.gripper.openWidthMetres = Mathf.Abs(
                        Separation(urdf, tipA, tipB, driver.name, robot.gripper.open) - Separation(urdf, tipA, tipB, driver.name, robot.gripper.closed));
                    between = string.IsNullOrEmpty(tipB) ? $"how far '{tipA}' travels" : $"the change in the distance between '{tipA}' and '{tipB}'";
                }
                if (robot.gripper.openWidthMetres > 1e-5f)
                    report.Found($"Gripper open width {Num(robot.gripper.openWidthMetres)} m, measured in the URDF: {between} from closed to open.");
                else
                    report.Problem("The gripper's open width could not be measured from the URDF (nothing moves between the closed and the open position). Type it in.");
            }
        }

        private static string FindGripperPose(ResolvedRobot robot, SrdfModel srdf, params string[] names)
        {
            if (srdf == null || string.IsNullOrEmpty(robot.gripperGroup)) return null;
            foreach (string wanted in names)
            {
                SrdfGroupState state = srdf.States.FirstOrDefault(s => s.group == robot.gripperGroup && string.Equals(s.name, wanted, StringComparison.OrdinalIgnoreCase));
                if (state != null) return state.name;
            }
            return null;
        }

        // The driver joint's value in a named pose; when the pose lists only a follower, the
        // driver position that puts the follower there.
        private static bool DriverValueInPose(ResolvedRobot robot, JointSpec driver, string poseName, string source, RobotSetupReport report, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(poseName)) return false;
            NamedPose pose = robot.poses.Find(p => p.name == poseName) ?? robot.poses.Find(p => p.name == $"{robot.gripperGroup}/{poseName}");
            if (pose == null)
            {
                report.Warn($"The gripper pose '{poseName}' ({source}) is not one of the named poses.");
                return false;
            }
            foreach (JointValue v in pose.joints)
                if (v.joint == driver.name)
                {
                    value = v.value;
                    report.Found($"Gripper position {Num(value)} of '{driver.name}' from the pose '{pose.name}' ({source}).");
                    return true;
                }
            foreach (JointValue v in pose.joints)
            {
                JointSpec follower = robot.joints.Find(j => j.name == v.joint);
                if (follower == null || !follower.IsMimic || follower.mimic.leader != driver.name) continue;
                value = follower.mimic.LeaderFor(v.value);
                report.Found($"Gripper position {Num(value)} of '{driver.name}' from its follower '{follower.name}' in the pose '{pose.name}' ({source}).");
                return true;
            }
            report.Warn($"The pose '{pose.name}' ({source}) does not give the gripper driver joint '{driver.name}' a value.");
            return false;
        }

        // ---- the gripper, from the URDF alone ----

        private sealed class GripperGuess
        {
            public List<string> joints;
            public string baseLink;
            public string reason;
        }

        // A joint that other joints mimic drives a gripper: the gripper is everything that
        // moves below the link that joint starts at. Failing that, prismatic joints at the end
        // of the tree are fingers.
        private static GripperGuess GuessGripper(UrdfModel urdf)
        {
            UrdfJointInfo leader = urdf.Joints
                .Where(j => j.IsMovable && !j.IsMimic)
                .Select(j => (joint: j, followers: urdf.Joints.Count(f => f.IsMovable && f.IsMimic && f.mimic.joint == j.name)))
                .Where(e => e.followers > 0)
                .OrderByDescending(e => e.followers)
                .Select(e => e.joint).FirstOrDefault();
            if (leader != null)
            {
                var below = new HashSet<string>(urdf.Subtree(leader.parent));
                return new GripperGuess
                {
                    joints = urdf.Joints.Where(j => j.IsMovable && below.Contains(j.child)).Select(j => j.name).ToList(),
                    baseLink = leader.parent,
                    reason = $"'{leader.name}' is mimicked by other joints, so everything that moves below '{leader.parent}' is the gripper",
                };
            }

            List<UrdfJointInfo> fingers = urdf.Joints
                .Where(j => j.type == UrdfJointType.Prismatic && !urdf.Subtree(j.child).Skip(1).Any(l => urdf.ParentJointOf(l).IsMovable))
                .ToList();
            if (fingers.Count > 0 && urdf.Joints.Any(j => j.IsMovable && !fingers.Contains(j)))
            {
                return new GripperGuess
                {
                    joints = fingers.Select(j => j.name).ToList(),
                    baseLink = fingers[0].parent,
                    reason = "prismatic joints with nothing movable below them are fingers",
                };
            }
            return null;
        }

        // The link at the end of the chain with the most movable joints that follow no other.
        private static string DeepestLink(UrdfModel urdf)
        {
            string best = urdf.RootLink;
            int bestCount = -1;
            foreach (string link in urdf.Links)
            {
                if (urdf.ChildJointsOf(link).Count > 0) continue;
                int count = urdf.JointsTo(link).Count(j => j.IsMovable && !j.IsMimic);
                if (count > bestCount) { best = link; bestCount = count; }
            }
            return best;
        }

        // The two links the gripper's opening is measured between: among the gripper links
        // with nothing movable below them (the fingertips), the pair whose distance the driver
        // changes most over its travel. With one such link, b is empty: a single finger.
        private static bool FingertipPair(UrdfModel urdf, HashSet<string> gripperJoints, string driver, out string a, out string b)
        {
            a = b = "";
            var tips = new List<string>();
            foreach (string name in gripperJoints)
            {
                if (!urdf.TryGetJoint(name, out UrdfJointInfo joint)) continue;
                foreach (string link in urdf.Subtree(joint.child))
                {
                    bool movableBelow = urdf.Subtree(link).Skip(1).Any(l => urdf.ParentJointOf(l).IsMovable);
                    if (!movableBelow && !tips.Contains(link)) tips.Add(link);
                }
            }
            if (tips.Count == 0) return false;
            a = tips[0];
            if (tips.Count == 1) return true;

            urdf.TryGetJoint(driver, out UrdfJointInfo driverJoint);
            float low = driverJoint != null && driverJoint.hasRange ? driverJoint.lower : 0f;
            float high = driverJoint != null && driverJoint.hasRange ? driverJoint.upper : 1f;
            float bestChange = -1f;
            for (int i = 0; i < tips.Count; i++)
                for (int j = i + 1; j < tips.Count; j++)
                {
                    float change = Mathf.Abs(Separation(urdf, tips[i], tips[j], driver, low) - Separation(urdf, tips[i], tips[j], driver, high));
                    if (change > bestChange) { bestChange = change; a = tips[i]; b = tips[j]; }
                }
            return true;
        }

        // The distance between two links with the driver at a position and its followers
        // where their mimic rules put them. With b empty: how far a stands from its place
        // with the driver at zero.
        private static float Separation(UrdfModel urdf, string a, string b, string driver, float driverPosition)
        {
            Dictionary<string, float> at = urdf.WithMimics(new Dictionary<string, float> { [driver] = driverPosition });
            return string.IsNullOrEmpty(b)
                ? Vector3.Distance(urdf.LinkPosition(a, at), urdf.LinkPosition(a, urdf.WithMimics(null)))
                : Vector3.Distance(urdf.LinkPosition(a, at), urdf.LinkPosition(b, at));
        }

        // ---- helpers ----

        private static List<string> Driven(List<string> names, UrdfModel urdf, RobotSetupReport report, string what)
        {
            var result = new List<string>();
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || result.Contains(name)) continue;
                if (!urdf.TryGetJoint(name, out UrdfJointInfo joint))
                    report.Problem($"The {what} joint '{name}' is not a joint of the URDF.");
                else if (joint.IsMovable)
                    result.Add(name);
            }
            return result;
        }

        // The first candidate that has a value, reported with where it came from.
        private static string Pick(RobotSetupReport report, string what, params (string value, string source)[] candidates)
        {
            foreach ((string value, string source) in candidates)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                report.Found($"{what}: '{value.Trim()}' ({source}).");
                return value.Trim();
            }
            return "";
        }

        private static string FirstSet(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        private static string GroupNames(SrdfModel srdf) =>
            srdf.Groups.Count > 0 ? "its groups: " + string.Join(", ", srdf.Groups.Select(g => g.name)) : "it has no groups";

        private static string Num(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
