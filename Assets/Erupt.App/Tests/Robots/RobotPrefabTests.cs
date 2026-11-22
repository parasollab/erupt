#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Std;
using RosMessageTypes.Trajectory;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Robot.Tests;
using Erupt.Ros.Tests;

namespace Erupt.App.Robots.Tests
{
    /// <summary>The robots the project ships: every <see cref="RobotProfile"/> in a <c>com.erupt.robot.*</c> package.</summary>
    public static class ShippedRobots
    {
        /// <summary>
        /// Asset paths of the shipped robot prefabs. A robot package is covered by being in the
        /// project: nothing has to be added here.
        /// </summary>
        public static IEnumerable<string> PrefabPaths()
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:RobotProfile", new[] { "Packages" }))
            {
                string profilePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!profilePath.StartsWith("Packages/com.erupt.robot.")) continue;
                paths.Add(profilePath.Substring(0, profilePath.Length - ".asset".Length) + ".prefab");
            }
            paths.Sort(System.StringComparer.Ordinal);
            // NUnit needs at least one case; a project without robot packages gets one that is skipped.
            if (paths.Count == 0) paths.Add("");
            return paths;
        }
    }

    /// <summary>
    /// Every shipped robot prefab, on a fake bus: it is a complete robot when dropped into a
    /// scene (registers, starts in its initial pose inside its limits, validates), its IK
    /// reaches, its gripper opens and closes with the followers following, a trajectory through
    /// its named poses replays, its ghosts copy it, and three of them coexist in three
    /// namespaces. Nothing here names a robot, a joint count or a gripper kind.
    /// </summary>
    public class RobotPrefabTests
    {
        private FakeRosBus bus;
        private RobotRegistry robots;
        private GameObject routerObject, selectionObject;
        private readonly List<GameObject> spawned = new();

        [SetUp]
        public void SetUp()
        {
            bus = new FakeRosBus();
            robots = FakeRobots.Registry(bus);
            // What a scene with the ERUPT root has for a robot to find: the router and the selection.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no interaction sources found"));
            routerObject = new GameObject("router");
            routerObject.AddComponent<InteractionRouter>();
            selectionObject = new GameObject("selection");
            selectionObject.AddComponent<SelectionService>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
            foreach (RobotOwned owned in Object.FindObjectsByType<RobotOwned>(FindObjectsSortMode.None))
                if (owned != null) Object.DestroyImmediate(owned.gameObject);
            Object.DestroyImmediate(routerObject);
            Object.DestroyImmediate(selectionObject);
        }

        private static GameObject Load(string prefabPath)
        {
            Assume.That(!string.IsNullOrEmpty(prefabPath), "No robot package (com.erupt.robot.*) is in this project.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab, $"The profile next to it exists, but there is no prefab at '{prefabPath}'.");
            return prefab;
        }

        // Instantiates the prefab switched off, so the id and namespace are set before it registers.
        private RobotInstance Spawn(GameObject prefab, string id, string rosNamespace, Vector3 position)
        {
            var holder = new GameObject("holder");
            holder.SetActive(false);
            GameObject go = Object.Instantiate(prefab, position, Quaternion.Euler(0f, 90f, 0f), holder.transform);
            go.name = id;
            var robot = go.GetComponent<RobotInstance>();
            Assert.IsNotNull(robot, "The prefab has a RobotInstance on its root.");
            robot.Configure(id, rosNamespace, robot.Profile);
            robot.UseRegistry(robots);
            go.transform.SetParent(null, true);
            Object.DestroyImmediate(holder);
            spawned.Add(go);
            return robot;
        }

        private static IEnumerator Settle()
        {
            yield return new WaitForFixedUpdate();
            yield return null;
            yield return null;   // the joint handles exist after a LateUpdate
        }

        private static float Joint(RobotInstance robot, string joint)
        {
            Assert.IsTrue(robot.Model.TryGetJointAngle(joint, out float position), $"{robot.Id} has no joint '{joint}'");
            return position;
        }

        private static void AssertInsideLimits(RobotInstance robot, string when)
        {
            foreach (string joint in robot.Model.GetJointStateNames())
            {
                if (!robot.Model.Limits.TryGet(joint, out JointLimit limit) || !limit.HasLimits) continue;
                float position = Joint(robot, joint);
                Assert.That(position, Is.InRange(limit.Lower - 1e-4f, limit.Upper + 1e-4f), $"{joint} {when}: outside [{limit.Lower}, {limit.Upper}]");
            }
        }

        [UnityTest]
        public IEnumerator DroppedIntoAScene_ItIsACompleteRobot_InItsInitialPose([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            GameObject prefab = Load(prefabPath);
            RobotInstance robot = Spawn(prefab, "", "", new Vector3(0.5f, 0.2f, -1f));
            yield return Settle();

            // Registered, under the object's name, in the root namespace.
            CollectionAssert.AreEqual(new[] { robot }, robots.All);
            Assert.AreSame(robot, robots.Active);
            Assert.AreEqual("", robot.Namespace);

            // The profile is the robot's own description and nothing is wrong with it or the prefab.
            RobotProfile profile = robot.Profile;
            Assert.IsNotNull(profile, "The prefab's RobotInstance has a profile.");
            CollectionAssert.IsEmpty(profile.Validate(), "The profile validates.");
            CollectionAssert.IsEmpty(robot.Validate(), "Every joint and link the profile names exists under the prefab.");
            StringAssert.StartsWith(System.IO.Path.GetDirectoryName(prefabPath).Replace('\\', '/'), AssetDatabase.GetAssetPath(profile), "The profile ships next to the prefab.");

            // Every per-robot component is the instance's own.
            Assert.IsNotNull(robot.Controller);
            Assert.IsNotNull(robot.Interaction);
            Assert.IsNotNull(robot.TrajectoryPlayer);
            Assert.IsNotNull(robot.Ghosts);
            foreach (Component part in new Component[] { robot.Controller, robot.Interaction, robot.TrajectoryPlayer, robot.Ghosts })
                Assert.IsTrue(part.transform.IsChildOf(robot.transform), $"{part.GetType().Name} sits under another object.");
            Assert.AreSame(robot.transform, robot.Controller.Root);
            Assert.AreEqual(profile.endEffectorLink, robot.Controller.EndEffector.name);

            // It found the scene's router by itself: the binding is on and serves this robot.
            var binding = robot.GetComponentInChildren<RobotInteractionRouterBinding>();
            Assert.IsTrue(binding.enabled && binding.Dispatcher != null, "The robot bound itself to the interaction router.");
            Assert.AreSame(robot.Interaction, binding.Dispatcher.ControllerFor(robot.Interaction.Handle.gameObject));

            // Joints and roles from the profile; nothing is left out and nothing is unknown.
            CollectionAssert.AreEquivalent(profile.JointNames(JointRole.Arm), robot.Model.JointNames, "The IK chain is the profile's arm joints.");
            CollectionAssert.AreEquivalent(
                profile.JointNames(JointRole.Arm).Concat(profile.JointNames(JointRole.Gripper)), robot.Model.GetJointStateNames(),
                "The joint state names the arm and the gripper joints.");

            // It starts in the profile's initial pose, inside its limits.
            NamedPose initial = profile.InitialPose;
            Assert.IsNotNull(initial, "The profile names an initial pose.");
            foreach (JointValue value in initial.joints)
                Assert.AreEqual(value.value, Joint(robot, value.joint), 1e-4f, $"{value.joint} in the initial pose '{initial.name}'");
            AssertInsideLimits(robot, "at the start");

            // Its planning frame is its own anchor: the base stands at the origin of the frame
            // and looks along ROS x, which is what a ROS stack started without a pose believes.
            Transform frame = robot.PlanningFrame;
            Assert.IsNotNull(frame);
            Assert.IsTrue(frame.IsChildOf(robot.transform) && frame != robot.transform, "The planning frame is an anchor inside the prefab.");
            Assert.Less(Vector3.Distance(Vector3.one, frame.lossyScale), 1e-4f, "The planning frame is not scaled.");
            Transform root = robot.transform;
            Assert.Less(Vector3.Distance(frame.position, root.position), 1e-4f, "The planning frame is at the robot's root link.");
            Vector3 forward = frame.InverseTransformDirection(root.forward);   // an imported robot looks along its local z
            Assert.AreEqual(0f, Mathf.Atan2(forward.z, forward.x) * Mathf.Rad2Deg, 0.01f, "ROS yaw of the robot in its own planning frame");
            Assert.AreEqual(0f, Vector3.Angle(frame.up, root.up), 0.01f, "ROS z is up in both");

            // Handles: one per arm joint, owned by the robot.
            var handles = Object.FindObjectsByType<RobotOwned>(FindObjectsSortMode.None).Where(m => m.name.StartsWith("Joint IK Handle")).ToList();
            Assert.AreEqual(robot.Model.JointNames.Count, handles.Count);
            Assert.IsTrue(handles.All(h => robots.OwnerOf(h.gameObject) == robot));
            Assert.AreSame(robot, robots.OwnerOf(robot.Interaction.Handle.gameObject));
            Assert.Less(Vector3.Distance(robot.Interaction.Handle.position, robot.Controller.EndEffector.position), 1e-3f, "The handle sits on the end effector.");

            // Its ROS names are relative, so they land in its namespace.
            robot.Ros.Publish(robot.JointStatesTopic, new StringMsg("x"));
            Assert.AreEqual(1, bus.CountOn("/" + profile.jointStatesTopic));
            Assert.IsFalse(string.IsNullOrEmpty(robot.TrajectoryCommandTopic), "The profile names the trajectory command topic.");
            Assert.IsFalse(robot.TrajectoryCommandTopic.StartsWith("/"), "A robot's own names are relative.");

            // Ghosts come from the profile's ghost prefab, belong to the robot and are posed like it.
            robot.Ghosts.SpawnGoalGhost();
            GameObject ghost = robot.Ghosts.GoalGhost;
            Assert.IsNotNull(ghost, "The profile has a ghost prefab.");
            spawned.Add(ghost);
            Assert.AreSame(robot, robots.OwnerOf(ghost));
            Assert.Less(Vector3.Distance(robot.transform.position, ghost.transform.position), 1e-4f);
            Assert.IsEmpty(ghost.GetComponentsInChildren<Collider>().Where(c => c.enabled), "A ghost cannot be hit.");
            var ghostBodies = ghost.GetComponentsInChildren<ArticulationBody>().ToDictionary(b => b.name);
            foreach (ArticulationBody body in robot.Controller.Joints)
            {
                Assert.IsTrue(ghostBodies.TryGetValue(body.name, out ArticulationBody copy), $"The ghost has the link '{body.name}'.");
                Assert.AreEqual(body.jointPosition[0], copy.jointPosition[0], 1e-4f, $"ghost {body.name}");
            }
        }

        [UnityTest]
        public IEnumerator ItsBaseCanBePlacedElsewhere_AndItStaysThere_InTheSamePose([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            RobotInstance robot = Spawn(Load(prefabPath), "", "", Vector3.zero);
            yield return Settle();
            float[] joints = robot.Model.GetJointStatePositions();
            Transform tool = robot.Controller.EndEffector;
            Vector3 toolInBase = robot.transform.InverseTransformPoint(tool.position);
            var target = new Pose(new Vector3(1.5f, 0.4f, -2f), Quaternion.Euler(0f, 120f, 0f));

            // First where it would go: a translucent copy, posed like the robot, and nothing moves.
            Assert.IsTrue(robot.Ghosts.ShowPlacementGhost(target));
            GameObject ghost = robot.Ghosts.PlacementGhost;
            spawned.Add(ghost);
            yield return Settle();
            Assert.Less(Vector3.Distance(target.position, ghost.transform.position), 1e-3f, "The copy stands at the proposed base.");
            Assert.Less(Quaternion.Angle(target.rotation, ghost.transform.rotation), 0.1f);
            Transform ghostTool = ghost.GetComponentsInChildren<Transform>().First(t => t.name == tool.name);
            Assert.Less(Vector3.Distance(target.position + target.rotation * toolInBase, ghostTool.position), 5e-3f, "The copy is in the robot's joint pose.");
            Assert.Less(Vector3.Distance(Vector3.zero, robot.transform.position), 1e-4f);

            // Then the robot itself.
            Pose heardBefore = default;
            int heard = 0;
            robot.BaseMoved += (_, before) => { heard++; heardBefore = before; };
            Assert.IsTrue(robot.PlaceBase(target, out string reason), reason);
            yield return Settle();

            Assert.AreEqual(1, heard);
            Assert.Less(Vector3.Distance(Vector3.zero, heardBefore.position), 1e-4f);
            Assert.Less(Vector3.Distance(target.position, robot.transform.position), 1e-3f, "The base is where it was put, after physics has run.");
            Assert.Less(Quaternion.Angle(target.rotation, robot.transform.rotation), 0.1f);
            float[] after = robot.Model.GetJointStatePositions();
            for (int i = 0; i < joints.Length; i++) Assert.AreEqual(joints[i], after[i], 1e-3f, $"joint {i} kept its position");
            Assert.Less(Vector3.Distance(target.position + target.rotation * toolInBase, tool.position), 5e-3f, "The arm went with the base.");
            AssertInsideLimits(robot, "after the base was placed");
            // The planning frame is part of the robot: it is at the base again, so the ROS side needs nothing.
            Assert.Less(Vector3.Distance(robot.transform.position, robot.PlanningFrame.position), 1e-4f);
        }

        [UnityTest]
        public IEnumerator IK_ReachesPosesAFewCentimetresFromTheStart([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            RobotInstance robot = Spawn(Load(prefabPath), "", "", Vector3.zero);
            yield return Settle();

            DirectArticulationIKController ik = robot.Controller;
            Vector3 start = ik.EndEffector.position;
            float[] before = robot.Model.GetJointStatePositions();

            // Down, sideways and back towards the base: 4 cm each.
            Vector3 toBase = (robot.transform.position - start).normalized;
            foreach (Vector3 offset in new[] { Vector3.down * 0.04f, Vector3.Cross(Vector3.up, toBase).normalized * 0.04f, toBase * 0.04f })
            {
                Vector3 target = start + offset;
                ik.BeginInteraction();
                InteractionRefusal refusal = InteractionRefusal.None;
                for (int frame = 0; frame < 60; frame++)
                {
                    refusal = ik.TrySolveToTarget(target);
                    yield return null;
                    if (Vector3.Distance(ik.EndEffector.position, target) < 0.01f) break;
                }
                ik.EndInteraction();

                Assert.Less(Vector3.Distance(ik.EndEffector.position, target), 0.01f, $"IK did not reach {offset} from the start: {refusal.Reason}");
                AssertInsideLimits(robot, $"after IK to {offset}");
            }

            float[] after = robot.Model.GetJointStatePositions();
            Assert.Greater(before.Zip(after, (a, b) => Mathf.Abs(a - b)).Max(), 1e-3f, "IK moved the joints.");

            // The pose holds through physics steps: nothing pushes the arm away from where IK left it.
            Vector3 held = ik.EndEffector.position;
            for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
            Assert.Less(Vector3.Distance(held, ik.EndEffector.position), 1e-3f, "The arm stays where IK put it.");
        }

        [UnityTest]
        public IEnumerator TheGripper_OpensAndCloses_InsideItsLimits_WithItsFollowers([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            RobotInstance robot = Spawn(Load(prefabPath), "", "", Vector3.zero);
            yield return Settle();

            RobotProfile profile = robot.Profile;
            Assume.That(profile.gripper != null && profile.gripper.IsSet, "This robot has no gripper.");
            IRobotGripper gripper = robot.Gripper;
            Assert.IsNotNull(gripper, "The profile describes a gripper, so the robot has one.");
            Assert.AreEqual(profile.gripper.openWidthMetres, robot.Controller.GripperOpenWidth, 1e-6f);
            Assert.Greater(profile.gripper.openWidthMetres, 0.01f, "A gripper opens more than a centimetre.");

            var commands = new List<bool>();
            gripper.GripperCommanded += commands.Add;

            foreach (bool open in new[] { true, false, true })
            {
                if (open) robot.Controller.OpenGripper(); else robot.Controller.CloseGripper();
                float target = open ? profile.gripper.open : profile.gripper.closed;

                float deadline = Time.realtimeSinceStartup + 10f;
                while (Mathf.Abs(Joint(robot, profile.gripper.driverJoint) - target) > 1e-6f && Time.realtimeSinceStartup < deadline)
                {
                    AssertInsideLimits(robot, open ? "while opening" : "while closing");
                    AssertFollowers(robot, profile);
                    yield return null;
                }
                yield return new WaitForFixedUpdate();

                Assert.AreEqual(target, Joint(robot, profile.gripper.driverJoint), 1e-4f, $"The driver joint when {(open ? "open" : "closed")}.");
                Assert.AreEqual(open ? profile.gripper.openWidthMetres : 0f, robot.Controller.GripperWidth, 1e-5f);
                AssertInsideLimits(robot, open ? "open" : "closed");
                AssertFollowers(robot, profile);
            }
            CollectionAssert.AreEqual(new[] { true, false, true }, commands, "Each command is announced once, for the plugin that sends it to the real gripper.");

            // The joints move where the articulation is, not only in the controller's books.
            foreach (JointSpec joint in profile.joints.Where(j => j.role == JointRole.Gripper))
            {
                Assert.IsTrue(robot.Model.TryGetJointLink(joint.name, out Transform link), joint.name);
                Assert.AreEqual(Joint(robot, joint.name), link.GetComponent<ArticulationBody>().jointPosition[0], 1e-4f, joint.name);
            }

            // A joint state that names only the driver (what MoveIt sends) moves the followers too.
            float half = (profile.gripper.open + profile.gripper.closed) * 0.5f;
            robot.Model.ApplyJointState(new[] { profile.gripper.driverJoint }, new double[] { half });
            Assert.AreEqual(half, Joint(robot, profile.gripper.driverJoint), 1e-4f);
            AssertFollowers(robot, profile);
            AssertInsideLimits(robot, "half open");

            // The arm did not move with the gripper.
            NamedPose initial = profile.InitialPose;
            foreach (JointValue value in initial.joints.Where(v => profile.TryGetJoint(v.joint, out JointSpec j) && j.role == JointRole.Arm))
                Assert.AreEqual(value.value, Joint(robot, value.joint), 1e-4f, value.joint);
        }

        // Every follower stands where its mimic rule puts it for its leader's position (inside its own limits).
        private static void AssertFollowers(RobotInstance robot, RobotProfile profile)
        {
            foreach (JointSpec joint in profile.joints)
            {
                if (joint.role != JointRole.Gripper || !joint.IsMimic) continue;
                float expected = joint.mimic.Follow(Joint(robot, joint.mimic.leader));
                if (joint.HasLimits) expected = Mathf.Clamp(expected, joint.Lower, joint.Upper);
                Assert.AreEqual(expected, Joint(robot, joint.name), 1e-4f, $"{joint.name} follows {joint.mimic.leader}");
            }
        }

        [UnityTest]
        public IEnumerator ATrajectoryThroughTheNamedPoses_Replays_AndGivesThePoseBack([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            RobotInstance robot = Spawn(Load(prefabPath), "", "", Vector3.zero);
            yield return Settle();

            RobotProfile profile = robot.Profile;
            List<string> arm = profile.JointNames(JointRole.Arm);
            // The poses that speak about the arm, each as a full arm configuration.
            List<NamedPose> poses = profile.poses.Where(p => p.joints.Any(v => arm.Contains(v.joint))).ToList();
            Assert.GreaterOrEqual(poses.Count, 2, "The robot has at least two named arm poses to move between.");

            // As a plan does, the trajectory starts where the robot is and then visits each pose.
            const double SegmentSeconds = 0.2;
            var trajectory = new JointTrajectoryMsg { joint_names = arm.ToArray() };
            var waypoints = new List<double[]> { arm.Select(joint => (double)Joint(robot, joint)).ToArray() };
            foreach (NamedPose pose in poses)
                waypoints.Add(arm.Select(joint =>
                {
                    JointValue value = pose.joints.FirstOrDefault(v => v.joint == joint);
                    return value != null ? (double)value.value : 0.0;
                }).ToArray());
            trajectory.points = waypoints.Select((positions, i) =>
            {
                double at = i * SegmentSeconds;
                return new JointTrajectoryPointMsg { positions = positions, time_from_start = new DurationMsg { sec = (int)at, nanosec = (uint)((at - (int)at) * 1e9) } };
            }).ToArray();
            CollectionAssert.IsEmpty(robot.Model.Limits.Validate(trajectory, JointLimitTolerance.Default), "The named poses are inside the limits.");

            float[] before = robot.Model.GetJointStatePositions();
            string[] names = robot.Model.GetJointStateNames();
            int controlTaken = 0;
            robot.Model.ControlTaken += () => controlTaken++;
            var visited = new bool[poses.Count];

            // The player is past a waypoint by at most one frame's worth of the next segment
            // when a frame ends, so that is how close the robot has to have been to each pose.
            float largestStep = 0f;
            for (int i = 1; i < waypoints.Count; i++)
                largestStep = Mathf.Max(largestStep, arm.Select((joint, j) => Mathf.Abs((float)(waypoints[i][j] - waypoints[i - 1][j]))).Max());
            void Look()
            {
                float tolerance = 2e-3f + largestStep * Time.deltaTime / (float)SegmentSeconds;
                for (int i = 0; i < poses.Count; i++)
                    if (arm.Select((joint, j) => Mathf.Abs(Joint(robot, joint) - (float)waypoints[i + 1][j])).Max() < tolerance) visited[i] = true;
            }

            robot.TrajectoryPlayer.StartReplay(trajectory);
            float deadline = Time.realtimeSinceStartup + 20f;
            while (!robot.TrajectoryPlayer.HasFinishedOneLoop() && Time.realtimeSinceStartup < deadline)
            {
                Look();
                AssertInsideLimits(robot, "during the replay");
                yield return null;
            }
            Look();

            Assert.IsTrue(robot.TrajectoryPlayer.HasFinishedOneLoop(), "The replay went through the trajectory.");
            Assert.GreaterOrEqual(controlTaken, 1, "A replay takes the robot's pose, so a live mirror lets go.");
            for (int i = 0; i < poses.Count; i++)
                Assert.IsTrue(visited[i], $"The replay passed through the pose '{poses[i].name}'.");

            robot.TrajectoryPlayer.StopReplay();
            yield return null;
            yield return null;
            yield return new WaitForFixedUpdate();
            float[] after = robot.Model.GetJointStatePositions();
            for (int i = 0; i < names.Length; i++)
                Assert.AreEqual(before[i], after[i], 1e-4f, $"{names[i]} after the replay stopped");
        }

        // Nothing may assume one or two: the same prefab three times is three independent robots.
        [UnityTest]
        public IEnumerator ThreeInstances_InThreeNamespaces_Coexist([ValueSource(typeof(ShippedRobots), nameof(ShippedRobots.PrefabPaths))] string prefabPath)
        {
            GameObject prefab = Load(prefabPath);
            var instances = new List<RobotInstance>();
            for (int i = 0; i < FakeRobots.ThreeIds.Length; i++)
                instances.Add(Spawn(prefab, FakeRobots.ThreeIds[i], FakeRobots.ThreeIds[i], new Vector3((i - 1) * 1.5f, 0f, 0f)));
            yield return Settle();

            CollectionAssert.AreEqual(instances, robots.All);
            CollectionAssert.AreEqual(FakeRobots.ThreeIds, robots.All.Select(r => r.Id));
            CollectionAssert.AreEqual(FakeRobots.ThreeIds, robots.All.Select(r => r.Namespace));
            Assert.AreEqual(3, robots.All.Select(r => r.Controller).Distinct().Count());
            Assert.AreEqual(3, robots.All.Select(r => r.Interaction).Distinct().Count());
            Assert.AreEqual(3, robots.All.Select(r => r.Ros).Distinct().Count(), "Each robot has its own namespaced bus.");
            Assert.AreEqual(3, robots.All.Select(r => r.PlanningFrame).Distinct().Count(), "Each robot has its own planning frame.");
            Assert.AreEqual(1, Object.FindObjectsByType<RobotIntentDispatcher>(FindObjectsSortMode.None).Length, "One dispatcher serves all three.");

            // One set of joint handles per robot, each owned by its robot.
            var handles = Object.FindObjectsByType<RobotOwned>(FindObjectsSortMode.None).Where(m => m.name.StartsWith("Joint IK Handle")).ToList();
            int perRobot = instances[0].Model.JointNames.Count;
            Assert.AreEqual(3 * perRobot, handles.Count);
            foreach (RobotInstance robot in instances)
                Assert.AreEqual(perRobot, handles.Count(h => robots.OwnerOf(h.gameObject) == robot), robot.Id);

            // Each robot talks in its own namespace.
            foreach (RobotInstance robot in instances)
            {
                robot.Ros.Publish(robot.JointStatesTopic, new StringMsg(robot.Id));
                robot.Ros.Publish(robot.TrajectoryCommandTopic, new StringMsg(robot.Id));
            }
            foreach (RobotInstance robot in instances)
            {
                Assert.AreEqual(1, bus.CountOn($"/{robot.Id}/{robot.Profile.jointStatesTopic}"));
                Assert.AreEqual(1, bus.CountOn($"/{robot.Id}/{robot.Profile.trajectoryCommandTopic}"));
            }

            // Moving the third robot (arm and gripper) leaves the other two where they are.
            RobotInstance third = instances[2];
            RobotProfile profile = third.Profile;
            string armJoint = profile.JointNames(JointRole.Arm)[1];
            float[] firstBefore = instances[0].Model.GetJointStatePositions();
            float[] secondBefore = instances[1].Model.GetJointStatePositions();
            float moved = Joint(third, armJoint) + 0.2f;
            third.Model.ApplyJointState(new[] { armJoint }, new double[] { moved });
            if (third.Gripper != null) third.Gripper.ToggleGripper();
            for (int i = 0; i < 10; i++) yield return null;

            Assert.AreEqual(moved, Joint(third, armJoint), 1e-4f);
            CollectionAssert.AreEqual(firstBefore, instances[0].Model.GetJointStatePositions());
            CollectionAssert.AreEqual(secondBefore, instances[1].Model.GetJointStatePositions());
            foreach (RobotInstance robot in instances) AssertInsideLimits(robot, "with three robots");
        }
    }
}
#endif
