// PLACEHOLDER — hand-written stand-in for the real (TBD) SIRL preference result message.
// When the real msg definition exists, delete this file and regenerate via
// Robotics -> Generate ROS Messages (duplicate k_RosMessageName registrations collide).
using System;
using System.Linq;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Trajectory;

namespace RosMessageTypes.Sirl
{
    [Serializable]
    public class PreferenceQueryResultMsg : Message
    {
        public const string k_RosMessageName = "sirl_msgs/PreferenceQueryResult";
        public override string RosMessageName => k_RosMessageName;

        //  The two trajectories shown to the user, in presentation order
        public JointTrajectoryMsg[] trajectories;
        //  Index (into trajectories) of the preferred trajectory
        public int preferred_index;

        public PreferenceQueryResultMsg()
        {
            this.trajectories = new JointTrajectoryMsg[0];
            this.preferred_index = 0;
        }

        public PreferenceQueryResultMsg(JointTrajectoryMsg[] trajectories, int preferred_index)
        {
            this.trajectories = trajectories;
            this.preferred_index = preferred_index;
        }

        public static PreferenceQueryResultMsg Deserialize(MessageDeserializer deserializer) => new PreferenceQueryResultMsg(deserializer);

        private PreferenceQueryResultMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.trajectories, JointTrajectoryMsg.Deserialize, deserializer.ReadLength());
            deserializer.Read(out this.preferred_index);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.WriteLength(this.trajectories);
            serializer.Write(this.trajectories);
            serializer.Write(this.preferred_index);
        }

        public override string ToString()
        {
            return "PreferenceQueryResultMsg: " +
            "\ntrajectories: " + System.String.Join(", ", trajectories.ToList()) +
            "\npreferred_index: " + preferred_index.ToString();
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
