using System;
using NUnit.Framework;

namespace Erupt.Ros.Tests
{
    /// <summary>ROS name resolution: absolute names pass through, relative names land under the namespace.</summary>
    public class RosNameTests
    {
        [TestCase("", "joint_states", "/joint_states")]
        [TestCase(null, "joint_states", "/joint_states")]
        [TestCase("   ", "joint_states", "/joint_states")]
        [TestCase("/", "joint_states", "/joint_states")]
        [TestCase("fr3", "joint_states", "/fr3/joint_states")]
        [TestCase("/fr3", "joint_states", "/fr3/joint_states")]
        [TestCase("fr3/", "joint_states", "/fr3/joint_states")]
        [TestCase("/fr3/", "joint_states", "/fr3/joint_states")]
        [TestCase(" fr3 ", "joint_states", "/fr3/joint_states")]
        [TestCase("left/arm", "joint_states", "/left/arm/joint_states")]
        [TestCase("/left/arm/", "joint_states", "/left/arm/joint_states")]
        [TestCase("left//arm", "joint_states", "/left/arm/joint_states")]
        [TestCase("fr3", "pick_place/description", "/fr3/pick_place/description")]
        [TestCase("fr3", "pick_place//description/", "/fr3/pick_place/description")]
        [TestCase("", "pick_place/description", "/pick_place/description")]
        public void Resolve_PlacesARelativeNameUnderTheNamespace(string rosNamespace, string name, string expected)
        {
            string resolved = RosName.Resolve(rosNamespace, name);

            Assert.AreEqual(expected, resolved);
            StringAssert.DoesNotContain("//", resolved);
        }

        [TestCase("", "/joint_states")]
        [TestCase("fr3", "/joint_states")]
        [TestCase("/left/arm/", "/tf")]
        [TestCase("fr3", "/pick_place/description")]
        [TestCase("fr3", "/")]
        public void Resolve_ReturnsAnAbsoluteNameUnchanged(string rosNamespace, string name)
        {
            Assert.AreEqual(name, RosName.Resolve(rosNamespace, name));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(" / ")]
        public void Resolve_RefusesANameWithNoSegment(string name)
        {
            Assert.Throws<ArgumentException>(() => RosName.Resolve("fr3", name));
        }

        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("  ", "")]
        [TestCase("/", "")]
        [TestCase("//", "")]
        [TestCase("fr3", "/fr3")]
        [TestCase("/fr3/", "/fr3")]
        [TestCase(" /fr3/ ", "/fr3")]
        [TestCase("left/arm", "/left/arm")]
        [TestCase("//left///arm//", "/left/arm")]
        public void Normalize_GivesOneLeadingSlash_NoTrailingSlash_NoEmptySegments(string rosNamespace, string expected)
        {
            Assert.AreEqual(expected, RosName.Normalize(rosNamespace));
        }

        [TestCase("left", "arm", "/left/arm")]
        [TestCase("/left/", "/arm/", "/left/arm")]
        [TestCase("", "arm", "/arm")]
        [TestCase("left", "", "/left")]
        [TestCase("", "", "")]
        [TestCase("cell/left", "arm/tool", "/cell/left/arm/tool")]
        public void Combine_NestsTheChildNamespaceInTheParent(string parent, string child, string expected)
        {
            Assert.AreEqual(expected, RosName.Combine(parent, child));
        }

        [Test]
        public void IsAbsolute_IsTrueOnlyForALeadingSlash()
        {
            Assert.IsTrue(RosName.IsAbsolute("/tf"));
            Assert.IsFalse(RosName.IsAbsolute("tf"));
            Assert.IsFalse(RosName.IsAbsolute("left/tf"));
            Assert.IsFalse(RosName.IsAbsolute(""));
            Assert.IsFalse(RosName.IsAbsolute(null));
        }
    }
}
