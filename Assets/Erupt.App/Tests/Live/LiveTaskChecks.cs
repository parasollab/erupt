using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Plugins.Demonstration;
using Erupt.Plugins.MoveIt;
using Erupt.Plugins.Mtc;
using Erupt.Robot;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Sensor;
using RosMessageTypes.Trajectory;

namespace Erupt.App.Live.Tests
{
    /// <summary>The gripper, pick and place (MTC), demonstrations and the scene reset, against the real stack.</summary>
    public abstract partial class LiveSceneFixture
    {
        // ---- gripper -----------------------------------------------------------------------------------

        /// <summary>
        /// The gripper verb closes and opens the real gripper: the ROS robot's finger joint goes to
        /// the profile's closed and open positions, and the Unity gripper shows what ROS reports.
        /// </summary>
        protected static IEnumerator AssertGripperReachesRos(RobotInstance robot)
        {
            GripperSpec spec = robot.Profile.gripper;
            Assert.IsTrue(spec != null && spec.IsSet, $"The profile of '{robot.Id}' describes its gripper.");
            Assert.IsNotNull(robot.Gripper, $"Robot '{robot.Id}' has a gripper.");
            MoveItPlanningClient client = MoveIt.SessionFor(robot).Client;
            string joint = robot.Names.ToRos(spec.driverJoint);
            yield return SyncFromRos(robot);

            Dictionary<string, double> ros = null;
            yield return RosJoints(robot, r => ros = r);
            bool startsOpen = Math.Abs(ros[joint] - spec.open) < Math.Abs(ros[joint] - spec.closed);
            int refusalsBefore = Refusals.Count;

            using var states = new LiveRos.TopicCounter<JointStateMsg>(robot.Ros, robot.JointStatesTopic);
            for (int press = 0; press < 2; press++)
            {
                bool expectOpen = press == 0 ? !startsOpen : startsOpen;
                float target = expectOpen ? spec.open : spec.closed;
                Assert.IsFalse(Press(EndEffectorOf(robot), "gripper").IsRefused);

                bool timedOut = false;
                yield return LiveRos.Until(() => states.Count > 0 && Math.Abs(Value(states.Messages[^1], joint) - target) < 2e-3, 20f, t => timedOut = t);
                if (timedOut)
                    Assert.Fail($"The gripper verb did not {(expectOpen ? "open" : "close")} the ROS gripper of '{robot.Id}': {joint} is " +
                                $"{(states.Count > 0 ? Value(states.Messages[^1], joint) : double.NaN):F4}, expected {target:F4}. " +
                                "Refusals: " + string.Join("; ", Refusals.Skip(refusalsBefore).Select(r => r.Reason)));

                // ... and it comes back: the Unity gripper shows what ROS reports.
                bool was = client.IsMirroring;
                client.SetMirroring(true);
                yield return LiveRos.Until(() => robot.Model.TryGetJointAngle(spec.driverJoint, out float shown) && Mathf.Abs(shown - target) < 2e-3f,
                    10f, $"the Unity gripper of '{robot.Id}' to show the {(expectOpen ? "open" : "closed")} gripper ROS reports");
                client.SetMirroring(was);
                states.Clear();
                yield return LiveRos.Seconds(0.3f);
            }
            Assert.IsEmpty(Refusals.Skip(refusalsBefore).Select(r => r.Reason).ToList(), "The gripper verb raised refusals.");
            AssertNoExceptions();
        }

        private static double Value(JointStateMsg state, string joint)
        {
            int at = Array.IndexOf(state.name, joint);
            return at < 0 ? double.NaN : state.position[at];
        }

        // ---- pick and place (MTC) ------------------------------------------------------------------------

        /// <summary>
        /// The pick-and-place flow of the MTC tab on one robot: record a task for a small
        /// Unity-published object (select it, put it somewhere else, stop), receive the stage tree
        /// and the solutions, select and preview one, execute it. Attach and detach are mirrored,
        /// and the object ends at the place pose in Unity and in MoveIt.
        /// </summary>
        /// <param name="pick">Where the object starts, seen from the robot: metres (forward, left, up) from its base.</param>
        /// <param name="place">Where it is to go, in the same terms.</param>
        /// <param name="whilePlanning">Runs while the task is being planned (e.g. another robot planning); may be null.</param>
        protected static IEnumerator AssertPickAndPlace(RobotInstance robot, Vector3 pick, Vector3 place, Func<IEnumerator> whilePlanning = null,
            float placeTolerance = 0.005f, Vector3? partSize = null)
        {
            Vector3 pickRos = InFrontOf(robot, pick), placeRos = InFrontOf(robot, place);
            yield return MoveRosRobotTo(robot, robot.Profile.initialPose);
            Assert.IsNotNull(Mtc, "The MTC plugin is registered.");
            MtcPlugin.RobotSession session = Mtc.SessionFor(robot);
            Assert.IsNotNull(session, $"MTC has a session for '{robot.Id}'.");
            yield return LiveRos.Until(() => session.Available && session.PickPlace.ExecutionAvailable, 30f,
                $"the pick_place and execute_solution actions of '{robot.Id}'");
            yield return LiveRos.Until(() => !session.PickPlace.Busy, 60f, $"the pick-and-place server of '{robot.Id}' to be idle");
            MoveIt.StopPreview(robot);
            yield return SyncFromRos(robot);
            Transform frame = robot.PlanningFrame;

            // A small box within reach (a 4 cm cube unless told otherwise), published like any obstacle.
            GameObject part = AddShape(PrimitiveType.Cube, frame, pickRos, Quaternion.identity, partSize ?? Vector3.one * 0.04f);
            string id = IdOf(part);
            yield return AssertInPlanningScene(robot, part, id);
            Vector3 pickPosition = part.transform.position;
            Quaternion pickRotation = part.transform.rotation;
            Vector3 placePosition = frame.TransformPoint(new Vector3(placeRos.x, placeRos.z, placeRos.y));
            Quaternion placeRotation = frame.rotation * Quaternion.Euler(0f, 90f, 0f);   // turned a quarter about the vertical

            // Record: the robot the user works with, the tab's Record button.
            Context.Selection.Select(EndEffectorOf(robot));          // selection makes the robot the active one
            Assert.AreSame(robot, Context.Robots.Active);
            Assert.AreSame(session.Recorder, Mtc.Recorder, "The tab records for the active robot.");
            Mtc.Recorder.StartRecording();
            Context.Selection.Select(part.GetComponent<ISelectable>());
            var publisher = part.GetComponent<CollisionObjectPublisher>();
            Assert.IsTrue(publisher.pausePublishing, "The object being demonstrated is not published while it is moved.");
            part.transform.SetPositionAndRotation(placePosition, placeRotation);
            yield return null;
            Mtc.Recorder.NotifyObjectReleased(part);
            Mtc.Recorder.StopRecording();
            Assert.Less((part.transform.position - pickPosition).magnitude, 1e-4f, "The object is back at its pick pose once the task is sent.");
            Assert.IsFalse(publisher.pausePublishing);

            // Planning: feedback, stage tree, solutions.
            PickPlaceClient client = session.PickPlace;
            yield return LiveRos.Until(() => client.Busy || client.LastOutcome != PickPlaceOutcome.None, 15f, $"the pick_place goal of '{robot.Id}' to be taken");
            if (whilePlanning != null) yield return whilePlanning();
            bool timedOut = false;
            yield return LiveRos.Until(() => !client.Busy, 240f, t => timedOut = t);
            Assert.IsFalse(timedOut, $"Pick-and-place planning for '{robot.Id}' did not finish (status '{client.LastStatus}').");
            Assert.AreEqual(PickPlaceOutcome.Succeeded, client.LastOutcome, $"Planning for '{robot.Id}' ended with '{client.LastStatus}'.");
            Assert.IsNotEmpty(client.SolutionIds.ToList(), "The server found solutions.");
            Assert.IsNotNull(client.Description, "The stage tree arrived.");
            string[] stages = client.Description.stages.Select(s => s.name).ToArray();
            CollectionAssert.Contains(stages, "pick object", "stage tree: " + string.Join(", ", stages));
            CollectionAssert.Contains(stages, "place object");
            Assert.IsNotNull(client.Statistics, "The stage statistics arrived.");
            Debug.Log($"[live] '{robot.Id}' pick-and-place: {client.SolutionIds.Count} solution(s), {stages.Length} stages, task {client.TaskId}");

            // Select the best solution (a solution button of the tab).
            PlanResult plan = null;
            bool selected = false;
            Mtc.SelectSolution(robot, client.SolutionIds[0], r => { plan = r; selected = true; });
            yield return LiveRos.Until(() => selected, 15f, "the selected solution to be fetched");
            Assert.IsNotNull(plan, "The solution was fetched.");
            Assert.AreEqual(robot.Id, plan.RobotId);
            Assert.IsFalse(plan.HasLimitViolations, "The solution stays inside the joint limits: " + JointLimitViolation.Describe(plan.LimitViolations));
            var solution = (SolutionMsg)plan.PlannerPayload;
            Assert.Greater(solution.sub_trajectory.Length, 5, "The solution has its steps.");
            TrajectorySelectable handle = Mtc.Results.LastOrDefault(r => r != null && r.Result == plan);
            Assert.IsNotNull(handle, "The solution is selectable.");

            // Preview it: the robot moves, the object rides along and is put back afterwards.
            Assert.IsFalse(Press(handle, "preview").IsRefused);
            Assert.IsTrue(session.Player.IsPlaying, "The preview plays.");
            bool rode = false;
            float moved = 0f;
            Dictionary<string, float> before = UnityJoints(robot);
            float previewEnd = Time.realtimeSinceStartup + 90f;
            while (session.Player.IsPlaying && Time.realtimeSinceStartup < previewEnd && !(rode && moved > 0.05f))
            {
                yield return null;
                if (part == null) break;
                rode |= part.transform.parent != null && robot.Model.Root != null && part.transform.IsChildOf(robot.transform);
                Dictionary<string, float> now = UnityJoints(robot);
                moved = Mathf.Max(moved, now.Max(j => Mathf.Abs(j.Value - before[j.Key])));
            }
            Assert.Greater(moved, 0.05f, "The preview moves the robot.");
            Assert.IsTrue(rode, "During the preview the object rides the gripper.");
            Mtc.StopPreview(robot);
            yield return null;
            yield return null;
            Assert.IsNotNull(part, "The preview does not destroy the object.");
            Assert.Less((part.transform.position - pickPosition).magnitude, 1e-3f, "After the preview the object is back at its pick pose.");

            // Execute it with the trajectory's verb.
            int attached = 0, detached = 0;
            Action<EnvironmentObject, Transform> onAttached = (o, _) => { if (o != null && o.Id == id) attached++; };
            Action<EnvironmentObject> onDetached = o => { if (o != null && o.Id == id) detached++; };
            Context.Environment.Attached += onAttached;
            Context.Environment.Detached += onDetached;
            bool sawAttachedInMoveIt = false;
            try
            {
                Assert.IsFalse(Press(handle, "execute").IsRefused);
                yield return LiveRos.Until(() => client.Phase == PickPlacePhase.Executing || client.LastOutcome != PickPlaceOutcome.Succeeded
                                                 || client.LastStatus.StartsWith("SUCCEEDED"), 20f, t => { });
                float executeEnd = Time.realtimeSinceStartup + 180f;
                while (client.Busy && Time.realtimeSinceStartup < executeEnd)
                {
                    if (!sawAttachedInMoveIt && attached > 0 && detached == 0)
                    {
                        PlanningSceneMsg during = null;
                        yield return LiveRos.PlanningScene(robot, s => during = s);
                        sawAttachedInMoveIt = LiveRos.FindAttached(during, id) != null;
                    }
                    yield return null;
                }
                Assert.IsFalse(client.Busy, $"Execution on '{robot.Id}' did not finish (status '{client.LastStatus}').");
                Assert.AreEqual(PickPlaceOutcome.Succeeded, client.LastOutcome, $"Execution on '{robot.Id}' ended with '{client.LastStatus}'.");
            }
            finally
            {
                Context.Environment.Attached -= onAttached;
                Context.Environment.Detached -= onDetached;
            }
            Assert.Greater(attached, 0, "The attach was mirrored in Unity.");
            Assert.Greater(detached, 0, "The detach was mirrored in Unity.");
            Assert.IsTrue(sawAttachedInMoveIt, "While Unity showed the object in the gripper, MoveIt had it attached.");

            // Where it ended: on both sides, and where it was asked to go.
            yield return LiveRos.Seconds(1.5f);
            Assert.IsNotNull(part, "The object survives the pick and place.");
            Assert.IsFalse(part.transform.IsChildOf(robot.transform), "The object is released from the gripper.");
            PlanningSceneMsg scene = null;
            yield return LiveRos.PlanningScene(robot, s => scene = s);
            Assert.IsNull(LiveRos.FindAttached(scene, id), "MoveIt no longer has the object attached.");
            yield return AssertInPlanningScene(robot, part, id);
            float offset = (part.transform.position - placePosition).magnitude;
            float turn = Quaternion.Angle(part.transform.rotation, placeRotation);
            Debug.Log($"[live] '{robot.Id}' placed '{id}' {offset * 1000f:F1} mm and {turn:F2} deg from the demonstrated pose " +
                      $"(offset in the planning frame, Unity axes: {frame.InverseTransformVector(part.transform.position - placePosition):F4})");
            Assert.Less(offset, placeTolerance, $"The object ends at the pose it was demonstrated at (it is {frame.InverseTransformVector(part.transform.position - placePosition):F4} off, Unity axes of the planning frame).");
            // A cube looks the same after a quarter turn: compare modulo 90° about the vertical.
            Assert.Less(Mathf.Min(turn, Mathf.Abs(turn - 90f), Mathf.Abs(turn - 180f)), 2f, "The object ends in the orientation it was demonstrated in.");

            // The Unity robot is where the ROS robot is.
            yield return SyncFromRos(robot);
            DeleteWithVerb(part);
            yield return AssertNotInPlanningScene(robot, id);
            AssertNoExceptions();
        }

        /// <summary>
        /// The everyday pick and place: the object rests on a surface of the scene (a table, a
        /// counter: whatever published obstacle lies under the two spots) and is put down on it
        /// somewhere else. The object touches the surface at both ends, which a planner that
        /// treats every contact as a collision refuses. Where the scene has no surface under
        /// the spots, a slab is added for the object to rest on.
        /// </summary>
        /// <param name="pick">Metres (forward, left) from the robot's base to where the object stands.</param>
        /// <param name="place">Metres (forward, left) to where it is put down.</param>
        protected static IEnumerator AssertPickAndPlaceOnSurface(RobotInstance robot, Vector2 pick, Vector2 place)
        {
            // The same 4 cm cube as the pick and place in the air, standing on the surface: what
            // differs is only that it touches something at both ends. (The pick-and-place server
            // grasps an object at its centre from above. A taller block does not make it easier:
            // at 10 cm the palm of a two-finger hand reaches the block's top.)
            var block = Vector3.one * 0.04f;
            GameObject slab = null;
            bool underPick = TrySurfaceUnder(robot, pick, out float pickUp, out string pickSurface);
            bool underPlace = TrySurfaceUnder(robot, place, out float placeUp, out string placeSurface);
            if (!underPick || !underPlace)
            {
                // Nothing of the scene under one of the spots: a slab, 15 cm above the base.
                Vector2 middle = (pick + place) * 0.5f;
                Vector3 centre = InFrontOf(robot, new Vector3(middle.x, middle.y, 0.14f));
                slab = AddShape(PrimitiveType.Cube, robot.PlanningFrame, centre, Quaternion.identity,
                    Vector3.one);
                // Along the robot's axes: x is its left-right, z its forward.
                slab.transform.rotation = robot.transform.rotation;
                slab.transform.localScale = new Vector3(Mathf.Abs(pick.y - place.y) + 0.3f, 0.02f, Mathf.Abs(pick.x - place.x) + 0.3f);
                yield return AssertInPlanningScene(robot, slab, IdOf(slab));
                Physics.SyncTransforms();
                Assert.IsTrue(TrySurfaceUnder(robot, pick, out pickUp, out pickSurface) && TrySurfaceUnder(robot, place, out placeUp, out placeSurface),
                    "The slab that was added lies under both spots.");
            }
            Debug.Log($"[live] '{robot.Id}' pick and place on a surface: from '{pickSurface}' ({pickUp:F3} m above the base) to '{placeSurface}' ({placeUp:F3} m).");

            // The block stands on the surface: its centre is half its height above it. The server
            // may lift the place pose by up to 20 mm to clear the surface.
            yield return AssertPickAndPlace(robot, new Vector3(pick.x, pick.y, pickUp + block.y * 0.5f), new Vector3(place.x, place.y, placeUp + block.y * 0.5f),
                placeTolerance: 0.025f, partSize: block);

            if (slab != null)
            {
                string id = IdOf(slab);
                DeleteWithVerb(slab);
                yield return AssertNotInPlanningScene(robot, id);
            }
        }

        // The height above the robot's base of the highest published obstacle under a spot
        // (metres forward and left of the base); false when there is none within reach.
        private static bool TrySurfaceUnder(RobotInstance robot, Vector2 forwardLeft, out float up, out string surface)
        {
            up = 0f;
            surface = "";
            Physics.SyncTransforms();
            Vector3 above = robot.transform.TransformPoint(new Vector3(-forwardLeft.y, 0.6f, forwardLeft.x));
            foreach (RaycastHit hit in Physics.RaycastAll(above, Vector3.down, 1.0f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                var publisher = hit.collider.GetComponentInParent<CollisionObjectPublisher>();
                if (publisher == null || !publisher.isActiveAndEnabled || publisher.pausePublishing) continue;
                if (hit.collider.GetComponentInParent<RobotInstance>() != null) continue;
                up = hit.point.y - robot.transform.position.y;
                surface = publisher.objectId;
                // Low enough for the arm to reach down to, high enough to be a surface and not the floor far below.
                return up > -0.3f && up < 0.5f;
            }
            return false;
        }

        /// <summary>
        /// A point given from a robot's own point of view (metres forward, to its left, and up
        /// from its base) as a position in its planning frame, ROS axes. An imported robot looks
        /// along its local z with x to its right.
        /// </summary>
        protected static Vector3 InFrontOf(RobotInstance robot, Vector3 forwardLeftUp)
        {
            Vector3 world = robot.transform.TransformPoint(new Vector3(-forwardLeftUp.y, forwardLeftUp.z, forwardLeftUp.x));
            Vector3 local = robot.PlanningFrame.InverseTransformPoint(world);
            return new Vector3(local.x, local.z, local.y);
        }

        // ---- demonstrations ------------------------------------------------------------------------------

        /// <summary>
        /// Record a short demonstration, replay it, publish it. Nothing on the ROS side consumes
        /// it, so the check is that what goes on the wire is well formed (it comes back through
        /// the endpoint, which decodes it with the ROS definitions) and that nothing throws.
        /// </summary>
        protected static IEnumerator AssertDemonstration(RobotInstance robot)
        {
            Assert.IsNotNull(Demonstrations, "The demonstration plugin is registered.");
            DemonstrationCollectionPlugin.RobotSession session = Demonstrations.SessionFor(robot);
            Assert.IsNotNull(session, $"The demonstration plugin has a session for '{robot.Id}'.");
            MoveIt.StopPreview(robot);
            yield return SyncFromRos(robot);
            int exceptionsBefore = Exceptions.Count;

            DemonstrationRecorder recorder = session.Recorder;
            using var published = new LiveRos.TopicCounter<JointTrajectoryMsg>(Context.Ros, recorder.TrajectoryTopic);
            using var virtualStates = new LiveRos.TopicCounter<JointStateMsg>(Context.Ros, recorder.OutputStateTopic);

            Demonstrations.StartDemonstration(robot);
            Assert.AreSame(robot, Demonstrations.RecordingRobot, "The demonstration records on the robot it was started for.");
            Vector3 home = robot.Model.EndEffector.position;
            yield return DragEndEffector(robot, home + RosOffset(robot, new Vector3(0.08f, 0.08f, -0.05f)));
            yield return LiveRos.Seconds(0.4f);
            yield return DragEndEffector(robot, home + RosOffset(robot, new Vector3(0.0f, -0.08f, 0.05f)));
            yield return LiveRos.Seconds(0.4f);
            Demonstrations.StopDemonstration(robot);

            JointTrajectoryMsg demonstration = recorder.LastDemonstration;
            Assert.IsNotNull(demonstration, "A demonstration was recorded.");
            Assert.GreaterOrEqual(demonstration.points.Length, 5, "The demonstration has its samples.");
            Assert.IsEmpty(recorder.LastDemonstrationViolations.ToList(), "A demonstration made by dragging stays inside the joint limits.");

            // Replay: the robot goes through it once.
            Demonstrations.Replay(robot);
            Assert.IsTrue(session.IsReplaying, "The replay started.");
            float moved = 0f;
            Dictionary<string, float> before = UnityJoints(robot);
            float end = Time.realtimeSinceStartup + 30f;
            while (session.IsReplaying && Time.realtimeSinceStartup < end)
            {
                yield return null;
                Dictionary<string, float> now = UnityJoints(robot);
                moved = Mathf.Max(moved, now.Max(j => Mathf.Abs(j.Value - before[j.Key])));
            }
            Assert.IsFalse(session.IsReplaying, "The replay ends by itself.");
            Assert.Greater(moved, 0.01f, "The replay moves the robot.");

            // Publish: one message, with the robot's ROS joint names, increasing times, one
            // position per joint; ROS hands it back unchanged.
            published.Clear();
            Demonstrations.Publish(robot);
            yield return LiveRos.Until(() => published.Count >= 2, 10f,
                $"the demonstration to come back from ROS on '{recorder.TrajectoryTopic}' (once as sent, once as ROS relays it)");
            string[] rosNames = robot.Names.ToRos(robot.Model.GetJointStateNames());
            foreach (JointTrajectoryMsg message in published.Messages)
            {
                CollectionAssert.AreEqual(rosNames, message.joint_names, "The published demonstration names the joints as ROS does.");
                Assert.AreEqual(demonstration.points.Length, message.points.Length);
                double last = -1;
                foreach (JointTrajectoryPointMsg point in message.points)
                {
                    Assert.AreEqual(rosNames.Length, point.positions.Length, "One position per joint.");
                    double t = point.time_from_start.sec + point.time_from_start.nanosec * 1e-9;
                    Assert.Greater(t, last, "Times increase along the demonstration.");
                    last = t;
                }
            }
            Assert.Greater(virtualStates.Count, 0, "The virtual robot's joint state is published while the plugin runs.");
            CollectionAssert.AreEqual(rosNames, virtualStates.Messages[^1].name, "The virtual joint state names the joints as ROS does.");

            Assert.AreEqual(exceptionsBefore, Exceptions.Count, "Recording, replaying and publishing a demonstration throws nothing:\n" +
                                                                 Join(Exceptions.Skip(exceptionsBefore)));
            yield return SyncFromRos(robot);
        }

        // ---- scene reset -----------------------------------------------------------------------------------

        /// <summary>
        /// Reloading the scene (what the app's reset action does) keeps one connection: the
        /// outgoing scene's obstacles leave the planning scene, the incoming scene's arrive, joint
        /// states flow again, and no second socket or stale callback is left behind.
        /// </summary>
        protected IEnumerator AssertSceneReloadKeepsOneConnection()
        {
            RobotInstance robot = Robots[0];
            GameObject shape = AddShape(PrimitiveType.Sphere, robot.PlanningFrame, new Vector3(0.2f, -0.8f, 0.6f), Quaternion.identity, Vector3.one * 0.1f);
            string shapeId = IdOf(shape);
            yield return AssertInPlanningScene(robot, shape, shapeId);
            string robotId = robot.Id;
            ROSConnection before = Connection;
            int exceptionsBefore = Exceptions.Count, errorsBefore = Errors.Count;

            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            yield return null;
            yield return null;
            yield return Bind();
            Spawned.Clear();

            ROSConnection[] connections = Resources.FindObjectsOfTypeAll<ROSConnection>().Where(c => c.gameObject.scene.IsValid()).ToArray();
            Assert.AreEqual(1, connections.Length, "One ROS connection after the scene was reloaded.");
            Assert.IsTrue(before != null, "The connection survived the reload.");
            Assert.AreSame(before, connections[0], "The reloaded scene uses the same connection.");
            Assert.IsTrue(before.HasConnectionThread && !before.HasConnectionError);

            robot = Robot(robotId);
            yield return AssertConnected();
            yield return AssertNotInPlanningScene(robot, shapeId, 15f);
            yield return AssertEveryPublisherIsInPlanningScene(robot);
            yield return AssertJointStatesMirrored(robot);
            yield return LiveRos.Seconds(1f);
            Assert.AreEqual(exceptionsBefore, Exceptions.Count, "Reloading the scene throws nothing:\n" + Join(Exceptions.Skip(exceptionsBefore)));
            List<string> errors = Errors.Skip(errorsBefore).Where(e => !IsHeadlessNoise(e)).ToList();
            Assert.IsEmpty(errors, "Reloading the scene logged errors:\n" + Join(errors));
        }
    }
}
