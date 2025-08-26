using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class LocalPlannerActionFeedback : ActionFeedback<LocalPlannerFeedback>
    {
        public const string k_RosMessageName = "moveit_msgs/LocalPlannerActionFeedback";
        public override string RosMessageName => k_RosMessageName;


        public LocalPlannerActionFeedback() : base()
        {
            this.feedback = new LocalPlannerFeedback();
        }

        public LocalPlannerActionFeedback(HeaderMsg header, GoalStatusMsg status, LocalPlannerFeedback feedback) : base(header, status)
        {
            this.feedback = feedback;
        }
        public static LocalPlannerActionFeedback Deserialize(MessageDeserializer deserializer) => new LocalPlannerActionFeedback(deserializer);

        LocalPlannerActionFeedback(MessageDeserializer deserializer) : base(deserializer)
        {
            this.feedback = LocalPlannerFeedback.Deserialize(deserializer);
        }
        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.header);
            serializer.Write(this.status);
            serializer.Write(this.feedback);
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
