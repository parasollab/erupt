// Generated shape of example_interfaces/action/Fibonacci Goal for this sample.
using System;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.ExampleInterfaces
{
    [Serializable]
    public class FibonacciGoal : Message
    {
        public const string k_RosMessageName = "example_interfaces/Fibonacci";
        public override string RosMessageName => k_RosMessageName;

        public int order;

        public FibonacciGoal()
        {
        }

        public FibonacciGoal(int order)
        {
            this.order = order;
        }

        public static FibonacciGoal Deserialize(MessageDeserializer deserializer) =>
            new FibonacciGoal(deserializer);

        FibonacciGoal(MessageDeserializer deserializer)
        {
            deserializer.Read(out order);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(order);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize, MessageSubtopic.Goal);
        }
    }
}
