using System.Linq;
using NUnit.Framework;
using RosMessageTypes.Std;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// The namespacing decorator, observed through <see cref="FakeRosBus"/>: what reaches the
    /// transport is always the final absolute name.
    /// </summary>
    public class NamespacedRosBusTests
    {
        private FakeRosBus fake;
        private IRosBus bus;

        [SetUp]
        public void SetUp()
        {
            fake = new FakeRosBus();
            bus = fake.InNamespace("fr3");
        }

        [Test]
        public void Subscribe_ListensOnTheResolvedTopic_AndUnsubscribeReleasesIt()
        {
            int received = 0;
            System.Action<StringMsg> callback = _ => received++;

            bus.Subscribe("joint_states", callback);
            CollectionAssert.AreEqual(new[] { "/fr3/joint_states" }, fake.Subscriptions);

            fake.Inbound("/fr3/joint_states", new StringMsg("a"));
            fake.Inbound("/joint_states", new StringMsg("not ours"));
            Assert.AreEqual(1, received);

            bus.Unsubscribe("joint_states", callback);
            fake.Inbound("/fr3/joint_states", new StringMsg("b"));
            Assert.AreEqual(1, received);
        }

        [Test]
        public void Publishers_AreRegisteredOnTheResolvedTopic()
        {
            bus.RegisterPublisher<StringMsg>("status");
            bus.RegisterPublisher<StringMsg>("collision_object", 512);

            CollectionAssert.AreEqual(new[] { "/fr3/status", "/fr3/collision_object" }, fake.RegisteredPublishers);
        }

        [Test]
        public void Publish_SendsOnTheResolvedTopic()
        {
            var first = new StringMsg("a");
            var second = new StringMsg("b");

            bus.Publish("status", first);
            bus.Publish("status", second, true);

            CollectionAssert.AreEqual(new[] { "/fr3/status", "/fr3/status" }, fake.Publishes.Select(p => p.Topic).ToList());
            CollectionAssert.AreEqual(new[] { first, second }, fake.PublishedOn("/fr3/status").ToList());
        }

        [Test]
        public void Services_AreRegisteredAndCalledOnTheResolvedName()
        {
            fake.SetServiceHandler("/fr3/get_planning_scene", request => new StringMsg("from " + ((StringMsg)request).data));
            string answer = null;

            bus.RegisterRosService<StringMsg, StringMsg>("get_planning_scene");
            bus.SendServiceMessage<StringMsg>("get_planning_scene", new StringMsg("unity"), response => answer = response.data);

            CollectionAssert.AreEqual(new[] { "/fr3/get_planning_scene" }, fake.RegisteredServices);
            Assert.AreEqual("from unity", answer);
        }

        [Test]
        public void ActionClients_AreRegisteredOnTheResolvedName()
        {
            var client = bus.RegisterActionClient<StringMsg, StringMsg, StringMsg>("pick_place");

            Assert.AreSame(client, fake.ActionClient<StringMsg, StringMsg, StringMsg>("/fr3/pick_place"));
        }

        [Test]
        public void AbsoluteNames_PassThroughUnchanged()
        {
            fake.SetServiceHandler("/get_planning_scene", request => new StringMsg("global"));
            string answer = null;

            bus.Subscribe<StringMsg>("/tf", _ => { });
            bus.RegisterPublisher<StringMsg>("/collision_object");
            bus.Publish("/collision_object", new StringMsg("a"));
            bus.RegisterRosService<StringMsg, StringMsg>("/get_planning_scene");
            bus.SendServiceMessage<StringMsg>("/get_planning_scene", new StringMsg("unity"), response => answer = response.data);
            var client = bus.RegisterActionClient<StringMsg, StringMsg, StringMsg>("/pick_place");

            CollectionAssert.AreEqual(new[] { "/tf" }, fake.Subscriptions);
            CollectionAssert.AreEqual(new[] { "/collision_object" }, fake.RegisteredPublishers);
            Assert.AreEqual(1, fake.CountOn("/collision_object"));
            CollectionAssert.AreEqual(new[] { "/get_planning_scene" }, fake.RegisteredServices);
            Assert.AreEqual("global", answer);
            Assert.AreSame(client, fake.ActionClient<StringMsg, StringMsg, StringMsg>("/pick_place"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase("/")]
        public void InNamespace_WithNoNamespace_IsTheBusItself(string rosNamespace)
        {
            Assert.AreSame(fake, fake.InNamespace(rosNamespace));
            Assert.AreSame(bus, bus.InNamespace(rosNamespace));
        }

        [Test]
        public void InNamespace_AppliedTwice_NestsTheNamespaces()
        {
            IRosBus arm = fake.InNamespace("left").InNamespace("/arm/");

            arm.Subscribe<StringMsg>("joint_states", _ => { });
            arm.RegisterPublisher<StringMsg>("status");
            arm.Publish("status", new StringMsg("a"));
            arm.RegisterRosService<StringMsg, StringMsg>("compute_ik");
            var client = arm.RegisterActionClient<StringMsg, StringMsg, StringMsg>("pick_place");
            arm.Publish("/tf", new StringMsg("b"));

            CollectionAssert.AreEqual(new[] { "/left/arm/joint_states" }, fake.Subscriptions);
            CollectionAssert.AreEqual(new[] { "/left/arm/status" }, fake.RegisteredPublishers);
            CollectionAssert.AreEqual(new[] { "/left/arm/status", "/tf" }, fake.Publishes.Select(p => p.Topic).ToList());
            CollectionAssert.AreEqual(new[] { "/left/arm/compute_ik" }, fake.RegisteredServices);
            Assert.AreSame(client, fake.ActionClient<StringMsg, StringMsg, StringMsg>("/left/arm/pick_place"));
        }

        [Test]
        public void ANamespacedBus_OverANamespacedBus_KeepsOneLayerOverTheTransport()
        {
            var nested = new NamespacedRosBus(new NamespacedRosBus(fake, "left"), "arm");

            Assert.AreEqual("/left/arm", nested.Namespace);
            Assert.AreSame(fake, nested.Inner);
            Assert.AreEqual("/left/arm/joint_states", nested.Resolve("joint_states"));
            Assert.AreEqual("/joint_states", nested.Resolve("/joint_states"));
        }

        [Test]
        public void ConnectionState_IsTheInnerBusState()
        {
            fake.HasConnectionThread = false;
            fake.HasConnectionError = true;
            Assert.IsFalse(bus.HasConnectionThread);
            Assert.IsTrue(bus.HasConnectionError);

            fake.HasConnectionThread = true;
            fake.HasConnectionError = false;
            Assert.IsTrue(bus.HasConnectionThread);
            Assert.IsFalse(bus.HasConnectionError);
        }
    }
}
