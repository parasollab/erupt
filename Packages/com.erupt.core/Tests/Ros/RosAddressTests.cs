using NUnit.Framework;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>What the user may enter as the ROS address: an IPv4 address in dotted form.</summary>
    public class RosAddressTests
    {
        [TestCase("192.168.1.48", "192.168.1.48")]
        [TestCase("127.0.0.1", "127.0.0.1")]
        [TestCase("0.0.0.0", "0.0.0.0")]
        [TestCase("255.255.255.255", "255.255.255.255")]
        [TestCase("10.0.0.5", "10.0.0.5")]
        // Leading zeros are decimal to the person typing them, not octal.
        [TestCase("010.001.000.005", "10.1.0.5")]
        [TestCase("192.168.01.9", "192.168.1.9")]
        public void AnAddress_IsAccepted_AndWrittenWithoutLeadingZeros(string entered, string expected)
        {
            Assert.IsTrue(RosAddress.TryParseIPv4(entered, out string canonical));
            Assert.AreEqual(expected, canonical);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("192.168.1")]
        [TestCase("192.168.1.")]
        [TestCase(".168.1.48")]
        [TestCase("192..1.48")]
        [TestCase("192.168.1.48.7")]
        [TestCase("192.168.1.256")]
        [TestCase("192.168.1.1000")]
        [TestCase("1234")]
        [TestCase("192.168.1.48:10000")]
        [TestCase("192.168.1.4a")]
        [TestCase(" 192.168.1.48")]
        [TestCase("-1.0.0.1")]
        [TestCase("ros.local")]
        public void AnythingElse_IsNot(string entered)
        {
            Assert.IsFalse(RosAddress.TryParseIPv4(entered, out string canonical));
            Assert.IsNull(canonical);
        }

        [Test]
        public void TheLongestAddress_FitsTheKeypadsLimit()
        {
            Assert.AreEqual("255.255.255.255".Length, RosAddress.MaxLength);
        }

        [Test]
        public void TheEndpoint_IsFoundThroughANamespacedBus_AndNotOnABusWithoutOne()
        {
            var bus = new FakeRosBus();
            Assert.AreSame(bus, bus.Endpoint());
            Assert.AreSame(bus, bus.Root.Endpoint(), "A robot's view of the bus leads to the same endpoint.");
            Assert.AreSame(bus, bus.Scoped("left").Scoped("arm").Endpoint());
            Assert.IsNull(((IRosBus)null).Endpoint());

            bus.ConnectTo("10.0.0.7", 10001);
            Assert.AreEqual("10.0.0.7", bus.Host);
            Assert.AreEqual(10001, bus.Port);
            Assert.AreEqual("127.0.0.1", bus.DefaultHost, "What the build carries does not change with it.");
            CollectionAssert.AreEqual(new[] { ("10.0.0.7", 10001) }, bus.ConnectCalls);
        }
    }
}
