using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;

namespace Erupt.Editor
{
    public enum UrdfJointType { Fixed, Revolute, Continuous, Prismatic, Floating, Planar, Unknown }

    /// <summary>A URDF <c>&lt;mimic&gt;</c> rule: <c>position = leader * multiplier + offset</c>.</summary>
    public sealed class UrdfMimicInfo
    {
        public string joint = "";
        public float multiplier = 1f;
        public float offset;
    }

    /// <summary>One <c>&lt;joint&gt;</c> of a URDF, in ROS units and ROS axes.</summary>
    public sealed class UrdfJointInfo
    {
        public string name = "";
        public string typeName = "";
        public UrdfJointType type = UrdfJointType.Unknown;
        public string parent = "";
        public string child = "";
        public Vector3 xyz;
        public Vector3 rpy;
        public Vector3 axis = Vector3.right;
        /// <summary>The joint has a <c>&lt;limit&gt;</c> element.</summary>
        public bool hasLimitElement;
        /// <summary>The limit element gives both <c>lower</c> and <c>upper</c>.</summary>
        public bool hasRange;
        public float lower;
        public float upper;
        /// <summary>rad/s or m/s; 0 when the URDF gives none.</summary>
        public float velocity;
        public UrdfMimicInfo mimic;

        public bool IsMovable =>
            type == UrdfJointType.Revolute || type == UrdfJointType.Continuous || type == UrdfJointType.Prismatic;
        public bool IsMimic => mimic != null && !string.IsNullOrEmpty(mimic.joint);
    }

    /// <summary>
    /// What Robot Setup needs from a URDF, read from the XML itself rather than from the Unity
    /// import: the link tree, every joint with its kind, limits, velocity limit and mimic rule,
    /// and the mesh files the links name. Positions can be computed for any joint values
    /// (<see cref="LinkPosition"/>), which is how the gripper's opening is measured.
    /// </summary>
    public sealed class UrdfModel
    {
        public string RobotName { get; private set; } = "";
        public List<string> Links { get; } = new List<string>();
        public List<UrdfJointInfo> Joints { get; } = new List<UrdfJointInfo>();
        /// <summary>Mesh and texture references as written in the URDF, without duplicates.</summary>
        public List<string> MeshFiles { get; } = new List<string>();
        /// <summary>What is wrong with the file itself; a URDF with problems may still be usable.</summary>
        public List<string> Problems { get; } = new List<string>();
        /// <summary>The link no joint has as its child; empty when the tree has none or several.</summary>
        public string RootLink { get; private set; } = "";

        private readonly Dictionary<string, UrdfJointInfo> jointByName = new Dictionary<string, UrdfJointInfo>();
        private readonly Dictionary<string, UrdfJointInfo> parentJointOfLink = new Dictionary<string, UrdfJointInfo>();
        private readonly Dictionary<string, List<UrdfJointInfo>> childJointsOfLink = new Dictionary<string, List<UrdfJointInfo>>();

        /// <exception cref="FormatException">The text is not a URDF (no <c>&lt;robot&gt;</c> root, or not XML).</exception>
        public static UrdfModel Parse(string xml)
        {
            var document = new XmlDocument();
            try { document.LoadXml(xml); }
            catch (XmlException e) { throw new FormatException("The URDF is not valid XML: " + e.Message, e); }

            XmlElement robot = document.DocumentElement;
            if (robot == null || robot.Name != "robot")
                throw new FormatException("The URDF has no <robot> root element. A .xacro file has to be expanded first (xacro file.urdf.xacro > file.urdf).");

            var model = new UrdfModel { RobotName = robot.GetAttribute("name") };
            var seenMeshes = new HashSet<string>();

            foreach (XmlNode node in robot.ChildNodes)
            {
                if (!(node is XmlElement element)) continue;
                if (element.Name == "link")
                {
                    string name = element.GetAttribute("name");
                    if (string.IsNullOrEmpty(name)) { model.Problems.Add("A link has no name."); continue; }
                    if (model.Links.Contains(name)) { model.Problems.Add($"Link '{name}' is defined more than once."); continue; }
                    model.Links.Add(name);
                    foreach (XmlElement mesh in element.GetElementsByTagName("mesh"))
                    {
                        string file = mesh.GetAttribute("filename");
                        if (!string.IsNullOrEmpty(file) && seenMeshes.Add(file)) model.MeshFiles.Add(file);
                    }
                }
                else if (element.Name == "joint")
                {
                    UrdfJointInfo joint = ParseJoint(element, model.Problems);
                    if (joint == null) continue;
                    if (model.jointByName.ContainsKey(joint.name)) { model.Problems.Add($"Joint '{joint.name}' is defined more than once."); continue; }
                    model.Joints.Add(joint);
                    model.jointByName[joint.name] = joint;
                }
                else if (element.Name.StartsWith("xacro:", StringComparison.Ordinal))
                {
                    model.Problems.Add($"The URDF still contains xacro (<{element.Name}>); expand it first (xacro file.urdf.xacro > file.urdf).");
                }
            }

            foreach (UrdfJointInfo joint in model.Joints)
            {
                if (!model.Links.Contains(joint.parent)) model.Problems.Add($"Joint '{joint.name}' has the parent link '{joint.parent}', which the URDF does not define.");
                if (!model.Links.Contains(joint.child)) model.Problems.Add($"Joint '{joint.name}' has the child link '{joint.child}', which the URDF does not define.");
                if (model.parentJointOfLink.ContainsKey(joint.child))
                    model.Problems.Add($"Link '{joint.child}' is the child of more than one joint.");
                else
                    model.parentJointOfLink[joint.child] = joint;
                if (!model.childJointsOfLink.TryGetValue(joint.parent, out List<UrdfJointInfo> children))
                    model.childJointsOfLink[joint.parent] = children = new List<UrdfJointInfo>();
                children.Add(joint);
            }

            foreach (UrdfJointInfo joint in model.Joints)
            {
                if (!joint.IsMimic) continue;
                if (joint.mimic.joint == joint.name) model.Problems.Add($"Joint '{joint.name}' mimics itself.");
                else if (!model.jointByName.ContainsKey(joint.mimic.joint)) model.Problems.Add($"Joint '{joint.name}' mimics '{joint.mimic.joint}', which the URDF does not define.");
            }

            var roots = model.Links.FindAll(link => !model.parentJointOfLink.ContainsKey(link));
            if (roots.Count == 1) model.RootLink = roots[0];
            else if (roots.Count == 0) model.Problems.Add("The URDF has no root link (every link is the child of a joint).");
            else model.Problems.Add($"The URDF has {roots.Count} root links ({string.Join(", ", roots)}); a robot has one.");

            return model;
        }

        private static UrdfJointInfo ParseJoint(XmlElement element, List<string> problems)
        {
            string name = element.GetAttribute("name");
            if (string.IsNullOrEmpty(name)) { problems.Add("A joint has no name."); return null; }

            var joint = new UrdfJointInfo { name = name, typeName = element.GetAttribute("type") };
            switch (joint.typeName)
            {
                case "fixed": joint.type = UrdfJointType.Fixed; break;
                case "revolute": joint.type = UrdfJointType.Revolute; break;
                case "continuous": joint.type = UrdfJointType.Continuous; break;
                case "prismatic": joint.type = UrdfJointType.Prismatic; break;
                case "floating": joint.type = UrdfJointType.Floating; break;
                case "planar": joint.type = UrdfJointType.Planar; break;
                default:
                    joint.type = UrdfJointType.Unknown;
                    problems.Add($"Joint '{name}' has the unknown type '{joint.typeName}'.");
                    break;
            }

            foreach (XmlNode node in element.ChildNodes)
            {
                if (!(node is XmlElement child)) continue;
                switch (child.Name)
                {
                    case "parent": joint.parent = child.GetAttribute("link"); break;
                    case "child": joint.child = child.GetAttribute("link"); break;
                    case "origin":
                        joint.xyz = Vector(child.GetAttribute("xyz"), Vector3.zero);
                        joint.rpy = Vector(child.GetAttribute("rpy"), Vector3.zero);
                        break;
                    case "axis":
                        joint.axis = Vector(child.GetAttribute("xyz"), Vector3.right);
                        break;
                    case "limit":
                        joint.hasLimitElement = true;
                        bool hasLower = Number(child.GetAttribute("lower"), out joint.lower);
                        bool hasUpper = Number(child.GetAttribute("upper"), out joint.upper);
                        joint.hasRange = hasLower && hasUpper;
                        Number(child.GetAttribute("velocity"), out joint.velocity);
                        break;
                    case "mimic":
                        joint.mimic = new UrdfMimicInfo { joint = child.GetAttribute("joint") };
                        if (Number(child.GetAttribute("multiplier"), out float multiplier)) joint.mimic.multiplier = multiplier;
                        if (Number(child.GetAttribute("offset"), out float offset)) joint.mimic.offset = offset;
                        break;
                }
            }

            if (joint.axis.sqrMagnitude < 1e-12f) joint.axis = Vector3.right;
            else joint.axis.Normalize();
            if (joint.velocity < 0f) joint.velocity = 0f;
            return joint;
        }

        public bool TryGetJoint(string name, out UrdfJointInfo joint)
        {
            joint = null;
            return !string.IsNullOrEmpty(name) && jointByName.TryGetValue(name, out joint);
        }

        public bool HasLink(string name) => !string.IsNullOrEmpty(name) && Links.Contains(name);

        /// <summary>The joint that connects a link to its parent; null for the root link.</summary>
        public UrdfJointInfo ParentJointOf(string link) =>
            !string.IsNullOrEmpty(link) && parentJointOfLink.TryGetValue(link, out UrdfJointInfo joint) ? joint : null;

        /// <summary>The joints that start at a link.</summary>
        public IReadOnlyList<UrdfJointInfo> ChildJointsOf(string link) =>
            !string.IsNullOrEmpty(link) && childJointsOfLink.TryGetValue(link, out List<UrdfJointInfo> joints)
                ? joints : (IReadOnlyList<UrdfJointInfo>)Array.Empty<UrdfJointInfo>();

        /// <summary>The joints between the root link and <paramref name="link"/>, root first.</summary>
        public List<UrdfJointInfo> JointsTo(string link)
        {
            var path = new List<UrdfJointInfo>();
            for (int guard = 0; guard <= Joints.Count; guard++)
            {
                UrdfJointInfo joint = ParentJointOf(link);
                if (joint == null) break;
                path.Add(joint);
                link = joint.parent;
            }
            path.Reverse();
            return path;
        }

        /// <summary>Whether <paramref name="link"/> is <paramref name="ancestor"/> or below it.</summary>
        public bool IsAtOrBelow(string link, string ancestor)
        {
            for (int guard = 0; guard <= Joints.Count && !string.IsNullOrEmpty(link); guard++)
            {
                if (link == ancestor) return true;
                UrdfJointInfo joint = ParentJointOf(link);
                if (joint == null) return false;
                link = joint.parent;
            }
            return false;
        }

        /// <summary>Every link at or below <paramref name="link"/>.</summary>
        public List<string> Subtree(string link)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            if (HasLink(link)) pending.Push(link);
            while (pending.Count > 0 && result.Count <= Links.Count)
            {
                string current = pending.Pop();
                result.Add(current);
                foreach (UrdfJointInfo joint in ChildJointsOf(current)) pending.Push(joint.child);
            }
            return result;
        }

        /// <summary>
        /// A link's pose in the root link's frame (ROS axes, metres) for the given joint
        /// positions; joints that are not named stand at zero.
        /// </summary>
        public Matrix4x4 LinkPose(string link, IReadOnlyDictionary<string, float> positions = null)
        {
            Matrix4x4 pose = Matrix4x4.identity;
            foreach (UrdfJointInfo joint in JointsTo(link))
            {
                pose *= Translation(joint.xyz) * RollPitchYaw(joint.rpy);
                float q = 0f;
                positions?.TryGetValue(joint.name, out q);
                if (joint.type == UrdfJointType.Revolute || joint.type == UrdfJointType.Continuous)
                    pose *= AxisAngle(joint.axis, q);
                else if (joint.type == UrdfJointType.Prismatic)
                    pose *= Translation(joint.axis * q);
            }
            return pose;
        }

        /// <summary>The origin of a link in the root link's frame (ROS axes, metres).</summary>
        public Vector3 LinkPosition(string link, IReadOnlyDictionary<string, float> positions = null)
        {
            Matrix4x4 pose = LinkPose(link, positions);
            return new Vector3(pose.m03, pose.m13, pose.m23);
        }

        /// <summary>
        /// Joint positions with every mimic joint put where its rule says, following chains of
        /// mimics. Joints the argument names keep their value unless they mimic another joint.
        /// </summary>
        public Dictionary<string, float> WithMimics(IReadOnlyDictionary<string, float> positions)
        {
            var result = new Dictionary<string, float>();
            if (positions != null)
                foreach (KeyValuePair<string, float> pair in positions) result[pair.Key] = pair.Value;

            // A chain of mimics is at most as long as the number of joints.
            for (int pass = 0; pass < Joints.Count; pass++)
            {
                bool changed = false;
                foreach (UrdfJointInfo joint in Joints)
                {
                    if (!joint.IsMimic || joint.mimic.joint == joint.name) continue;
                    result.TryGetValue(joint.mimic.joint, out float leader);
                    float value = leader * joint.mimic.multiplier + joint.mimic.offset;
                    if (result.TryGetValue(joint.name, out float current) && Mathf.Approximately(current, value)) continue;
                    result[joint.name] = value;
                    changed = true;
                }
                if (!changed) break;
            }
            return result;
        }

        // ---- maths in ROS axes (right-handed), written out so Unity's left-handed helpers are not involved ----

        private static Matrix4x4 Translation(Vector3 t)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m03 = t.x; m.m13 = t.y; m.m23 = t.z;
            return m;
        }

        // URDF fixed-axis roll, pitch, yaw: R = Rz(yaw) * Ry(pitch) * Rx(roll).
        private static Matrix4x4 RollPitchYaw(Vector3 rpy)
        {
            return AxisAngle(Vector3.forward, rpy.z) * AxisAngle(Vector3.up, rpy.y) * AxisAngle(Vector3.right, rpy.x);
        }

        // Rodrigues' formula for a right-handed rotation by angle (radians) about a unit axis.
        private static Matrix4x4 AxisAngle(Vector3 axis, float angle)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle), t = 1f - c;
            float x = axis.x, y = axis.y, z = axis.z;
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = t * x * x + c;     m.m01 = t * x * y - s * z; m.m02 = t * x * z + s * y;
            m.m10 = t * x * y + s * z; m.m11 = t * y * y + c;     m.m12 = t * y * z - s * x;
            m.m20 = t * x * z - s * y; m.m21 = t * y * z + s * x; m.m22 = t * z * z + c;
            return m;
        }

        private static bool Number(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            value = (float)parsed;
            return true;
        }

        private static Vector3 Vector(string text, Vector3 fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            string[] parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return fallback;
            return Number(parts[0], out float x) && Number(parts[1], out float y) && Number(parts[2], out float z)
                ? new Vector3(x, y, z) : fallback;
        }
    }
}
