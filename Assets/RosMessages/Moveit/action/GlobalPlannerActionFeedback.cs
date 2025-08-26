using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class GlobalPlannerActionFeedback : ActionFeedback<GlobalPlannerFeedback>
    {
        public const string k_RosMessageName = "moveit_msgs/GlobalPlannerActionFeedback";
        public override string RosMessageName => k_RosMessageName;


        public GlobalPlannerActionFeedback() : base()
        {
            this.feedback = new GlobalPlannerFeedback();
        }

        public GlobalPlannerActionFeedback(HeaderMsg header, GoalStatusMsg status, GlobalPlannerFeedback feedback) : base(header, status)
        {
            this.feedback = feedback;
        }
        public static GlobalPlannerActionFeedback Deserialize(MessageDeserializer deserializer) => new GlobalPlannerActionFeedback(deserializer);

        GlobalPlannerActionFeedback(MessageDeserializer deserializer) : base(deserializer)
        {
            this.feedback = GlobalPlannerFeedback.Deserialize(deserializer);
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
