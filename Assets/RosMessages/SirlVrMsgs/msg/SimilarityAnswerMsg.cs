// Hand-written to mirror ros/sirl_vr_msgs/msg/SimilarityAnswer.msg in the sirl repo
// (Desktop/sirl). If sirl_vr_msgs is ever built in a ROS workspace this project points
// at, prefer regenerating via Robotics -> Generate ROS Messages and deleting this file.
using System;
using System.Linq;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.SirlVrMsgs
{
    [Serializable]
    public class SimilarityAnswerMsg : Message
    {
        public const string k_RosMessageName = "sirl_vr_msgs/SimilarityAnswer";
        public override string RosMessageName => k_RosMessageName;

        //  Must reference the same query_id the GetSimilarityQuery response returned.
        public uint query_id;
        //  The two trajectory slots judged most similar (e.g. [0, 2]); fixed size 2.
        public byte[] similar_pair;
        public string session_id;

        public SimilarityAnswerMsg()
        {
            this.query_id = 0;
            this.similar_pair = new byte[2];
            this.session_id = "";
        }

        public SimilarityAnswerMsg(uint query_id, byte[] similar_pair, string session_id)
        {
            this.query_id = query_id;
            this.similar_pair = similar_pair;
            this.session_id = session_id;
        }

        public static SimilarityAnswerMsg Deserialize(MessageDeserializer deserializer) => new SimilarityAnswerMsg(deserializer);

        private SimilarityAnswerMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.query_id);
            deserializer.Read(out this.similar_pair, sizeof(byte), 2);
            deserializer.Read(out this.session_id);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.query_id);
            serializer.Write(this.similar_pair);
            serializer.Write(this.session_id);
        }

        public override string ToString()
        {
            return "SimilarityAnswerMsg: " +
            "\nquery_id: " + query_id.ToString() +
            "\nsimilar_pair: " + System.String.Join(", ", similar_pair.ToList()) +
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
