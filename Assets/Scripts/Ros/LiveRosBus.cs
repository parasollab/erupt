using System;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace Erupt.Ros
{
    /// <summary>Pass-through to the real ROSConnection. The default in every build.</summary>
    public sealed class LiveRosBus : IRosBus
    {
        private ROSConnection connection;

        // Lazy, matching the pre-seam behavior where each component called
        // GetOrCreateInstance() from its own Start().
        private ROSConnection Connection => connection ??= ROSConnection.GetOrCreateInstance();

        public bool HasConnectionThread => Connection.HasConnectionThread;

        public void Subscribe<T>(string topic, Action<T> callback) where T : Message =>
            Connection.Subscribe(topic, callback);

        public void RegisterPublisher<T>(string topic) where T : Message =>
            Connection.RegisterPublisher<T>(topic);

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
