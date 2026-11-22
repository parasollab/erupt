using UnityEngine;
using Erupt.Robot;

namespace Erupt.Plugins
{
    /// <summary>
    /// Per-robot copies of the components a plugin serializes once. A plugin prefab carries one
    /// of each of its helper components with their settings; the first robot's session uses
    /// those, and every further robot gets a copy with the same serialized settings on a child
    /// object of its own, destroyed with the session.
    /// </summary>
    public static class SessionComponents
    {
        /// <summary>
        /// A child of <paramref name="parent"/> for one robot's copies, created inactive so the
        /// components added to it wake only after they are configured; the caller activates it.
        /// </summary>
        public static GameObject CreateHost(Transform parent, string what, RobotInstance robot)
        {
            var host = new GameObject($"{what} ({(robot != null ? robot.Id : "no robot")})");
            host.SetActive(false);
            host.transform.SetParent(parent, false);
            return host;
        }

        /// <summary>
        /// Add a <typeparamref name="T"/> to <paramref name="host"/> with the serialized settings
        /// of <paramref name="template"/> (object references included); a plain new component
        /// when there is no template.
        /// </summary>
        public static T Copy<T>(T template, GameObject host) where T : MonoBehaviour
        {
            T copy = host.AddComponent<T>();
            if (template != null) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(template), copy);
            return copy;
        }
    }
}
