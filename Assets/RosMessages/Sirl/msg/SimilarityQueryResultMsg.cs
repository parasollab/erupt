// PLACEHOLDER — hand-written stand-in for the real (TBD) SIRL similarity result message.
// When the real msg definition exists, delete this file and regenerate via
// Robotics -> Generate ROS Messages (duplicate k_RosMessageName registrations collide).
using System;
using System.Linq;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Trajectory;

namespace RosMessageTypes.Sirl
{
    [Serializable]
    public class SimilarityQueryResultMsg : Message
    {
        public const string k_RosMessageName = "sirl_msgs/SimilarityQueryResult";
        public override string RosMessageName => k_RosMessageName;

        //  The three trajectories shown to the user, in presentation order
        public JointTrajectoryMsg[] trajectories;
        //  Indices (into trajectories) of the two judged most similar
        public int similar_index_a;
        public int similar_index_b;

        public SimilarityQueryResultMsg()
        {
            this.trajectories = new JointTrajectoryMsg[0];
            this.similar_index_a = 0;
            this.similar_index_b = 0;
        }

        public SimilarityQueryResultMsg(JointTrajectoryMsg[] trajectories, int similar_index_a, int similar_index_b)
        {
            this.trajectories = trajectories;
            this.similar_index_a = similar_index_a;
            this.similar_index_b = similar_index_b;
        }

        public static SimilarityQueryResultMsg Deserialize(MessageDeserializer deserializer) => new SimilarityQueryResultMsg(deserializer);

        private SimilarityQueryResultMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.trajectories, JointTrajectoryMsg.Deserialize, deserializer.ReadLength());
            deserializer.Read(out this.similar_index_a);
            deserializer.Read(out this.similar_index_b);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.WriteLength(this.trajectories);
            serializer.Write(this.trajectories);
            serializer.Write(this.similar_index_a);
            serializer.Write(this.similar_index_b);
        }

        public override string ToString()
        {
            return "SimilarityQueryResultMsg: " +
            "\ntrajectories: " + System.String.Join(", ", trajectories.ToList()) +
            "\nsimilar_index_a: " + similar_index_a.ToString() +
            "\nsimilar_index_b: " + similar_index_b.ToString();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize);
        }
    }
}
