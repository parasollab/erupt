using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.Trajectory;

namespace Erupt.Robot
{
    /// <summary>
    /// Shows translucent copies of a robot at its start and goal poses, and one that plays a
    /// plan (<see cref="PlayPreview"/>) while the robot itself stays where it is. The copy comes
    /// from the robot profile's ghost prefab and is posed like the robot this component belongs to.
    /// </summary>
    public class RobotGhosts : MonoBehaviour, IJointPoseTarget
    {
        [Tooltip("Ghost source. Empty = the ghost prefab of the robot's profile.")]
        public GameObject robotPrefab;

        public Color startGhostColor = new Color(0, 1, 0, 0.5f);
        public Color goalGhostColor = new Color(1, 0.5f, 0, 0.5f);
        [Tooltip("Colour of the copy that shows where the robot's base would be placed.")]
        public Color placementGhostColor = new Color(0f, 0.75f, 1f, 0.4f);
        [Tooltip("Colour of the copy that plays a plan's preview.")]
        public Color previewGhostColor = new Color(0.1f, 0.35f, 1f, 0.6f);

        [Tooltip("The robot the ghosts copy. Empty = the RobotInstance this component sits under.")]
        public GameObject realRobot;

        private RobotInstance robot;
        private GameObject startGhost;
        private GameObject goalGhost;
        private GameObject placementGhost;
        private GameObject previewGhost;
        private JointTrajectoryPlayer previewPlayer;
        private readonly Dictionary<string, ArticulationBody> previewBodies = new Dictionary<string, ArticulationBody>();

        public GameObject StartGhost => startGhost;
        public GameObject GoalGhost => goalGhost;
        /// <summary>The copy that shows where the robot's base would be placed; null while none is shown.</summary>
        public GameObject PlacementGhost => placementGhost;

        private RobotInstance Robot => robot != null ? robot : robot = GetComponentInParent<RobotInstance>(true);

        private GameObject Source => realRobot != null ? realRobot : (Robot != null ? Robot.gameObject : null);

        private GameObject GhostPrefab =>
            robotPrefab != null ? robotPrefab : (Robot != null && Robot.Profile != null ? Robot.Profile.ghostPrefab : null);

        public void SpawnStartGhost()
        {
            ClearStartGhost();
            startGhost = SpawnGhost("StartGhost", startGhostColor);
            if (startGhost != null) CopyPoseToGhost(Source, startGhost);
        }

        public void SpawnGoalGhost()
        {
            ClearGoalGhost();
            goalGhost = SpawnGhost("GoalGhost", goalGhostColor);
            if (goalGhost != null) CopyPoseToGhost(Source, goalGhost);
        }

        public void UpdateStartGhost()
        {
            if (startGhost == null) return;
            CopyPoseToGhost(Source, startGhost);
        }

        public void UpdateGoalGhost()
        {
            if (goalGhost == null) return;
            CopyPoseToGhost(Source, goalGhost);
        }

        public void ClearStartGhost()
        {
            if (startGhost != null) Destroy(startGhost);
            startGhost = null;
        }

        public void ClearGoalGhost()
        {
            if (goalGhost != null) Destroy(goalGhost);
            goalGhost = null;
        }

        /// <summary>
        /// Show a copy of the robot, in its current joint pose, with its base at
        /// <paramref name="basePose"/>: where the robot would stand if it were placed there.
        /// Calling it again moves the copy. False when the robot has no ghost prefab.
        /// </summary>
        public bool ShowPlacementGhost(Pose basePose)
        {
            if (placementGhost == null)
            {
                placementGhost = SpawnGhost("PlacementGhost", placementGhostColor);
                if (placementGhost == null) return false;
            }
            ArticulationRootMover.MoveRoot(placementGhost, basePose.position, basePose.rotation);
            CopyPoseToGhost(Source, placementGhost);
            return true;
        }

        public void ClearPlacementGhost()
        {
            if (placementGhost != null)
            {
                if (Application.isPlaying) Destroy(placementGhost);
                else DestroyImmediate(placementGhost);
            }
            placementGhost = null;
        }

        /// <summary>Remove the start and goal copies (the planning ghosts). The placement copy has its own <see cref="ClearPlacementGhost"/>, the preview copy <see cref="StopPreview"/>.</summary>
        public void ClearGhosts()
        {
            ClearStartGhost();
            ClearGoalGhost();
        }

        /// <summary>The copy a preview plays on; null while none is playing.</summary>
        public GameObject PreviewGhost => previewGhost;

        /// <summary>True while a preview is playing on its copy.</summary>
        public bool IsPreviewPlaying => previewGhost != null;

        /// <summary>False for a robot with no ghost prefab: it has no copy to preview on.</summary>
        public bool CanShowGhosts => GhostPrefab != null && Source != null;

        /// <summary>
        /// Play a trajectory, in a loop, on a copy of the robot in <see cref="previewGhostColor"/>.
        /// The robot itself does not move. The copy starts in the robot's pose (so the joints the
        /// trajectory does not name, a gripper's, look as they do on the robot) and is removed by
        /// <see cref="StopPreview"/>. Calling it again plays the new trajectory instead.
        /// False when the robot has no ghost prefab.
        /// </summary>
        public bool PlayPreview(JointTrajectoryMsg trajectory)
        {
            if (trajectory == null || !CanShowGhosts) return false;
            // A copy of its own for every preview: a player that was told to stop only lets go
            // on its next frame, and a new preview must not wait for it.
            StopPreview();
            previewGhost = SpawnGhost("PreviewGhost", previewGhostColor);
            if (previewGhost == null) return false;

            previewBodies.Clear();
            foreach (ArticulationBody body in previewGhost.GetComponentsInChildren<ArticulationBody>(true))
                previewBodies[body.name] = body;
            CopyPoseToGhost(Source, previewGhost);

            // The player lives on the copy, so it ends with it.
            previewPlayer = previewGhost.AddComponent<JointTrajectoryPlayer>();
            previewPlayer.StartReplay(trajectory, this);
            return true;
        }

        /// <summary>Stop the preview and remove its copy. Does nothing when none is playing.</summary>
        public void StopPreview()
        {
            if (previewGhost != null)
            {
                if (Application.isPlaying) Destroy(previewGhost);
                else DestroyImmediate(previewGhost);
            }
            previewGhost = null;
            previewPlayer = null;
            previewBodies.Clear();
        }

        // The preview's joint states, written to the copy: each named joint goes to the copy's
        // body named like the link the joint moves on the robot, clamped to the robot's limits
        // like every other joint write.
        void IJointPoseTarget.ApplyJointState(string[] names, double[] positions)
        {
            if (previewGhost == null || names == null || positions == null) return;
            IRobotModel model = Robot != null ? Robot.Model : null;
            if (model == null) return;

            int count = Mathf.Min(names.Length, positions.Length);
            for (int i = 0; i < count; i++)
            {
                if (!model.TryGetJointLink(names[i], out Transform link) || link == null) continue;
                if (!previewBodies.TryGetValue(link.name, out ArticulationBody body) || body == null || body.dofCount != 1) continue;
                JointLimit limit = default;
                if (model.Limits != null) model.Limits.TryGet(names[i], out limit);
                JointWriter.ApplyJointPosition(body, (float)positions[i], limit, sync: false);
            }
            Physics.SyncTransforms();
        }

        // The placement and preview copies stand in the scene on their own and would outlive the robot.
        private void OnDestroy()
        {
            ClearPlacementGhost();
            StopPreview();
        }

        private GameObject SpawnGhost(string ghostName, Color color)
        {
            GameObject prefab = GhostPrefab;
            GameObject source = Source;
            if (prefab == null || source == null)
            {
                Debug.LogWarning("[RobotGhosts] No ghost prefab (set one on the robot profile) or no robot to copy; no ghost shown.", this);
                return null;
            }

            // Instantiate under an inactive parent so Awake is deferred until after cleanup.
            var deferParent = new GameObject();
            deferParent.SetActive(false);

            GameObject ghost = Instantiate(prefab, deferParent.transform);
            ghost.name = ghostName;

            // Strip all behavior scripts before Awake can run — ghost is purely visual.
            foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
                DestroyImmediate(mb);

            foreach (var col in ghost.GetComponentsInChildren<Collider>())
                col.enabled = false;

            foreach (var ab in ghost.GetComponentsInChildren<ArticulationBody>(true))
            {
                ab.useGravity = false;
                ab.linearVelocity = Vector3.zero;
                ab.angularVelocity = Vector3.zero;
                if (ab.isRoot) ab.immovable = true;
            }

            var overlay = ghost.AddComponent<TranslucentOverride>();
            overlay.overlayColor = color;
            overlay.SetTranslucent(true);  // Apply immediately; don't wait for Start()

            // Set world position before activating so ArticulationBody physics registers at the right location.
            ghost.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);

            // The ghost stands in the scene on its own; the marker says whose it is.
            RobotOwned.Mark(ghost, Robot);

            ghost.transform.SetParent(null);
            ghost.SetActive(true);
            Destroy(deferParent);

            return ghost;
        }

        // Poses the ghost like the source: each one-degree-of-freedom joint of the source is
        // written to the ghost body of the same name (both hierarchies come from the same URDF)
        // through JointWriter, clamped to the robot's limits like every other joint write.
        private void CopyPoseToGhost(GameObject source, GameObject ghost)
        {
            if (source == null || ghost == null) return;

            var ghostAbByName = new Dictionary<string, ArticulationBody>();
            foreach (var ab in ghost.GetComponentsInChildren<ArticulationBody>(true))
                ghostAbByName[ab.name] = ab;

            foreach (var sourceAb in source.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (!ghostAbByName.TryGetValue(sourceAb.name, out ArticulationBody ghostAb)) continue;
                // URDF joints have one degree of freedom or none.
                if (sourceAb.dofCount != 1) continue;

                JointWriter.ApplyJointPosition(ghostAb, sourceAb.jointPosition[0], LimitFor(sourceAb), sync: false);
            }

            Physics.SyncTransforms();
        }

        // The limits of the joint on a body of the source robot; none when the robot does not
        // describe that joint.
        private JointLimit LimitFor(ArticulationBody sourceBody)
        {
            RobotInstance owner = Robot;
            if (owner == null) return default;

            DirectArticulationIKController controller = owner.Controller;
            if (controller != null && controller.TryGetLimit(sourceBody, out JointLimit limit)) return limit;

            // A source outside the controller's own hierarchy: the profile says which joint moves the link.
            IJointLimits limits = owner.Model != null ? owner.Model.Limits : null;
            JointSpec[] joints = owner.Profile != null ? owner.Profile.joints : null;
            if (limits == null || joints == null) return default;
            foreach (JointSpec joint in joints)
                if (joint != null && joint.childLink == sourceBody.name && limits.TryGet(joint.name, out limit))
                    return limit;
            return default;
        }
    }
}
