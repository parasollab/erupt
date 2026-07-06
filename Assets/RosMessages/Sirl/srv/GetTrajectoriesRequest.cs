// PLACEHOLDER — hand-written stand-in for the real (TBD) SIRL trajectory service.
// When the real srv definition exists, delete this file and regenerate via
// Robotics -> Generate ROS Messages (duplicate k_RosMessageName registrations collide).
using System;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.Sirl
{
    [Serializable]
    public class GetTrajectoriesRequest : Message
    {
        public const string k_RosMessageName = "sirl_msgs/GetTrajectories";
        public override string RosMessageName => k_RosMessageName;

        //  Number of trajectories requested (3 for similarity queries, 2 for preference queries)
        public uint num_trajectories;

        public GetTrajectoriesRequest()
        {
            this.num_trajectories = 0;
        }

        public GetTrajectoriesRequest(uint num_trajectories)
        {
            this.num_trajectories = num_trajectories;
        }

        public static GetTrajectoriesRequest Deserialize(MessageDeserializer deserializer) => new GetTrajectoriesRequest(deserializer);

        private GetTrajectoriesRequest(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.num_trajectories);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.num_trajectories);
        }

        public override string ToString()
        {
            return "GetTrajectoriesRequest: " +
            "\nnum_trajectories: " + num_trajectories.ToString();
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
