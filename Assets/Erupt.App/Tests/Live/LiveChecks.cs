using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Obstacles;
using Erupt.Plugins;
using Erupt.Plugins.MoveIt;
using Erupt.Robot;
using Erupt.UiBindings;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// The checks the single-arm and the three-arm fixtures share. Each one drives the app the
    /// way the UI does and asserts on what the ROS side reports back.
    /// </summary>
    public abstract partial class LiveSceneFixture
    {
        protected const float PositionTolerance = 1e-3f;      // metres
        protected const float RotationToleranceDegrees = 0.1f;
        protected const float JointTolerance = 1e-3f;         // radians / metres

        // ---- connection ----------------------------------------------------------------------------

        protected static IEnumerator AssertConnected()
        {
            Assert.IsNotNull(Connection, "The app created its ROS connection.");
            Assert.IsTrue(Connection.HasConnectionThread, "The connection thread is running.");
            // A message that made the round trip is the proof; the error flag alone starts out clear.
            using (var states = new LiveRos.TopicCounter<JointStateMsg>(Robots[0].Ros, Robots[0].JointStatesTopic))
                yield return LiveRos.Until(() => states.Count > 0, 20f, $"a joint state of '{Robots[0].Id}' through the endpoint");
            Assert.IsFalse(Connection.HasConnectionError, "The connection reports no error.");
        }

        protected static void AssertNoExceptions() =>
            Assert.IsEmpty(Exceptions, "The app logged exceptions:\n" + Join(Exceptions));

        /// <summary>Errors the headless XR rig and the editor log on their own; nothing of ERUPT's.</summary>
        protected static bool IsHeadlessNoise(string error) =>
            error.Contains("Hand Tracking Subsystem") || error.Contains("SampleRateHz") || error.Contains("OVR")
            || error.Contains("XR ") || error.Contains("OpenXR") || error.Contains("Oculus") || error.Contains("Meta ");

        protected static void AssertNoErrorsFromErupt()
        {
            List<string> ours = Errors.Where(e => !IsHeadlessNoise(e)).ToList();
            Assert.IsEmpty(ours, "The app logged errors:\n" + Join(ours));
        }

        /// <summary>
        /// The planning-scene mirror did not have to guess: no change from a planning scene was
        /// taken for a conflict (an echo that was not recognised), and no pose came in a frame
        /// the app could not place.
        /// </summary>
        protected static void AssertNoPlanningSceneComplaints()
        {
            List<string> complaints = Warnings.Where(w => w.StartsWith("[CO Listener]") &&
                (w.Contains("reports a change to") || w.Contains("reports poses in frame"))).ToList();
            Assert.IsEmpty(complaints, "The planning-scene mirror complained:\n" + Join(complaints));
        }

        // ---- joint states ----------------------------------------------------------------------------

        /// <summary>The last joint state a robot's ROS side published, by ROS joint name.</summary>
        protected static IEnumerator RosJoints(RobotInstance robot, Action<Dictionary<string, double>> got)
        {
            using var states = new LiveRos.TopicCounter<JointStateMsg>(robot.Ros, robot.JointStatesTopic);
            yield return LiveRos.Until(() => states.Count > 0, 10f, $"a joint state of '{robot.Id}'");
            JointStateMsg msg = states.Messages[^1];
            var result = new Dictionary<string, double>();
            for (int i = 0; i < msg.name.Length; i++) result[msg.name[i]] = msg.position[i];
            got(result);
        }

        /// <summary>Bring the Unity robot to where its ROS robot is, as the "Mirror joints" toggle does, and leave the toggle as it was.</summary>
        protected static IEnumerator SyncFromRos(RobotInstance robot)
        {
            MoveItPlanningClient client = MoveIt.SessionFor(robot).Client;
            bool was = client.IsMirroring;
            client.SetMirroring(true);
            using (var states = new LiveRos.TopicCounter<JointStateMsg>(robot.Ros, robot.JointStatesTopic))
                yield return LiveRos.Until(() => states.Count >= 2, 10f, $"joint states of '{robot.Id}'");
            yield return null;
            client.SetMirroring(was);
        }

        protected static IEnumerator AssertJointStatesMirrored(RobotInstance robot)
        {
            MoveItRobotSession session = MoveIt.SessionFor(robot);
            Assert.IsNotNull(session, $"MoveIt serves robot '{robot.Id}'.");

            Dictionary<string, double> ros = null;
            yield return RosJoints(robot, r => ros = r);

            // Names: every joint ROS reports is one Unity has (no unknown joint), under the name
            // rule both ways. A joint Unity has and ROS does not report has to be a follower of
            // one it does report: joint_states carries the driven joints, and a gripper's mimic
            // joints are worked out from their leader on both sides.
            string[] unityNames = robot.Model.GetJointStateNames();
            string[] reported = robot.Names.ToLocal(ros.Keys.ToArray());
            CollectionAssert.IsSubsetOf(reported, unityNames, $"'{robot.Id}': ROS reports joints the Unity robot does not have.");
            CollectionAssert.AreEquivalent(ros.Keys, robot.Names.ToRos(reported), $"'{robot.Id}': the joint name rule is not the same both ways.");
            var followers = new List<JointSpec>();
            foreach (string joint in unityNames.Except(reported))
            {
                JointSpec spec = null;
                Assert.IsTrue(robot.Profile != null && robot.Profile.TryGetJoint(joint, out spec) && spec.IsMimic && reported.Contains(spec.mimic.leader),
                    $"'{robot.Id}': the Unity robot has the joint '{joint}', which ROS does not report and which follows no joint ROS reports " +
                    $"(ROS reports: {string.Join(", ", ros.Keys)}).");
                followers.Add(spec);
            }

            int refusalsBefore = Refusals.Count;
            bool was = session.Client.IsMirroring;
            session.Client.SetMirroring(true);
            yield return LiveRos.Seconds(1f);
            yield return RosJoints(robot, r => ros = r);
            yield return null;

            Dictionary<string, float> unity = UnityJoints(robot);
            foreach (KeyValuePair<string, double> joint in ros)
                Assert.AreEqual(joint.Value, unity[joint.Key], JointTolerance,
                    $"'{robot.Id}' joint {joint.Key}: Unity shows {unity[joint.Key]:F4}, ROS reports {joint.Value:F4}.");

            // The followers stand where their leader puts them.
            foreach (JointSpec follower in followers)
            {
                float expected = follower.mimic.Follow(unity[robot.Names.ToRos(follower.mimic.leader)]);
                if (follower.HasLimits) expected = Mathf.Clamp(expected, follower.Lower, follower.Upper);
                Assert.AreEqual(expected, unity[robot.Names.ToRos(follower.name)], JointTolerance,
                    $"'{robot.Id}' joint {follower.name} follows {follower.mimic.leader}.");
            }

            List<InteractionRefusal> mismatches = Refusals.Skip(refusalsBefore).Where(r => r.Code == RefusalCode.LimitMismatch).ToList();
            Assert.IsEmpty(mismatches, "A mirrored joint state was clamped: " + string.Join("; ", mismatches.Select(m => m.Reason)));
            session.Client.SetMirroring(was);
        }

        /// <summary>
        /// The robot stands in its planning frame where its ROS side has it: the root link of
        /// MoveIt's robot model, seen from the planning frame, is at the same pose on both sides.
        /// On the ROS side that comes from the robot's own TF; in Unity from the planning frame
        /// the robot uses (its prefab's anchor, or a frame the scene shares between robots).
        /// </summary>
        protected static IEnumerator AssertBasePoseAgrees(RobotInstance robot)
        {
            PlanningSceneMsg scene = null;
            yield return LiveRos.PlanningScene(robot, s => scene = s);
            Assert.IsNotNull(scene, $"The planning scene of '{robot.Id}' answers.");

            // ROS: where the planning frame is, seen from MoveIt's own frame (the model's root
            // link); inverted, that link in the planning frame.
            string modelFrame = LiveRos.ModelFrame(scene);
            LiveRos.FrameInModel(scene, robot.PlanningFrameId, out Vector3 frameInModel, out Quaternion frameInModelRotation);
            LiveRos.Invert(frameInModel, frameInModelRotation, out Vector3 rosRoot, out Quaternion rosRootRotation);

            // Unity: the same link of the robot in the scene, in its planning frame.
            Assert.IsTrue(robot.Model.TryFindLinkTransform(modelFrame, out Transform rootLink),
                $"'{robot.Id}': MoveIt's robot model starts at '{modelFrame}', which the Unity robot does not have.");
            LiveRos.ExpectedRosPose(robot.PlanningFrame, rootLink.position, AttachedObjectPlacement.WorldConventionRotation(rootLink),
                out Vector3 unityRoot, out Quaternion unityRootRotation);

            Assert.Less((unityRoot - rosRoot).magnitude, 1e-3f,
                $"'{robot.Id}': Unity has '{modelFrame}' at {unityRoot:F4} in '{robot.PlanningFrameId}', ROS at {rosRoot:F4}.");
            Assert.Less(Quaternion.Angle(unityRootRotation, rosRootRotation), 0.05f,
                $"'{robot.Id}': Unity and ROS disagree on how '{modelFrame}' is turned in '{robot.PlanningFrameId}'.");
            Debug.Log($"[live] '{robot.Id}': '{modelFrame}' at {rosRoot:F4} in '{robot.PlanningFrameId}' on both sides.");
        }

        protected static IEnumerator AssertPlannersDiscovered(RobotInstance robot)
        {
            MoveItPlanningClient client = MoveIt.SessionFor(robot).Client;
            yield return LiveRos.Until(() => client.Planners.Count > 0, 20f, $"planner discovery for '{robot.Id}'");
            PlannerListing pipeline = client.Planners.FirstOrDefault(p => p.PipelineId == client.Settings.planningPipelineId);
            Assert.IsNotNull(pipeline, $"MoveIt of '{robot.Id}' offers the pipeline '{client.Settings.planningPipelineId}' " +
                                       $"(it offers: {string.Join(", ", client.Planners.Select(p => p.PipelineId))}).");
            Assert.IsNotEmpty(pipeline.PlannerIds, "The pipeline lists its planners.");
            Debug.Log($"[live] '{robot.Id}' planners in '{pipeline.PipelineId}': {string.Join(", ", pipeline.PlannerIds)}");
        }

        // ---- planning scene, Unity → ROS ---------------------------------------------------------------

        protected static IEnumerator AssertEveryPublisherIsInPlanningScene(RobotInstance robot)
        {
            yield return LiveRos.Seconds(2.5f);   // the first publish of every object (1 Hz) has gone out
            int checkedObjects = 0;
            foreach (CollisionObjectPublisher publisher in CollisionObjectPublisher.Live.ToArray())
            {
                if (publisher == null || !publisher.isActiveAndEnabled || publisher.pausePublishing) continue;
                yield return AssertInPlanningScene(robot, publisher.gameObject, publisher.objectId);
                checkedObjects++;
            }
            Assert.Greater(checkedObjects, 0, "The scene publishes obstacles.");
            Debug.Log($"[live] {checkedObjects} Unity obstacle(s) verified in the planning scene of '{robot.Id}'.");
        }

        /// <summary>
        /// Every world object of a robot's planning scene that Unity does not own (the ROS side's
        /// demo scene, something another node added) stands in Unity where MoveIt has it. Passes
        /// with nothing to check when the planning scene holds only Unity's objects.
        /// </summary>
        protected static IEnumerator AssertRosObjectsAreMirrored(RobotInstance robot)
        {
            CollisionObjectsListenerSimple mirror = MoveIt.PlanningScene.Mirror;
            PlanningSceneMsg scene = null;
            yield return LiveRos.PlanningScene(robot, s => scene = s);
            int checkedObjects = 0;
            foreach (CollisionObjectMsg co in scene.world.collision_objects)
            {
                if (mirror.IsUnityOwned(co.id)) continue;
                Assert.IsTrue(mirror.TryGetObject(co.id, out GameObject go), $"'{co.id}' of the planning scene of '{robot.Id}' is not in Unity.");
                LiveRos.PoseIn(scene, robot.PlanningFrameId, co, out Vector3 position, out Quaternion rotation);
                ExpectedUnityPose(robot, position, rotation, out Vector3 unityPosition, out Quaternion unityRotation);
                Assert.Less((go.transform.position - unityPosition).magnitude, PositionTolerance,
                    $"'{co.id}' (from the ROS side of '{robot.Id}') is at {go.transform.position:F4} in Unity, MoveIt has it at {unityPosition:F4}.");
                Assert.Less(Quaternion.Angle(go.transform.rotation, unityRotation), RotationToleranceDegrees, $"'{co.id}': orientation in Unity.");
                checkedObjects++;
            }
            Debug.Log($"[live] {checkedObjects} object(s) of the ROS side of '{robot.Id}' verified in Unity.");
        }

        /// <summary>
        /// A Unity object is in a robot's MoveIt planning scene under <paramref name="id"/>, at the
        /// pose its Unity transform has in the robot's planning frame, with its size.
        /// </summary>
        protected static IEnumerator AssertInPlanningScene(RobotInstance robot, GameObject unityObject, string id, float timeoutSeconds = 12f)
        {
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            PlanningSceneMsg scene = null;
            CollisionObjectMsg co = null;
            string problem = null;
            while (true)
            {
                yield return LiveRos.PlanningScene(robot, s => scene = s);
                co = LiveRos.Find(scene, id);
                problem = co == null ? "it is not there" : PoseProblem(robot, scene, co, unityObject.transform) ?? GeometryProblem(robot, scene, co, unityObject);
                if (problem == null) break;
                if (Time.realtimeSinceStartup > end)
                    Assert.Fail($"Unity object '{unityObject.name}' (id '{id}') in the planning scene of '{robot.Id}': {problem}. MoveIt holds {LiveRos.Describe(co)}.");
                yield return LiveRos.Seconds(0.25f);
            }
        }

        private static string PoseProblem(RobotInstance robot, PlanningSceneMsg scene, CollisionObjectMsg co, Transform unity)
        {
            LiveRos.ExpectedRosPose(robot.PlanningFrame, unity.position, unity.rotation, out Vector3 wantPosition, out Quaternion wantRotation);
            LiveRos.PoseIn(scene, robot.PlanningFrameId, co, out Vector3 position, out Quaternion rotation);
            float distance = (position - wantPosition).magnitude;
            float angle = Quaternion.Angle(rotation, wantRotation);
            if (distance > PositionTolerance)
                return $"position in '{robot.PlanningFrameId}' is {position:F4}, Unity has it at {wantPosition:F4} ({distance * 1000f:F1} mm off)";
            if (angle > RotationToleranceDegrees)
                return $"orientation in '{robot.PlanningFrameId}' is {rotation:F4}, Unity has {wantRotation:F4} ({angle:F2} deg off)";
            return null;
        }

        private static string GeometryProblem(RobotInstance robot, PlanningSceneMsg scene, CollisionObjectMsg co, GameObject unityObject)
        {
            var filter = unityObject.GetComponent<MeshFilter>();
            int primitives = co.primitives?.Length ?? 0, meshes = co.meshes?.Length ?? 0;
            if (primitives + meshes == 0) return "it has no geometry";
            if (filter == null || filter.sharedMesh == null) return null;   // a compound: only its presence and pose are checked
            Mesh mesh = filter.sharedMesh;
            Vector3 size = Vector3.Scale(mesh.bounds.size, unityObject.transform.lossyScale);

            if (primitives == 1 && meshes == 0)
            {
                SolidPrimitiveMsg primitive = co.primitives[0];
                double[] want = primitive.type switch
                {
                    SolidPrimitiveMsg.BOX => new double[] { size.x, size.z, size.y },
                    SolidPrimitiveMsg.SPHERE => new double[] { Mathf.Max(size.x, size.y, size.z) * 0.5f },
                    SolidPrimitiveMsg.CYLINDER => new double[] { size.y, Mathf.Max(size.x, size.z) * 0.5f },
                    _ => null
                };
                if (want == null) return $"unexpected primitive type {primitive.type}";
                if (primitive.dimensions.Length != want.Length) return $"primitive has {primitive.dimensions.Length} dimensions, expected {want.Length}";
                for (int i = 0; i < want.Length; i++)
                    if (Math.Abs(primitive.dimensions[i] - want[i]) > PositionTolerance)
                        return $"dimensions are [{string.Join(", ", primitive.dimensions.Select(d => d.ToString("F4")))}], Unity's size gives [{string.Join(", ", want.Select(d => d.ToString("F4")))}]";
                return null;
            }

            if (meshes == 1 && primitives == 0 && mesh.isReadable)
            {
                // Every (sampled) Unity vertex, expressed in the planning frame, is a vertex of the
                // mesh MoveIt holds: checks scale, axis mapping and orientation in one go.
                LiveRos.PoseIn(scene, robot.PlanningFrameId, co, out Vector3 objectPosition, out Quaternion objectRotation);
                PoseMsg meshPose = co.mesh_poses != null && co.mesh_poses.Length > 0 ? co.mesh_poses[0] : null;
                if (meshPose != null)
                    LiveRos.Compose(objectPosition, objectRotation, LiveRos.Vec(meshPose.position), LiveRos.Quat(meshPose.orientation), out objectPosition, out objectRotation);
                PointMsg[] rosVertices = co.meshes[0].vertices;
                Vector3[] vertices = mesh.vertices;
                int step = Mathf.Max(1, vertices.Length / 40);
                for (int i = 0; i < vertices.Length; i += step)
                {
                    LiveRos.ExpectedRosPose(robot.PlanningFrame, unityObject.transform.TransformPoint(vertices[i]), Quaternion.identity, out Vector3 want, out _);
                    float best = float.MaxValue;
                    foreach (PointMsg v in rosVertices)
                        best = Mathf.Min(best, (objectPosition + LiveRos.Rotate(objectRotation, LiveRos.Vec(v)) - want).magnitude);
                    if (best > 2e-3f) return $"mesh vertex {i} of Unity (at {want:F4} in '{robot.PlanningFrameId}') is {best * 1000f:F1} mm from the nearest vertex MoveIt holds";
                }
            }
            return null;
        }

        protected static IEnumerator AssertNotInPlanningScene(RobotInstance robot, string id, float timeoutSeconds = 10f) =>
            LiveRos.UntilWorldObject(robot, id, co => co == null, timeoutSeconds, $"'{id}' to leave the planning scene of '{robot.Id}'");

        protected static ScenePlacementTab SceneTab()
        {
            var tab = UnityEngine.Object.FindFirstObjectByType<ScenePlacementTab>();
            Assert.IsNotNull(tab, "The scene has the Scene tab.");
            return tab;
        }

        /// <summary>Add a shape with the Scene tab's button and put it somewhere, as a grab would.</summary>
        protected static GameObject AddShape(PrimitiveType type, Transform frame, Vector3 rosPosition, Quaternion unityRotationInFrame, Vector3? scale = null)
        {
            GameObject shape = SceneTab().Add(type);
            Assert.IsNotNull(shape, $"The Scene tab added a {type}.");
            shape.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(rosPosition.x, rosPosition.z, rosPosition.y)), frame.rotation * unityRotationInFrame);
            if (scale.HasValue) shape.transform.localScale = scale.Value;
            Spawned.Add(shape);
            return shape;
        }

        protected static string IdOf(GameObject obstacle)
        {
            var env = obstacle.GetComponent<EnvironmentObject>();
            Assert.IsNotNull(env, $"'{obstacle.name}' is an environment object.");
            return env.Id;
        }

        /// <summary>Delete an obstacle with its tier 2 verb.</summary>
        protected static void DeleteWithVerb(GameObject obstacle)
        {
            ISelectable selectable = obstacle.GetComponent<ISelectable>();
            Assert.IsNotNull(selectable, $"'{obstacle.name}' is selectable.");
            InteractionRefusal refusal = Press(selectable, "delete");
            Assert.IsFalse(refusal.IsRefused, refusal.Reason);
        }

        protected static IEnumerator AssertObstacleLifecycle(IReadOnlyList<RobotInstance> robots)
        {
            Transform frame = robots[0].PlanningFrame;

            // A cube, turned about all three axes.
            GameObject cube = AddShape(PrimitiveType.Cube, frame, new Vector3(0.55f, 0.45f, 0.35f), Quaternion.Euler(20f, 35f, -15f));
            string cubeId = IdOf(cube);
            foreach (RobotInstance robot in robots) yield return AssertInPlanningScene(robot, cube, cubeId);

            // Moved and turned.
            cube.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(0.6f, 0.3f, 0.5f)), frame.rotation * Quaternion.Euler(-40f, 110f, 60f));
            foreach (RobotInstance robot in robots) yield return AssertInPlanningScene(robot, cube, cubeId);

            // Resized, differently on every axis.
            cube.transform.localScale = new Vector3(0.12f, 0.2f, 0.31f);
            foreach (RobotInstance robot in robots) yield return AssertInPlanningScene(robot, cube, cubeId);

            // A sphere and a cylinder: radius, height and the cylinder's axis.
            GameObject sphere = AddShape(PrimitiveType.Sphere, frame, new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, Vector3.one * 0.24f);
            GameObject cylinder = AddShape(PrimitiveType.Cylinder, frame, new Vector3(0.3f, 0.8f, 0.4f), Quaternion.Euler(90f, 20f, 0f), new Vector3(0.1f, 0.2f, 0.1f));   // on its side: a 90° pitch
            string sphereId = IdOf(sphere), cylinderId = IdOf(cylinder);
            foreach (RobotInstance robot in robots)
            {
                yield return AssertInPlanningScene(robot, sphere, sphereId);
                yield return AssertInPlanningScene(robot, cylinder, cylinderId);
            }

            // Deleted with the verb; undone; redone.
            DeleteWithVerb(cube);
            foreach (RobotInstance robot in robots) yield return AssertNotInPlanningScene(robot, cubeId);

            Assert.IsTrue(Context.Undo.Undo(), "The delete can be undone.");
            yield return null;
            Assert.IsTrue(Context.Environment.TryGet(cubeId, out GameObject restored), "Undo restores the cube under its id.");
            foreach (RobotInstance robot in robots) yield return AssertInPlanningScene(robot, restored, cubeId);

            Assert.IsTrue(Context.Undo.Redo(), "The delete can be redone.");
            foreach (RobotInstance robot in robots) yield return AssertNotInPlanningScene(robot, cubeId);

            DeleteWithVerb(sphere);
            DeleteWithVerb(cylinder);
            foreach (RobotInstance robot in robots)
            {
                yield return AssertNotInPlanningScene(robot, sphereId);
                yield return AssertNotInPlanningScene(robot, cylinderId);
            }
            AssertNoExceptions();
        }

        /// <summary>With nothing moving, nothing is said: no echo loop between Unity, MoveIt and the watcher.</summary>
        protected static IEnumerator AssertQuietAtRest(float seconds)
        {
            var outbound = new List<LiveRos.TopicCounter<CollisionObjectMsg>>();
            var inbound = new List<LiveRos.TopicCounter<CollisionObjectMsg>>();
            foreach (RobotInstance robot in Robots)
            {
                outbound.Add(new LiveRos.TopicCounter<CollisionObjectMsg>(robot.Ros, CollisionObjectPublisher.Topic));
                inbound.Add(new LiveRos.TopicCounter<CollisionObjectMsg>(robot.Ros, MoveIt.PlanningScene.Mirror.topic));
            }
            try
            {
                yield return LiveRos.Seconds(3f);   // whatever was in flight has landed
                foreach (var counter in outbound) counter.Clear();
                foreach (var counter in inbound) counter.Clear();
                yield return LiveRos.Seconds(seconds);
                for (int i = 0; i < Robots.Count; i++)
                {
                    Assert.AreEqual(0, outbound[i].Count, $"'{Robots[i].Id}': {outbound[i].Count} message(s) on collision_object in {seconds:0} s at rest: " +
                                                          string.Join(", ", outbound[i].Messages.Select(m => $"{m.id}/op{m.operation}")));
                    Assert.AreEqual(0, inbound[i].Count, $"'{Robots[i].Id}': {inbound[i].Count} message(s) on collision_objects_ros in {seconds:0} s at rest: " +
                                                         string.Join(", ", inbound[i].Messages.Select(m => $"{m.id}/op{m.operation}")));
                }
            }
            finally
            {
                foreach (var counter in outbound) counter.Dispose();
                foreach (var counter in inbound) counter.Dispose();
            }
        }

        // ---- planning scene, ROS → Unity ---------------------------------------------------------------

        /// <summary>Change a robot's planning scene from the ROS side (not through the app's own publisher).</summary>
        protected static IEnumerator ApplyOnRosSide(RobotInstance robot, CollisionObjectMsg co, bool mustSucceed = true)
        {
            var scene = new PlanningSceneMsg { is_diff = true };
            scene.robot_state.is_diff = true;
            scene.world.collision_objects = new[] { co };
            bool ok = false;
            yield return LiveRos.Call<ApplyPlanningSceneRequest, ApplyPlanningSceneResponse>(robot.Ros, "apply_planning_scene",
                new ApplyPlanningSceneRequest(scene), r => ok = r != null && r.success);
            if (mustSucceed) Assert.IsTrue(ok, $"apply_planning_scene of '{robot.Id}' accepted the change to '{co.id}'.");
        }

        protected static CollisionObjectMsg RosBox(RobotInstance robot, string id, sbyte operation, Vector3 position, Quaternion rotation, Vector3? size = null)
        {
            var co = new CollisionObjectMsg
            {
                id = id,
                header = new HeaderMsg { frame_id = robot.PlanningFrameId },
                operation = operation,
                pose = new PoseMsg(new PointMsg(position.x, position.y, position.z), new QuaternionMsg(rotation.x, rotation.y, rotation.z, rotation.w))
            };
            if (size.HasValue)
            {
                co.primitives = new[] { new SolidPrimitiveMsg { type = SolidPrimitiveMsg.BOX, dimensions = new double[] { size.Value.x, size.Value.y, size.Value.z } } };
                co.primitive_poses = new[] { new PoseMsg(new PointMsg(0, 0, 0), new QuaternionMsg(0, 0, 0, 1)) };
            }
            return co;
        }

        /// <summary>Where a pose given in a robot's planning frame (ROS axes) is in Unity, computed independently of the app.</summary>
        protected static void ExpectedUnityPose(RobotInstance robot, Vector3 rosPosition, Quaternion rosRotation, out Vector3 position, out Quaternion rotation)
        {
            Transform frame = robot.PlanningFrame;
            position = frame.TransformPoint(new Vector3(rosPosition.x, rosPosition.z, rosPosition.y));
            rotation = frame.rotation * new Quaternion(-rosRotation.x, -rosRotation.z, -rosRotation.y, rosRotation.w);
        }

        private static IEnumerator UntilUnityPose(GameObject go, RobotInstance robot, Vector3 rosPosition, Quaternion rosRotation, string what)
        {
            ExpectedUnityPose(robot, rosPosition, rosRotation, out Vector3 position, out Quaternion rotation);
            bool timedOut = false;
            yield return LiveRos.Until(() => go != null && (go.transform.position - position).magnitude <= PositionTolerance
                                             && Quaternion.Angle(go.transform.rotation, rotation) <= RotationToleranceDegrees, 10f, t => timedOut = t);
            if (timedOut)
                Assert.Fail($"{what}: Unity has it at {(go != null ? go.transform.position.ToString("F4") : "(destroyed)")} / " +
                            $"{(go != null ? go.transform.rotation.ToString("F4") : "")}, expected {position:F4} / {rotation:F4} " +
                            $"({(go != null ? (go.transform.position - position).magnitude * 1000f : 0f):F1} mm, {(go != null ? Quaternion.Angle(go.transform.rotation, rotation) : 0f):F2} deg off).");
        }

        /// <summary>
        /// An object that appears in one robot's planning scene from the ROS side shows up in
        /// Unity at the right place, follows a move, and goes when it is removed. With several
        /// robots it also reaches the other planning scenes, and nothing is sent back to where it
        /// came from.
        /// </summary>
        protected static IEnumerator AssertRosObjectIsMirrored(RobotInstance source, IReadOnlyList<RobotInstance> others)
        {
            const string id = "live_ros_box";
            CollisionObjectsListenerSimple mirror = MoveIt.PlanningScene.Mirror;
            Assert.IsNotNull(mirror, "The scene mirrors the planning scenes into Unity.");

            // Left over from an earlier run (MoveIt answers false when there is nothing to remove).
            yield return ApplyOnRosSide(source, RosBox(source, id, CollisionObjectMsg.REMOVE, Vector3.zero, Quaternion.identity), mustSucceed: false);
            yield return LiveRos.Seconds(1f);

            var size = new Vector3(0.10f, 0.20f, 0.30f);
            var position = new Vector3(0.45f, -0.55f, 0.30f);
            Quaternion rotation = new Quaternion(0.18f, -0.31f, 0.42f, 0.83f).normalized;

            using var backToSource = new LiveRos.TopicCounter<CollisionObjectMsg>(source.Ros, CollisionObjectPublisher.Topic);
            yield return ApplyOnRosSide(source, RosBox(source, id, CollisionObjectMsg.ADD, position, rotation, size));

            GameObject go = null;
            yield return LiveRos.Until(() => mirror.TryGetObject(id, out go), 10f, $"'{id}' (added on the ROS side of '{source.Id}') to appear in Unity");
            yield return UntilUnityPose(go, source, position, rotation, $"'{id}' added on the ROS side of '{source.Id}'");

            // Its size: the box Unity built spans the same extents along the same axes.
            MeshFilter built = go.GetComponentInChildren<MeshFilter>();
            Assert.IsNotNull(built, "The mirrored object has a visible shape.");
            Vector3 unitySize = Vector3.Scale(built.sharedMesh.bounds.size, built.transform.lossyScale);
            Assert.AreEqual(size.x, unitySize.x, PositionTolerance, "box x");
            Assert.AreEqual(size.y, unitySize.z, PositionTolerance, "box y (Unity z)");
            Assert.AreEqual(size.z, unitySize.y, PositionTolerance, "box z (Unity y)");
            Assert.LessOrEqual(Quaternion.Angle(built.transform.rotation, go.transform.rotation), RotationToleranceDegrees, "The shape sits in the object's frame.");

            // The other robots' planning scenes get it, at the same place in the world.
            foreach (RobotInstance other in others) yield return AssertInPlanningScene(other, go, id);

            // Moved on the ROS side.
            var position2 = new Vector3(0.30f, -0.70f, 0.45f);
            Quaternion rotation2 = new Quaternion(-0.5f, 0.1f, 0.2f, 0.84f).normalized;
            yield return ApplyOnRosSide(source, RosBox(source, id, CollisionObjectMsg.MOVE, position2, rotation2));
            yield return UntilUnityPose(go, source, position2, rotation2, $"'{id}' moved on the ROS side of '{source.Id}'");
            foreach (RobotInstance other in others) yield return AssertInPlanningScene(other, go, id);

            // Removed on the ROS side.
            yield return ApplyOnRosSide(source, RosBox(source, id, CollisionObjectMsg.REMOVE, Vector3.zero, Quaternion.identity));
            yield return LiveRos.Until(() => !mirror.TryGetObject(id, out _), 10f, $"'{id}' (removed on the ROS side of '{source.Id}') to leave Unity");
            foreach (RobotInstance other in others) yield return AssertNotInPlanningScene(other, id);

            yield return LiveRos.Seconds(2f);
            Assert.AreEqual(0, backToSource.Count, $"Unity sent {backToSource.Count} message(s) back to the planning scene the object came from: " +
                                                   string.Join(", ", backToSource.Messages.Select(m => $"{m.id}/op{m.operation}")));
            AssertNoExceptions();
        }

        // ---- planning and execution ----------------------------------------------------------------------

        /// <summary>
        /// Bring the ROS robot (and with it the Unity robot) to a named pose of its profile, by
        /// the app's own means: pose the Unity robot, set the goal, plan, execute. Does nothing
        /// when the robot is there already. Makes the checks that follow start from the same
        /// place whatever an earlier run left.
        /// </summary>
        protected static IEnumerator MoveRosRobotTo(RobotInstance robot, string poseName)
        {
            NamedPose pose = robot.Profile.poses.FirstOrDefault(p => p.name == poseName);
            Assert.IsNotNull(pose, $"The profile of '{robot.Id}' has a pose '{poseName}'.");
            string[] unityNames = pose.joints.Select(j => j.joint).ToArray();
            double[] values = pose.joints.Select(j => (double)j.value).ToArray();
            string[] rosNames = robot.Names.ToRos(unityNames);

            MoveItRobotSession session = MoveIt.SessionFor(robot);
            MoveIt.StopPreview(robot);
            if (Mtc != null) Mtc.StopPreview(robot);
            yield return SyncFromRos(robot);
            Dictionary<string, double> ros = null;
            yield return RosJoints(robot, r => ros = r);
            if (Enumerable.Range(0, rosNames.Length).All(i => Math.Abs(ros[rosNames[i]] - values[i]) < 0.01)) yield break;
            ISelectable endEffector = EndEffectorOf(robot);
            Assert.IsFalse(Press(endEffector, "set-start").IsRefused);
            robot.Model.BeginInteraction();
            robot.Model.ApplyJointState(unityNames, values);
            robot.Model.EndInteraction();
            yield return null;
            Assert.IsFalse(Press(endEffector, "set-goal").IsRefused);
            var outcome = new PlanOutcome();
            yield return PressPlan(robot, outcome);
            Assert.IsNull(outcome.Failure, $"MoveIt could not plan '{robot.Id}' back to '{poseName}': {outcome.Failure}");
            TrajectorySelectable handle = MoveIt.Results.LastOrDefault(r => r != null && r.Result == outcome.Result);
            Assert.IsFalse(Press(handle, "execute").IsRefused);
            yield return LiveRos.Until(() => !session.Client.IsFollowingExecution, 60f, $"'{robot.Id}' to reach '{poseName}'");
            yield return RosJoints(robot, r => ros = r);
            for (int i = 0; i < rosNames.Length; i++)
                Assert.AreEqual(values[i], ros[rosNames[i]], 0.01, $"'{robot.Id}' {rosNames[i]} after moving to '{poseName}'");
            MoveIt.ResetPlanningState(robot);
        }

        protected sealed class PlanOutcome
        {
            public PlanResult Result;
            public string Failure;
            public bool Done => Result != null || Failure != null;
        }

        /// <summary>Press "plan" on a robot's end effector and wait for MoveIt's answer.</summary>
        protected static IEnumerator PressPlan(RobotInstance robot, PlanOutcome outcome, float timeoutSeconds = 60f)
        {
            Action<PlanResult> produced = r => { if (r.RobotId == robot.Id) outcome.Result = r; };
            Action<string> failed = m => outcome.Failure = m ?? "(no message)";
            MoveIt.PlanProduced += produced;
            MoveIt.PlanFailed += failed;
            try
            {
                InteractionRefusal refusal = Press(EndEffectorOf(robot), "plan");
                Assert.IsFalse(refusal.IsRefused, refusal.Reason);
                yield return LiveRos.Until(() => outcome.Done, timeoutSeconds, $"MoveIt's answer to the plan request of '{robot.Id}'");
            }
            finally
            {
                MoveIt.PlanProduced -= produced;
                MoveIt.PlanFailed -= failed;
            }
        }

        /// <summary>A ROS-axes offset in a robot's planning frame as a Unity world offset.</summary>
        protected static Vector3 RosOffset(RobotInstance robot, Vector3 ros) =>
            robot.PlanningFrame.TransformVector(new Vector3(ros.x, ros.z, ros.y));

        /// <summary>
        /// The user's planning flow on one robot: set the start where the robot is, drag the end
        /// effector by <paramref name="rosOffset"/>, set the goal, plan, watch the preview,
        /// execute; the ROS robot goes to the goal and the Unity robot follows it there.
        /// </summary>
        protected static IEnumerator AssertPlanAndExecute(RobotInstance robot, Vector3 rosOffset)
        {
            MoveItRobotSession session = MoveIt.SessionFor(robot);
            Assert.IsNotNull(session, $"MoveIt serves '{robot.Id}'.");
            yield return MoveRosRobotTo(robot, robot.Profile.initialPose);
            session.StopPreview();
            yield return SyncFromRos(robot);
            ISelectable endEffector = EndEffectorOf(robot);

            Assert.IsFalse(Press(endEffector, "set-start").IsRefused);
            Dictionary<string, float> start = UnityJoints(robot);

            Vector3 target = robot.Model.EndEffector.position + RosOffset(robot, rosOffset);
            yield return DragEndEffector(robot, target);
            Assert.Less((robot.Model.EndEffector.position - target).magnitude, 0.02f, $"IK brought the end effector of '{robot.Id}' to the target.");
            Dictionary<string, float> goal = UnityJoints(robot);
            Assert.IsFalse(Press(endEffector, "set-goal").IsRefused);
            Assert.IsTrue(session.HasStart && session.HasGoal, "Start and goal are captured.");

            int refusalsBefore = Refusals.Count;
            var outcome = new PlanOutcome();
            yield return PressPlan(robot, outcome);
            Assert.IsNull(outcome.Failure, $"MoveIt failed to plan for '{robot.Id}': {outcome.Failure}");
            PlanResult plan = outcome.Result;
            Assert.AreEqual(robot.Id, plan.RobotId);
            Assert.IsTrue(plan.HasTrajectory, "The plan has a trajectory.");
            Assert.IsFalse(plan.HasLimitViolations, "The plan stays inside the joint limits: " + JointLimitViolation.Describe(plan.LimitViolations));
            Assert.IsEmpty(Refusals.Skip(refusalsBefore).Where(r => r.Code == RefusalCode.PlanOutsideLimits).ToList());

            // The trajectory is in Unity joint names, starts at the start and ends at the goal.
            string[] names = plan.Trajectory.joint_names;
            CollectionAssert.IsSubsetOf(names, robot.Model.GetJointStateNames(), "Trajectory joints are joints of the Unity robot.");
            string[] rosNames = robot.Names.ToRos(names);
            double[] first = plan.Trajectory.points[0].positions, last = plan.Trajectory.points[^1].positions;
            for (int i = 0; i < names.Length; i++)
            {
                Assert.AreEqual(start[rosNames[i]], first[i], 0.02, $"Trajectory start, {names[i]}");
                Assert.AreEqual(goal[rosNames[i]], last[i], session.Client.Settings.goalTolerance + 1e-3, $"Trajectory end, {names[i]}");
            }

            // The preview plays on a blue copy of this robot; the robot itself stays where it is.
            Assert.IsTrue(session.IsPreviewing, "The plan is previewed when it arrives.");
            Assert.IsTrue(robot.Ghosts != null && robot.Ghosts.IsPreviewPlaying, $"The preview of robot '{robot.Id}' plays on its copy.");
            ArticulationBody[] copyJoints = robot.Ghosts.PreviewGhost.GetComponentsInChildren<ArticulationBody>().Where(b => b.dofCount == 1).ToArray();
            float[] copyBefore = copyJoints.Select(b => b.jointPosition[0]).ToArray();
            float moved = 0f, robotMoved = 0f;
            Dictionary<string, float> before = UnityJoints(robot);
            float until = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < until && moved < 0.01f)
            {
                yield return null;
                for (int i = 0; i < copyJoints.Length; i++)
                    moved = Mathf.Max(moved, Mathf.Abs(copyJoints[i].jointPosition[0] - copyBefore[i]));
                Dictionary<string, float> now = UnityJoints(robot);
                robotMoved = Mathf.Max(robotMoved, now.Max(j => Mathf.Abs(j.Value - before[j.Key])));
            }
            Assert.Greater(moved, 0.01f, $"The copy of robot '{robot.Id}' moves along the plan.");
            Assert.Less(robotMoved, 1e-3f, $"Robot '{robot.Id}' itself does not move during the preview.");

            // Execute with the trajectory's verb.
            TrajectorySelectable handle = MoveIt.Results.LastOrDefault(r => r != null && r.Result == plan);
            Assert.IsNotNull(handle, "The plan is selectable.");
            Assert.IsFalse(Press(handle, "execute").IsRefused);

            using (var states = new LiveRos.TopicCounter<JointStateMsg>(robot.Ros, robot.JointStatesTopic))
            {
                bool timedOut = false;
                yield return LiveRos.Until(() => states.Count > 0 && Farthest(states.Messages[^1], rosNames, last) < 0.01, 40f, t => timedOut = t);
                if (timedOut)
                    Assert.Fail($"The ROS robot '{robot.Id}' did not reach the goal: farthest joint is " +
                                $"{(states.Count > 0 ? Farthest(states.Messages[^1], rosNames, last) : double.NaN):F4} rad away ({states.Count} joint states seen).");
                yield return LiveRos.Seconds(0.5f);
                JointStateMsg final = states.Messages[^1];
                Dictionary<string, float> unity = UnityJoints(robot);
                for (int i = 0; i < final.name.Length; i++)
                    Assert.AreEqual(final.position[i], unity[final.name[i]], JointTolerance, $"After execution, '{robot.Id}' {final.name[i]}: Unity vs joint_states.");
            }

            // The app knows the execution is over, and the Unity robot is the user's again.
            yield return LiveRos.Until(() => !session.Client.IsFollowingExecution, 20f, $"the execution on '{robot.Id}' to be reported finished");
            Assert.IsFalse(session.Client.IsMirroring, "After an execution the robot is no longer driven by joint states (unless Mirror joints is on).");

            // The robot is still the user's to pose: the next goal is set by dragging it again.
            Vector3 next = robot.Model.EndEffector.position + RosOffset(robot, new Vector3(-0.06f, 0.05f, 0.05f));
            yield return DragEndEffector(robot, next);
            yield return LiveRos.Seconds(0.5f);
            Assert.Less((robot.Model.EndEffector.position - next).magnitude, 0.02f,
                $"After an execution the end effector of '{robot.Id}' can be dragged to a new pose and stays there.");
            yield return SyncFromRos(robot);
            AssertNoExceptions();
        }

        private static double Farthest(JointStateMsg state, string[] rosNames, double[] goal)
        {
            double worst = 0;
            for (int i = 0; i < rosNames.Length; i++)
            {
                int at = Array.IndexOf(state.name, rosNames[i]);
                if (at < 0) return double.PositiveInfinity;
                worst = Math.Max(worst, Math.Abs(state.position[at] - goal[i]));
            }
            return worst;
        }

        /// <summary>
        /// A goal with an obstacle in the way gives a plan that goes around it or a refusal the
        /// user is told about; a goal inside an obstacle is always refused.
        /// </summary>
        protected static IEnumerator AssertPlanAroundObstacle(RobotInstance robot)
        {
            MoveItRobotSession session = MoveIt.SessionFor(robot);
            yield return MoveRosRobotTo(robot, robot.Profile.initialPose);
            session.StopPreview();
            yield return SyncFromRos(robot);
            ISelectable endEffector = EndEffectorOf(robot);
            Transform frame = robot.PlanningFrame;
            Assert.IsFalse(Press(endEffector, "set-start").IsRefused);
            Vector3 startTip = robot.Model.EndEffector.position;

            // The goal: the end effector 36 cm to the side (in the robot's own left, so the same
            // motion for every robot wherever it stands).
            Vector3 goalTip = startTip + robot.transform.TransformVector(new Vector3(-0.36f, 0f, 0f));
            yield return DragEndEffector(robot, goalTip);
            Assert.Less((robot.Model.EndEffector.position - goalTip).magnitude, 0.02f, "IK reached the goal pose.");
            Assert.IsFalse(Press(endEffector, "set-goal").IsRefused);

            // A post standing in the straight line between the two: clear of the arm at both ends,
            // in the way of the hand in between.
            Vector3 middle = frame.InverseTransformPoint((startTip + goalTip) * 0.5f);
            GameObject wall = AddShape(PrimitiveType.Cube, frame, new Vector3(middle.x, middle.z, middle.y + 0.03f),
                Quaternion.identity, new Vector3(0.03f, 0.36f, 0.03f));
            string wallId = IdOf(wall);
            yield return AssertInPlanningScene(robot, wall, wallId);

            int refusalsBefore = Refusals.Count;
            var outcome = new PlanOutcome();
            yield return PressPlan(robot, outcome);
            if (outcome.Result != null)
            {
                Assert.IsFalse(outcome.Result.HasLimitViolations);
                session.StopPreview();
                yield return null;
                string hit = FirstWaypointInside(robot, outcome.Result, wall.GetComponent<Collider>());
                Assert.IsNull(hit, $"The plan of '{robot.Id}' passes through the obstacle: {hit}");
                Debug.Log($"[live] '{robot.Id}' planned around the post ({outcome.Result.Trajectory.points.Length} points).");
            }
            else
            {
                Debug.Log($"[live] '{robot.Id}' could not plan around the post: {outcome.Failure}");
                Assert.IsNotEmpty(Refusals.Skip(refusalsBefore).Where(r => r.Code == RefusalCode.PlanFailed).ToList(),
                    "A failed plan is a refusal the user is told about.");
            }

            // The same goal with an obstacle around the goal pose of the hand: MoveIt must refuse,
            // and the user is told.
            session.StopPreview();
            yield return SyncFromRos(robot);
            wall.transform.SetPositionAndRotation(goalTip, frame.rotation);
            wall.transform.localScale = Vector3.one * 0.10f;
            yield return AssertInPlanningScene(robot, wall, wallId);
            refusalsBefore = Refusals.Count;
            int exceptionsBefore = Exceptions.Count;
            outcome = new PlanOutcome();
            yield return PressPlan(robot, outcome);
            Assert.IsNull(outcome.Result, "A goal inside an obstacle cannot be planned to.");
            Assert.IsNotNull(outcome.Failure);
            Debug.Log($"[live] '{robot.Id}' goal inside an obstacle refused with: {outcome.Failure}");
            yield return null;
            List<InteractionRefusal> told = Refusals.Skip(refusalsBefore).Where(r => r.Code == RefusalCode.PlanFailed).ToList();
            Assert.IsNotEmpty(told, "A failed plan is a refusal the user is told about.");
            StringAssert.Contains("Planning failed", told[0].Reason);
            Assert.AreEqual(exceptionsBefore, Exceptions.Count, "A refused plan throws nothing.");
            DeleteWithVerb(wall);
            yield return AssertNotInPlanningScene(robot, wallId);
            MoveIt.ResetPlanningState(robot);
            yield return SyncFromRos(robot);
            AssertNoExceptions();
        }

        // Plays the plan's waypoints on the robot and reports the first one where a link's centre
        // line is inside the obstacle. The robot is put back where it was.
        private static string FirstWaypointInside(RobotInstance robot, PlanResult plan, Collider obstacle)
        {
            IRobotModel model = robot.Model;
            string[] savedNames = model.GetJointStateNames();
            float[] saved = model.GetJointStatePositions();
            var chain = new List<Transform>();
            foreach (string joint in model.JointNames)
                if (model.TryGetJointLink(joint, out Transform link)) chain.Add(link);
            chain.Add(model.EndEffector);
            string hit = null;
            try
            {
                for (int p = 0; p < plan.Trajectory.points.Length && hit == null; p++)
                {
                    model.ApplyJointState(plan.Trajectory.joint_names, plan.Trajectory.points[p].positions);
                    Physics.SyncTransforms();
                    for (int i = 0; i + 1 < chain.Count && hit == null; i++)
                        for (int s = 0; s <= 6; s++)
                        {
                            Vector3 point = Vector3.Lerp(chain[i].position, chain[i + 1].position, s / 6f);
                            if ((obstacle.ClosestPoint(point) - point).sqrMagnitude < 1e-10f)
                            {
                                hit = $"waypoint {p}, between '{chain[i].name}' and '{chain[i + 1].name}'";
                                break;
                            }
                        }
                }
            }
            finally
            {
                model.ApplyJointState(savedNames, Array.ConvertAll(saved, v => (double)v));
            }
            return hit;
        }
    }
}
