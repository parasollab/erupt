using System;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace Erupt.Ros
{
    /// <summary>
    /// The ROS transport, behind a seam. Exists so scenes can run headless in tests
    /// without a live TCP endpoint — see refactor/backlog.md B16.
    /// </summary>
    /// <remarks>
    /// Mirrors the subset of ROSConnection that ERUPT actually uses. RegisterPublisher
    /// returns void because no call site uses ROSConnection's RosTopicState return.
    /// This is transport only: no topic, service or message type is renamed by it.
    /// </remarks>
    public interface IRosBus
    {
        bool HasConnectionThread { get; }

        void Subscribe<T>(string topic, Action<T> callback) where T : Message;

        void RegisterPublisher<T>(string topic) where T : Message;

        void RegisterRosService<TRequest, TResponse>(string serviceName)
            where TRequest : Message where TResponse : Message;

        void SendServiceMessage<TResponse>(string serviceName, Message request, Action<TResponse> callback)
            where TResponse : Message, new();

        IRosActionClient<TGoal, TResult, TFeedback>
            RegisterActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message;

        void Publish(string topic, Message message);

        void Publish(string topic, Message message, bool useTrailingPad);
    }
}
