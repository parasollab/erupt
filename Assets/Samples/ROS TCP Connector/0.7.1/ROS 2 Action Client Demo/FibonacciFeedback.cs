// Generated shape of example_interfaces/action/Fibonacci Feedback for this sample.
using System;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.ExampleInterfaces
{
    [Serializable]
    public class FibonacciFeedback : Message
    {
        public const string k_RosMessageName = "example_interfaces/Fibonacci";
        public override string RosMessageName => k_RosMessageName;

        public int[] sequence;

        public FibonacciFeedback()
        {
            sequence = new int[0];
        }

        public FibonacciFeedback(int[] sequence)
        {
            this.sequence = sequence;
        }

        public static FibonacciFeedback Deserialize(MessageDeserializer deserializer) =>
            new FibonacciFeedback(deserializer);

        FibonacciFeedback(MessageDeserializer deserializer)
        {
            deserializer.Read(out sequence, sizeof(int), deserializer.ReadLength());
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.WriteLength(sequence);
            serializer.Write(sequence);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize, MessageSubtopic.Feedback);
        }
    }
}
