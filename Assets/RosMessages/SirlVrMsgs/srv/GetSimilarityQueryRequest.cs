// Hand-written to mirror ros/sirl_vr_msgs/srv/GetSimilarityQuery.srv in the sirl repo
// (Desktop/sirl). If sirl_vr_msgs is ever built in a ROS workspace this project points
// at, prefer regenerating via Robotics -> Generate ROS Messages and deleting this file.
using System;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.SirlVrMsgs
{
    [Serializable]
    public class GetSimilarityQueryRequest : Message
    {
        public const string k_RosMessageName = "sirl_vr_msgs/GetSimilarityQuery";
        public override string RosMessageName => k_RosMessageName;

        public string session_id;

        public GetSimilarityQueryRequest()
        {
            this.session_id = "";
        }

        public GetSimilarityQueryRequest(string session_id)
        {
            this.session_id = session_id;
        }

        public static GetSimilarityQueryRequest Deserialize(MessageDeserializer deserializer) => new GetSimilarityQueryRequest(deserializer);

        private GetSimilarityQueryRequest(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.session_id);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.session_id);
        }

        public override string ToString()
        {
            return "GetSimilarityQueryRequest: " +
            "\nsession_id: " + session_id;
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
