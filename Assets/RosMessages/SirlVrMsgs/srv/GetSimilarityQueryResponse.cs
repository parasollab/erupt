// Hand-written to mirror ros/sirl_vr_msgs/srv/GetSimilarityQuery.srv in the sirl repo
// (Desktop/sirl). If sirl_vr_msgs is ever built in a ROS workspace this project points
// at, prefer regenerating via Robotics -> Generate ROS Messages and deleting this file.
using System;
using System.Linq;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Trajectory;

namespace RosMessageTypes.SirlVrMsgs
{
    [Serializable]
    public class GetSimilarityQueryResponse : Message
    {
        public const string k_RosMessageName = "sirl_vr_msgs/GetSimilarityQuery";
        public override string RosMessageName => k_RosMessageName;

        //  Id for this query event; must be echoed back in the SimilarityAnswer.
        public uint query_id;
        //  Index-ordered: slot 0/1/2 == trajectories[0/1/2].
        public JointTrajectoryMsg[] trajectories;

        public GetSimilarityQueryResponse()
        {
            this.query_id = 0;
            this.trajectories = new JointTrajectoryMsg[0];
        }

        public GetSimilarityQueryResponse(uint query_id, JointTrajectoryMsg[] trajectories)
        {
            this.query_id = query_id;
            this.trajectories = trajectories;
        }

        public static GetSimilarityQueryResponse Deserialize(MessageDeserializer deserializer) => new GetSimilarityQueryResponse(deserializer);

        private GetSimilarityQueryResponse(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.query_id);
            deserializer.Read(out this.trajectories, JointTrajectoryMsg.Deserialize, deserializer.ReadLength());
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.query_id);
            serializer.WriteLength(this.trajectories);
            serializer.Write(this.trajectories);
        }

        public override string ToString()
        {
            return "GetSimilarityQueryResponse: " +
            "\nquery_id: " + query_id.ToString() +
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
