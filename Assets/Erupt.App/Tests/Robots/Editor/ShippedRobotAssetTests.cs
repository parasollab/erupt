using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Erupt.Editor;
using Erupt.Robot;

namespace Erupt.App.Robots.Tests
{
    /// <summary>
    /// The robot packages as assets: every shipped profile is valid and agrees with the prefab
    /// next to it, the prefab is wired, and what Robot Setup would make of the description that
    /// ships with the robot is what the profile says (so the packages are what the tool makes).
    /// </summary>
    public class ShippedRobotAssetTests
    {
        private static IEnumerable<string> ProfilePaths()
        {
            List<string> paths = AssetDatabase.FindAssets("t:RobotProfile", new[] { "Packages" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Packages/com.erupt.robot."))
                .OrderBy(path => path, System.StringComparer.Ordinal).ToList();
            if (paths.Count == 0) paths.Add("");
            return paths;
        }

        [Test]
        public void TheProfile_IsValid_AndMatchesThePrefab([ValueSource(nameof(ProfilePaths))] string profilePath)
        {
            Assume.That(!string.IsNullOrEmpty(profilePath), "No robot package (com.erupt.robot.*) is in this project.");
            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(profilePath);
            string basePath = profilePath.Substring(0, profilePath.Length - ".asset".Length);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePath + ".prefab");
            var ghost = AssetDatabase.LoadAssetAtPath<GameObject>(basePath + "Ghost.prefab");
            Assert.IsNotNull(prefab, $"{basePath}.prefab");
            Assert.IsNotNull(ghost, $"{basePath}Ghost.prefab");
            Assert.IsTrue(File.Exists(basePath + ".setup.md"), "The setup report ships with the robot.");

            CollectionAssert.IsEmpty(profile.Validate());
            var instance = prefab.GetComponent<RobotInstance>();
            Assert.IsNotNull(instance, "The prefab has a RobotInstance on its root.");
            Assert.AreSame(profile, instance.Profile);
            CollectionAssert.IsEmpty(instance.Validate(), "Every joint and link of the profile exists under the prefab.");
            Assert.AreSame(ghost, profile.ghostPrefab);
            Assert.IsEmpty(ghost.GetComponentsInChildren<MonoBehaviour>(true), "A ghost is the bare model.");
            Assert.IsEmpty(ghost.GetComponentsInChildren<Collider>(true), "A ghost has no colliders.");
            Assert.AreEqual("", instance.Namespace, "A shipped robot is in the root namespace until a scene says otherwise.");
            Assert.IsNotNull(prefab.transform.Find(RobotSetup.PlanningFrameObjectName), "The prefab carries its planning-frame anchor.");

            // The joints as the older profile creator reads them off the imported robot.
            RobotProfile read = RobotProfileCreator.Build(prefab);
            try
            {
                CollectionAssert.AreEquivalent(read.joints.Select(j => j.name), profile.joints.Select(j => j.name));
                foreach (JointSpec imported in read.joints)
                {
                    Assert.IsTrue(profile.TryGetJoint(imported.name, out JointSpec joint));
                    Assert.AreEqual(imported.childLink, joint.childLink, imported.name);
                    Assert.AreEqual(imported.kind, joint.kind, imported.name);
                    if (joint.kind == JointKind.Continuous) continue;
                    Assert.AreEqual(imported.urdfLower, joint.urdfLower, 1e-3f, imported.name);
                    Assert.AreEqual(imported.urdfUpper, joint.urdfUpper, 1e-3f, imported.name);
                }
            }
            finally
            {
                Object.DestroyImmediate(read);
            }
        }

        [Test]
        public void TheDescriptionThatShipsWithTheRobot_GivesTheProfile([ValueSource(nameof(ProfilePaths))] string profilePath)
        {
            Assume.That(!string.IsNullOrEmpty(profilePath), "No robot package (com.erupt.robot.*) is in this project.");
            var profile = AssetDatabase.LoadAssetAtPath<RobotProfile>(profilePath);
            string folder = Path.GetDirectoryName(profilePath).Replace('\\', '/');

            // The description is next to the profile: the ROS-names file whose arm group is the profile's.
            string rosNames = Directory.GetFiles(folder, "*.ros.json")
                .FirstOrDefault(path => RosNamesFile.Parse(File.ReadAllText(path)).arm_group == profile.armGroup &&
                                        File.ReadAllText(path.Replace(".ros.json", ".urdf")).Contains($"\"{profile.joints[0].name}\"") &&
                                        UrdfModel.Parse(File.ReadAllText(path.Replace(".ros.json", ".urdf"))).Joints.Count(j => j.IsMovable) == profile.joints.Length &&
                                        SameLimits(UrdfModel.Parse(File.ReadAllText(path.Replace(".ros.json", ".urdf"))), profile));
            Assert.IsNotNull(rosNames, $"No description (<robot>.ros.json with its .urdf) next to {profilePath} matches the profile.");
            string basePath = rosNames.Substring(0, rosNames.Length - ".ros.json".Length);

            var report = new RobotSetupReport();
            ResolvedRobot robot = RobotSetup.Describe(new RobotSetupSettings
            {
                urdfPath = basePath + ".urdf", srdfPath = basePath + ".srdf", rosNamesPath = rosNames,
                displayName = profile.displayName,
                // The one value the shipped robots type in: where the handle sits.
                endEffectorLink = profile.endEffectorLink,
            }, report);

            Assert.IsNotNull(robot);
            CollectionAssert.IsEmpty(report.Texts(RobotSetupLevel.Problem));
            var again = ScriptableObject.CreateInstance<RobotProfile>();
            try
            {
                robot.ApplyTo(again);
                again.ghostPrefab = profile.ghostPrefab;
                again.names = profile.names;
                again.limitMarginRadians = profile.limitMarginRadians;
                again.limitMarginMetres = profile.limitMarginMetres;
                again.limitUserVelocity = profile.limitUserVelocity;
                Assert.AreEqual(EditorJsonUtility.ToJson(profile, true), EditorJsonUtility.ToJson(again, true).Replace($"\"m_Name\": \"\"", $"\"m_Name\": \"{profile.name}\""),
                    "Robot Setup, run on the description that ships with the robot, gives the shipped profile.");
            }
            finally
            {
                Object.DestroyImmediate(again);
            }
        }

        private static bool SameLimits(UrdfModel urdf, RobotProfile profile)
        {
            foreach (JointSpec joint in profile.joints)
            {
                if (!urdf.TryGetJoint(joint.name, out UrdfJointInfo described)) return false;
                if (joint.kind != JointKind.Continuous && described.hasRange &&
                    (Mathf.Abs(described.lower - joint.urdfLower) > 1e-5f || Mathf.Abs(described.upper - joint.urdfUpper) > 1e-5f)) return false;
                if (Mathf.Abs(described.velocity - joint.maxVelocity) > 1e-5f) return false;
            }
            return true;
        }
    }
}
