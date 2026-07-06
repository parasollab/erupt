// Hand-written to mirror ros/sirl_vr_msgs/msg/PreferenceAnswer.msg in the sirl repo
// (Desktop/sirl). If sirl_vr_msgs is ever built in a ROS workspace this project points
// at, prefer regenerating via Robotics -> Generate ROS Messages and deleting this file.
using System;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.SirlVrMsgs
{
    [Serializable]
    public class PreferenceAnswerMsg : Message
    {
        public const string k_RosMessageName = "sirl_vr_msgs/PreferenceAnswer";
        public override string RosMessageName => k_RosMessageName;

        //  Must reference the same query_id the GetPreferenceQuery response returned.
        public uint query_id;
        //  The trajectory slot the human preferred (0 or 1).
        public byte preferred;
        public string session_id;

        public PreferenceAnswerMsg()
        {
            this.query_id = 0;
            this.preferred = 0;
            this.session_id = "";
        }

        public PreferenceAnswerMsg(uint query_id, byte preferred, string session_id)
        {
            this.query_id = query_id;
            this.preferred = preferred;
            this.session_id = session_id;
        }

        public static PreferenceAnswerMsg Deserialize(MessageDeserializer deserializer) => new PreferenceAnswerMsg(deserializer);

        private PreferenceAnswerMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.query_id);
            deserializer.Read(out this.preferred);
            deserializer.Read(out this.session_id);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.query_id);
            serializer.Write(this.preferred);
            serializer.Write(this.session_id);
        }

        public override string ToString()
        {
            return "PreferenceAnswerMsg: " +
            "\nquery_id: " + query_id.ToString() +
            "\npreferred: " + preferred.ToString() +
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
