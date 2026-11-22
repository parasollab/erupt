using System;
using System.Text;

namespace Erupt.Ros
{
    /// <summary>
    /// ROS graph-name resolution for topics, services and actions: a name that starts with
    /// <c>/</c> is absolute and is used as written; any other name is relative and is placed
    /// under a namespace.
    /// </summary>
    /// <remarks>
    /// Private names (<c>~name</c>) are not resolved: Unity is not a ROS node, so there is no
    /// node name to expand them with. They are treated like any other relative name.
    /// </remarks>
    public static class RosName
    {
        public const char Separator = '/';

        /// <summary>True when <paramref name="name"/> starts with <c>/</c>.</summary>
        public static bool IsAbsolute(string name) =>
            !string.IsNullOrEmpty(name) && name[0] == Separator;

        /// <summary>
        /// The canonical form of a namespace: <c>""</c> for none, otherwise <c>/a/b</c> with one
        /// leading slash, no trailing slash and no empty segments. Leading and trailing slashes
        /// and surrounding whitespace in the input are tolerated, so <c>left/arm</c>,
        /// <c>/left/arm/</c> and <c> left//arm </c> all give <c>/left/arm</c>.
        /// </summary>
        public static string Normalize(string rosNamespace)
        {
            if (string.IsNullOrWhiteSpace(rosNamespace)) return string.Empty;
            var result = new StringBuilder(rosNamespace.Length + 1);
            AppendSegments(result, rosNamespace);
            return result.ToString();
        }

        /// <summary>
        /// The namespace <paramref name="child"/> nested inside <paramref name="parent"/>:
        /// <c>Combine("left", "arm")</c> is <c>/left/arm</c>. Either may be empty.
        /// </summary>
        public static string Combine(string parent, string child) =>
            Normalize(parent) + Normalize(child);

        /// <summary>
        /// The absolute name <paramref name="name"/> refers to from inside
        /// <paramref name="rosNamespace"/>. An absolute name is returned unchanged; a relative
        /// one becomes <c>/namespace/name</c>, or <c>/name</c> when the namespace is empty.
        /// The composed result never contains <c>//</c>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or has no segment.</exception>
        public static string Resolve(string rosNamespace, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A ROS name must not be empty.", nameof(name));
            if (IsAbsolute(name)) return name;

            var result = new StringBuilder(Normalize(rosNamespace), (rosNamespace?.Length ?? 0) + name.Length + 2);
            int before = result.Length;
            AppendSegments(result, name);
            if (result.Length == before)
                throw new ArgumentException($"'{name}' is not a ROS name.", nameof(name));
            return result.ToString();
        }

        // Appends "/segment" for every non-empty, trimmed segment of a slash-separated path.
        private static void AppendSegments(StringBuilder into, string path)
        {
            foreach (string part in path.Split(Separator))
            {
                string segment = part.Trim();
                if (segment.Length == 0) continue;
                into.Append(Separator).Append(segment);
            }
        }
    }
}
