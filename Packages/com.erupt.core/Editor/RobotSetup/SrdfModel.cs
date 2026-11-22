using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace Erupt.Editor
{
    /// <summary>A planning group of an SRDF: joints, links, chains and other groups it is made of.</summary>
    public sealed class SrdfGroup
    {
        public string name = "";
        public readonly List<string> joints = new List<string>();
        public readonly List<string> links = new List<string>();
        /// <summary>(base link, tip link) pairs.</summary>
        public readonly List<KeyValuePair<string, string>> chains = new List<KeyValuePair<string, string>>();
        public readonly List<string> subgroups = new List<string>();
    }

    /// <summary>A named joint configuration of one group (<c>&lt;group_state&gt;</c>).</summary>
    public sealed class SrdfGroupState
    {
        public string name = "";
        public string group = "";
        public readonly List<KeyValuePair<string, float>> joints = new List<KeyValuePair<string, float>>();
    }

    public sealed class SrdfEndEffector
    {
        public string name = "";
        public string parentLink = "";
        public string group = "";
        public string parentGroup = "";
    }

    public sealed class SrdfVirtualJoint
    {
        public string name = "";
        public string type = "";
        public string parentFrame = "";
        public string childLink = "";
    }

    /// <summary>
    /// What Robot Setup reads from an SRDF: planning groups, named poses, end effectors,
    /// passive joints and virtual joints. Collision pairs are of no use to the Unity side.
    /// </summary>
    public sealed class SrdfModel
    {
        public List<SrdfGroup> Groups { get; } = new List<SrdfGroup>();
        public List<SrdfGroupState> States { get; } = new List<SrdfGroupState>();
        public List<SrdfEndEffector> EndEffectors { get; } = new List<SrdfEndEffector>();
        public List<string> PassiveJoints { get; } = new List<string>();
        public List<SrdfVirtualJoint> VirtualJoints { get; } = new List<SrdfVirtualJoint>();
        public List<string> Problems { get; } = new List<string>();

        /// <exception cref="FormatException">The text is not an SRDF.</exception>
        public static SrdfModel Parse(string xml)
        {
            var document = new XmlDocument();
            try { document.LoadXml(xml); }
            catch (XmlException e) { throw new FormatException("The SRDF is not valid XML: " + e.Message, e); }

            XmlElement robot = document.DocumentElement;
            if (robot == null || robot.Name != "robot")
                throw new FormatException("The SRDF has no <robot> root element. A .xacro file has to be expanded first.");

            var model = new SrdfModel();
            foreach (XmlNode node in robot.ChildNodes)
            {
                if (!(node is XmlElement element)) continue;
                switch (element.Name)
                {
                    case "group":
                    {
                        var group = new SrdfGroup { name = element.GetAttribute("name") };
                        foreach (XmlNode inner in element.ChildNodes)
                        {
                            if (!(inner is XmlElement part)) continue;
                            switch (part.Name)
                            {
                                case "joint": group.joints.Add(part.GetAttribute("name")); break;
                                case "link": group.links.Add(part.GetAttribute("name")); break;
                                case "chain":
                                    group.chains.Add(new KeyValuePair<string, string>(part.GetAttribute("base_link"), part.GetAttribute("tip_link")));
                                    break;
                                case "group": group.subgroups.Add(part.GetAttribute("name")); break;
                                // Some MoveIt configurations declare a passive joint inside its group.
                                case "passive_joint": model.AddPassive(part.GetAttribute("name")); break;
                            }
                        }
                        if (string.IsNullOrEmpty(group.name)) model.Problems.Add("An SRDF group has no name.");
                        else model.Groups.Add(group);
                        break;
                    }
                    case "group_state":
                    {
                        var state = new SrdfGroupState { name = element.GetAttribute("name"), group = element.GetAttribute("group") };
                        foreach (XmlNode inner in element.ChildNodes)
                        {
                            if (!(inner is XmlElement part) || part.Name != "joint") continue;
                            string joint = part.GetAttribute("name");
                            // A multi-DOF joint lists several values; ERUPT's joints have one.
                            string[] values = part.GetAttribute("value").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            if (values.Length != 1 ||
                                !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                            {
                                model.Problems.Add($"SRDF pose '{state.name}' gives '{joint}' the value '{part.GetAttribute("value")}', which is not one number; it is left out.");
                                continue;
                            }
                            state.joints.Add(new KeyValuePair<string, float>(joint, (float)value));
                        }
                        model.States.Add(state);
                        break;
                    }
                    case "end_effector":
                        model.EndEffectors.Add(new SrdfEndEffector
                        {
                            name = element.GetAttribute("name"),
                            parentLink = element.GetAttribute("parent_link"),
                            group = element.GetAttribute("group"),
                            parentGroup = element.GetAttribute("parent_group"),
                        });
                        break;
                    case "passive_joint":
                        model.AddPassive(element.GetAttribute("name"));
                        break;
                    case "virtual_joint":
                        model.VirtualJoints.Add(new SrdfVirtualJoint
                        {
                            name = element.GetAttribute("name"),
                            type = element.GetAttribute("type"),
                            parentFrame = element.GetAttribute("parent_frame"),
                            childLink = element.GetAttribute("child_link"),
                        });
                        break;
                }
            }
            return model;
        }

        private void AddPassive(string joint)
        {
            if (!string.IsNullOrEmpty(joint) && !PassiveJoints.Contains(joint)) PassiveJoints.Add(joint);
        }

        public bool TryGetGroup(string name, out SrdfGroup group)
        {
            group = string.IsNullOrEmpty(name) ? null : Groups.Find(g => g.name == name);
            return group != null;
        }

        /// <summary>
        /// Every joint of a group, fixed ones included, in the URDF's order: the joints it
        /// lists, the parent joints of its links, the joints along its chains and the joints of
        /// its subgroups.
        /// </summary>
        public List<string> JointsOf(string groupName, UrdfModel urdf)
        {
            var names = new HashSet<string>();
            Collect(groupName, urdf, names, new HashSet<string>());
            var ordered = new List<string>();
            foreach (UrdfJointInfo joint in urdf.Joints)
                if (names.Contains(joint.name)) ordered.Add(joint.name);
            return ordered;
        }

        private void Collect(string groupName, UrdfModel urdf, HashSet<string> names, HashSet<string> visited)
        {
            if (!visited.Add(groupName) || !TryGetGroup(groupName, out SrdfGroup group)) return;

            foreach (string joint in group.joints) names.Add(joint);
            foreach (string link in group.links)
            {
                UrdfJointInfo parent = urdf.ParentJointOf(link);
                if (parent != null) names.Add(parent.name);
            }
            foreach (KeyValuePair<string, string> chain in group.chains)
            {
                // From the tip up to the base link; a chain whose base is not above its tip is ignored.
                var path = new List<string>();
                string link = chain.Value;
                bool reachedBase = false;
                for (int guard = 0; guard <= urdf.Joints.Count; guard++)
                {
                    if (link == chain.Key) { reachedBase = true; break; }
                    UrdfJointInfo parent = urdf.ParentJointOf(link);
                    if (parent == null) break;
                    path.Add(parent.name);
                    link = parent.parent;
                }
                if (reachedBase) foreach (string joint in path) names.Add(joint);
            }
            foreach (string subgroup in group.subgroups) Collect(subgroup, urdf, names, visited);
        }

        /// <summary>The tip link of the first chain of a group; empty when the group has no chain.</summary>
        public string ChainTipOf(string groupName) =>
            TryGetGroup(groupName, out SrdfGroup group) && group.chains.Count > 0 ? group.chains[0].Value : "";

        /// <summary>The base link of the first chain of a group; empty when the group has no chain.</summary>
        public string ChainBaseOf(string groupName) =>
            TryGetGroup(groupName, out SrdfGroup group) && group.chains.Count > 0 ? group.chains[0].Key : "";
    }
}
