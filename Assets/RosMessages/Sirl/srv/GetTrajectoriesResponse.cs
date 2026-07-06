// PLACEHOLDER — hand-written stand-in for the real (TBD) SIRL trajectory service.
// When the real srv definition exists, delete this file and regenerate via
// Robotics -> Generate ROS Messages (duplicate k_RosMessageName registrations collide).
using System;
using System.Linq;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Trajectory;

namespace RosMessageTypes.Sirl
{
    [Serializable]
    public class GetTrajectoriesResponse : Message
    {
        public const string k_RosMessageName = "sirl_msgs/GetTrajectories";
        public override string RosMessageName => k_RosMessageName;

        public JointTrajectoryMsg[] trajectories;

        public GetTrajectoriesResponse()
        {
            this.trajectories = new JointTrajectoryMsg[0];
        }

        public GetTrajectoriesResponse(JointTrajectoryMsg[] trajectories)
        {
            this.trajectories = trajectories;
        }

        public static GetTrajectoriesResponse Deserialize(MessageDeserializer deserializer) => new GetTrajectoriesResponse(deserializer);

        private GetTrajectoriesResponse(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.trajectories, JointTrajectoryMsg.Deserialize, deserializer.ReadLength());
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.WriteLength(this.trajectories);
            serializer.Write(this.trajectories);
        }

        public override string ToString()
        {
            return "GetTrajectoriesResponse: " +
            "\ntrajectories: " + System.String.Join(", ", trajectories.ToList());
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize, MessageSubtopic.Response);
        }
    }
}
