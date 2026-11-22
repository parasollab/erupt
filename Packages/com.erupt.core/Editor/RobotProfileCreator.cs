using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Erupt.Robot;

namespace Erupt.Editor
{
    /// <summary>
    /// Writes a <see cref="RobotProfile"/> for an imported robot: the joints it finds (name, link,
    /// type, limits, velocity limit and mimic where the import kept them) and a first guess at
    /// their roles and at the gripper. What a URDF does not say (planning groups, named poses,
    /// the trajectory topic, a ROS name rule) is left at its default for the user to fill in.
    /// </summary>
    /// <remarks>
    /// The URDF importer's joint components are read by reflection, as the runtime does, so
    /// this assembly does not depend on the importer.
    /// From the command line:
    /// <c>-executeMethod Erupt.Editor.RobotProfileCreator.CreateFromCommandLine -robot Assets/robot.prefab -out Assets/Robot.asset</c>
    /// </remarks>
    public static class RobotProfileCreator
    {
        private const string MenuPath = "ERUPT/Robots/Create Profile From Selected Robot";
        private const string UrdfJointType = "Unity.Robotics.UrdfImporter.UrdfJoint";
        private const string UrdfContinuousType = "Unity.Robotics.UrdfImporter.UrdfJointContinuous";

        [MenuItem(MenuPath)]
        public static void CreateFromSelection()
        {
            GameObject robot = Selection.activeGameObject;
            string path = EditorUtility.SaveFilePanelInProject(
                "Save robot profile", robot.name, "asset", "Where to save the profile of " + robot.name);
            if (string.IsNullOrEmpty(path)) return;

            RobotProfile profile = Create(robot, path);
            Selection.activeObject = profile;
            foreach (string problem in profile.Validate())
                Debug.LogWarning($"[RobotProfileCreator] {profile.name}: {problem}", profile);
        }

        [MenuItem(MenuPath, true)]
        private static bool CanCreateFromSelection() =>
            Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponentInChildren<ArticulationBody>(true) != null;

        /// <summary>Reads <c>-robot</c> (a prefab asset path) and <c>-out</c> (the profile's asset path).</summary>
        public static void CreateFromCommandLine()
        {
            string robotPath = Argument("-robot");
            string outPath = Argument("-out");
            if (string.IsNullOrEmpty(robotPath) || string.IsNullOrEmpty(outPath))
                throw new ArgumentException("Usage: -robot <prefab asset path> -out <profile asset path>");

            var robot = AssetDatabase.LoadAssetAtPath<GameObject>(robotPath);
            if (robot == null) throw new ArgumentException($"No prefab at '{robotPath}'.");

            RobotProfile profile = Create(robot, outPath);
            Debug.Log($"[RobotProfileCreator] Wrote {outPath}: {profile.joints.Length} joints.");
        }

        /// <summary>
        /// Write the profile of <paramref name="robotRoot"/> to <paramref name="assetPath"/>. An
        /// existing profile there is overwritten in place, so references to it stay valid.
        /// </summary>
        public static RobotProfile Create(GameObject robotRoot, string assetPath)
        {
            RobotProfile built = Build(robotRoot);
            var existing = AssetDatabase.LoadAssetAtPath<RobotProfile>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                existing.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }

            AssetDatabase.CreateAsset(built, assetPath);
            AssetDatabase.SaveAssets();
            return built;
        }

        /// <summary>The profile of an imported robot, in memory.</summary>
        public static RobotProfile Build(GameObject robotRoot)
        {
            if (robotRoot == null) throw new ArgumentNullException(nameof(robotRoot));

            var profile = ScriptableObject.CreateInstance<RobotProfile>();
            profile.displayName = robotRoot.name;

            var joints = new List<JointSpec>();
            var bodies = new List<ArticulationBody>();
            foreach (MonoBehaviour component in robotRoot.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!IsA(component, UrdfJointType)) continue;
                ArticulationBody body = component.GetComponent<ArticulationBody>();
                if (body == null) continue;

                bool revolute = body.jointType == ArticulationJointType.RevoluteJoint;
                bool prismatic = body.jointType == ArticulationJointType.PrismaticJoint;
                if (!revolute && !prismatic) continue;

                ArticulationDrive drive = body.xDrive;
                bool continuous = revolute && IsA(component, UrdfContinuousType);
                // Drive limits are degrees for a revolute joint and metres for a prismatic one.
                float toPosition = revolute ? Mathf.Deg2Rad : 1f;
                string jointName = Field<string>(component, "jointName");

                var joint = new JointSpec
                {
                    name = string.IsNullOrWhiteSpace(jointName) ? component.name : jointName,
                    childLink = component.name,
                    kind = continuous ? JointKind.Continuous : revolute ? JointKind.Revolute : JointKind.Prismatic,
                    urdfLower = continuous ? 0f : drive.lowerLimit * toPosition,
                    urdfUpper = continuous ? 0f : drive.upperLimit * toPosition,
                    // The importer puts the URDF velocity limit on the body, but Unity does not
                    // save it: after a reload this reads 0 and the limit has to be typed in.
                    maxVelocity = revolute ? body.maxAngularVelocity : body.maxLinearVelocity,
                };
                if (Field<bool>(component, "mimic"))
                {
                    joint.mimic = new MimicSpec
                    {
                        leader = Field<string>(component, "mimicJointName") ?? "",
                        multiplier = (float)Field<double>(component, "mimicMultiplier"),
                        offset = (float)Field<double>(component, "mimicOffset"),
                    };
                }
                joints.Add(joint);
                bodies.Add(body);
            }

            // A prismatic joint with no revolute joint below it is a gripper finger; a revolute
            // joint belongs to the arm. A revolute gripper has to be marked by hand.
            Transform lastArmLink = null;
            for (int i = 0; i < joints.Count; i++)
            {
                bool finger = joints[i].kind == JointKind.Prismatic && !HasRevoluteBelow(bodies[i]);
                joints[i].role = finger ? JointRole.Gripper
                    : joints[i].kind == JointKind.Prismatic ? JointRole.Passive
                    : JointRole.Arm;
                if (joints[i].role == JointRole.Arm) lastArmLink = bodies[i].transform;
            }
            profile.joints = joints.ToArray();

            ArticulationBody root = robotRoot.GetComponentInChildren<ArticulationBody>(true);
            profile.baseLink = root != null ? root.name : "";
            profile.endEffectorLink = lastArmLink != null ? lastArmLink.name : "";
            profile.gripper = GuessGripper(joints);

            // A prefab (or an instance of one) can be shown as the robot's ghost as it is.
            GameObject source = PrefabUtility.IsPartOfPrefabAsset(robotRoot)
                ? robotRoot
                : PrefabUtility.GetCorrespondingObjectFromSource(robotRoot);
            profile.ghostPrefab = source;
            return profile;
        }

        // The first finger that follows no other is the driver; a prismatic gripper opens to the
        // fingers' combined travel.
        private static GripperSpec GuessGripper(List<JointSpec> joints)
        {
            var gripper = new GripperSpec();
            JointSpec driver = joints.Find(j => j.role == JointRole.Gripper && !j.IsMimic);
            if (driver == null) return gripper;

            gripper.kind = GripperKind.Prismatic;
            gripper.driverJoint = driver.name;
            gripper.open = driver.urdfUpper;
            gripper.closed = driver.urdfLower;
            foreach (JointSpec joint in joints)
                if (joint.role == JointRole.Gripper) gripper.openWidthMetres += joint.urdfUpper - joint.urdfLower;
            return gripper;
        }

        private static bool HasRevoluteBelow(ArticulationBody body)
        {
            foreach (ArticulationBody child in body.GetComponentsInChildren<ArticulationBody>(true))
                if (child != body && child.jointType == ArticulationJointType.RevoluteJoint) return true;
            return false;
        }

        private static bool IsA(MonoBehaviour component, string typeName)
        {
            for (Type t = component != null ? component.GetType() : null; t != null; t = t.BaseType)
                if (t.FullName == typeName) return true;
            return false;
        }

        private static T Field<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field != null && field.GetValue(target) is T value ? value : default;
        }

        private static string Argument(string flag)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
