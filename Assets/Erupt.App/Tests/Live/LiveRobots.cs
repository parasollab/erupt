using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Erupt.Robot;

namespace Erupt.App.Live.Tests
{
    /// <summary>
    /// Which robot the live suite runs with. The reference scenes hold FR3s; with
    /// <see cref="RobotVariable"/> (one arm) or <see cref="ArmsVariable"/> (several) set, the
    /// robots of the scene are replaced, as the scene loads, by the shipped prefab of the robot
    /// the ROS side was started with: the same place, id and namespace, and nothing else done to
    /// it, which is what "drop the prefab into a scene" means.
    /// </summary>
    public static class LiveRobots
    {
        /// <summary>The ROS robot behind the endpoint in the <c>single</c> and <c>offline</c> scenarios, e.g. <c>ur5e</c>. Unset = the scene's own robot.</summary>
        public const string RobotVariable = "ERUPT_LIVE_ROBOT";
        /// <summary>The arms behind the endpoint in the <c>arms</c> scenario, as the ROS side takes them: <c>left=fr3 right=ur5e third=xarm7</c>. Unset = the scene's own robots.</summary>
        public const string ArmsVariable = "ERUPT_LIVE_ARMS";

        /// <summary>The ROS robot name asked for the single arm; null when the scene's own robot is used.</summary>
        public static string SingleRobot
        {
            get
            {
                string value = (System.Environment.GetEnvironmentVariable(RobotVariable) ?? "").Trim();
                return value.Length > 0 ? value : null;
            }
        }

        /// <summary>Robot id to ROS robot name, for the arms that were asked for; empty when the scene's own robots are used.</summary>
        public static Dictionary<string, string> Arms
        {
            get
            {
                var arms = new Dictionary<string, string>();
                string value = System.Environment.GetEnvironmentVariable(ArmsVariable) ?? "";
                foreach (string entry in value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split('=');
                    if (parts.Length != 2) throw new ArgumentException($"{ArmsVariable}: '{entry}' is not id=robot.");
                    // A pose after '@' is the ROS side's business.
                    arms[parts[0].Trim()] = parts[1].Split('@')[0].Trim();
                }
                return arms;
            }
        }

        /// <summary>Replace the robots of a scene that was just loaded (before any Start has run) by the ones asked for.</summary>
        public static void Apply(Scene scene, string scenario)
        {
            Dictionary<string, string> arms = scenario == LiveRos.Arms ? Arms : new Dictionary<string, string>();
            string single = scenario == LiveRos.Arms ? null : SingleRobot;
            if (single == null && arms.Count == 0) return;

            RobotInstance[] present = UnityEngine.Object.FindObjectsByType<RobotInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(r => r.gameObject.scene == scene).ToArray();
            foreach (RobotInstance robot in present)
            {
                string wanted = single;
                if (single == null && !arms.TryGetValue(robot.Id, out wanted)) continue;
                if (robot.Profile != null && robot.Profile.rosRobotName == wanted) continue;
                Replace(robot, wanted);
            }
        }

        private static void Replace(RobotInstance robot, string rosRobot)
        {
            GameObject prefab = FindPrefab(rosRobot);
            if (prefab == null)
                throw new InvalidOperationException($"No robot prefab in the project is for the ROS robot '{rosRobot}' (a RobotProfile whose rosRobotName is '{rosRobot}', with its prefab next to it).");

            Transform old = robot.transform;
            Transform parent = old.parent;
            Vector3 position = old.position;
            Quaternion rotation = old.rotation;
            // A robot without an id of its own is known by its object's name; an object named
            // after its robot model ("fr3") takes the new model's name, any other name stays.
            bool namedByObject = robot.Id == old.name;
            bool namedAfterModel = robot.Profile != null && old.name == robot.Profile.rosRobotName;
            string id = robot.Id, rosNamespace = robot.Namespace, oldName = old.name;
            // A frame the scene assigned (shared with other robots) stays; the prefab's own anchor is replaced with the prefab.
            Transform sharedFrame = robot.PlanningFrame != null && !robot.PlanningFrame.IsChildOf(old) ? robot.PlanningFrame : null;
            UnityEngine.Object.DestroyImmediate(old.gameObject);

            var holder = new GameObject("holder");
            holder.SetActive(false);
            GameObject go = UnityEngine.Object.Instantiate(prefab, position, rotation, holder.transform);
            go.name = namedAfterModel ? rosRobot : oldName;
            var instance = go.GetComponent<RobotInstance>();
            instance.Configure(namedByObject ? "" : id, rosNamespace, instance.Profile);
            if (sharedFrame != null) instance.SetPlanningFrame(sharedFrame);
            go.transform.SetParent(parent, true);
            UnityEngine.Object.DestroyImmediate(holder);
            Debug.Log($"[live] robot '{go.name}' (namespace '{rosNamespace}') is {prefab.name}, for the ROS robot '{rosRobot}'.");
        }

        private static GameObject FindPrefab(string rosRobot)
        {
#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:RobotProfile"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<RobotProfile>(path);
                if (profile == null || profile.rosRobotName != rosRobot) continue;
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path.Substring(0, path.Length - ".asset".Length) + ".prefab");
                if (prefab != null) return prefab;
            }
#endif
            return null;
        }
    }
}
