using System;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace Erupt.Ros
{
    /// <summary>
    /// An <see cref="IRosBus"/> seen from inside a ROS namespace: every topic, service and
    /// action name is resolved with <see cref="RosName.Resolve"/> before it reaches the inner
    /// bus, so relative names land under the namespace and absolute names pass through as
    /// written.
    /// </summary>
    /// <remarks>
    /// Wrapping a namespaced bus nests the namespaces (<c>left</c> then <c>arm</c> is
    /// <c>/left/arm</c>) and keeps a single layer over the undecorated bus, so names are
    /// resolved exactly once.
    /// </remarks>
    public sealed class NamespacedRosBus : IRosBus
    {
        /// <summary>The undecorated bus the resolved names are sent to.</summary>
        public IRosBus Inner { get; }

        /// <summary>The namespace in <see cref="RosName.Normalize"/> form (<c>""</c> or <c>/a/b</c>).</summary>
        public string Namespace { get; }

        public NamespacedRosBus(IRosBus inner, string rosNamespace)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (inner is NamespacedRosBus outer)
            {
                Inner = outer.Inner;
                Namespace = RosName.Combine(outer.Namespace, rosNamespace);
            }
            else
            {
                Inner = inner;
                Namespace = RosName.Normalize(rosNamespace);
            }
        }

        /// <summary>The absolute name <paramref name="name"/> refers to on this bus.</summary>
        public string Resolve(string name) => RosName.Resolve(Namespace, name);

        public bool HasConnectionThread => Inner.HasConnectionThread;

        public bool HasConnectionError => Inner.HasConnectionError;

        public void Subscribe<T>(string topic, Action<T> callback) where T : Message =>
            Inner.Subscribe(Resolve(topic), callback);

        public void Unsubscribe<T>(string topic, Action<T> callback) where T : Message =>
            Inner.Unsubscribe(Resolve(topic), callback);

        public void RegisterPublisher<T>(string topic) where T : Message =>
            Inner.RegisterPublisher<T>(Resolve(topic));

        public void RegisterPublisher<T>(string topic, int queueSize) where T : Message =>
            Inner.RegisterPublisher<T>(Resolve(topic), queueSize);

        public void RegisterRosService<TRequest, TResponse>(string serviceName)
            where TRequest : Message where TResponse : Message =>
            Inner.RegisterRosService<TRequest, TResponse>(Resolve(serviceName));

        public void SendServiceMessage<TResponse>(string serviceName, Message request, Action<TResponse> callback)
            where TResponse : Message, new() =>
            Inner.SendServiceMessage(Resolve(serviceName), request, callback);

        public IRosActionClient<TGoal, TResult, TFeedback>
            RegisterActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message =>
            Inner.RegisterActionClient<TGoal, TResult, TFeedback>(Resolve(actionName));

        public void Publish(string topic, Message message) =>
            Inner.Publish(Resolve(topic), message);

        public void Publish(string topic, Message message, bool useTrailingPad) =>
            Inner.Publish(Resolve(topic), message, useTrailingPad);
    }

    public static class RosBusNamespaceExtensions
    {
        /// <summary>
        /// This bus seen from inside <paramref name="rosNamespace"/>. An empty namespace returns
        /// the bus itself; applying it to an already namespaced bus nests the namespaces.
        /// </summary>
        public static IRosBus InNamespace(this IRosBus bus, string rosNamespace)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            return RosName.Normalize(rosNamespace).Length == 0 ? bus : new NamespacedRosBus(bus, rosNamespace);
        }

        /// <summary>
        /// A bus that resolves every name against <paramref name="rosNamespace"/>, the root
        /// namespace included: with an empty namespace a relative name such as
        /// <c>joint_states</c> still goes out as <c>/joint_states</c>. This is the bus for
        /// code that only uses relative names (everything that belongs to one robot);
        /// <see cref="InNamespace"/> leaves names untouched when there is no namespace.
        /// </summary>
        public static IRosBus Scoped(this IRosBus bus, string rosNamespace)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            return new NamespacedRosBus(bus, rosNamespace);
        }

        /// <summary>
        /// The name <paramref name="name"/> goes out as on this bus: resolved under the bus's
        /// namespace when it has one, otherwise as written.
        /// </summary>
        public static string ResolveName(this IRosBus bus, string name) =>
            bus is NamespacedRosBus scoped ? scoped.Resolve(name) : name;
    }
}
