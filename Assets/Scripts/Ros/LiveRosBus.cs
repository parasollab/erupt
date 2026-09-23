using System;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace Erupt.Ros
{
    /// <summary>Pass-through to the real ROSConnection. The default in every build.</summary>
    public sealed class LiveRosBus : IRosBus
    {
        private ROSConnection connection;

        /// <summary>
        /// Lazy, matching the pre-seam behaviour where each component called
        /// GetOrCreateInstance() from its own Start().
        /// </summary>
        /// <remarks>
        /// The null check must be Unity's, not <c>??=</c>. ROSConnection is a
        /// MonoBehaviour, so once it is destroyed the reference is "fake null": Unity's
        /// overloaded == reports null while ?? and ??= see a live object and refuse to
        /// refresh it. With ??= a cached connection from a previous play session survives
        /// into the next one and every Subscribe/Publish lands on a destroyed object,
        /// silently, which is exactly what stopped the collision object listener working.
        /// </remarks>
        private ROSConnection Connection
        {
            get
            {
                if (connection == null) connection = ROSConnection.GetOrCreateInstance();
                return connection;
            }
        }

        public bool HasConnectionThread => Connection.HasConnectionThread;

        public bool HasConnectionError => Connection.HasConnectionError;

        public void Subscribe<T>(string topic, Action<T> callback) where T : Message =>
            Connection.Subscribe(topic, callback);

        public void Unsubscribe<T>(string topic, Action<T> callback) where T : Message =>
            Connection.Unsubscribe(topic, callback);

        public void RegisterPublisher<T>(string topic) where T : Message =>
            Connection.RegisterPublisher<T>(topic);

        public void RegisterPublisher<T>(string topic, int queueSize) where T : Message =>
            Connection.RegisterPublisher<T>(topic, queueSize);

        public void RegisterRosService<TRequest, TResponse>(string serviceName)
            where TRequest : Message where TResponse : Message =>
            Connection.RegisterRosService<TRequest, TResponse>(serviceName);

        public void SendServiceMessage<TResponse>(string serviceName, Message request, Action<TResponse> callback)
            where TResponse : Message, new() =>
            Connection.SendServiceMessage(serviceName, request, callback);

        public IRosActionClient<TGoal, TResult, TFeedback>
            RegisterActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message =>
            Connection.RegisterActionClient<TGoal, TResult, TFeedback>(actionName);

        public void Publish(string topic, Message message) =>
            Connection.Publish(topic, message);

        public void Publish(string topic, Message message, bool useTrailingPad) =>
            Connection.Publish(topic, message, useTrailingPad);
    }
}
