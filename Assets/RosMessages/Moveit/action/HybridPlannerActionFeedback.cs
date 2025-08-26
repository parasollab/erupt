using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class HybridPlannerActionFeedback : ActionFeedback<HybridPlannerFeedback>
    {
        public const string k_RosMessageName = "moveit_msgs/HybridPlannerActionFeedback";
        public override string RosMessageName => k_RosMessageName;


        public HybridPlannerActionFeedback() : base()
        {
            this.feedback = new HybridPlannerFeedback();
        }

        public HybridPlannerActionFeedback(HeaderMsg header, GoalStatusMsg status, HybridPlannerFeedback feedback) : base(header, status)
        {
            this.feedback = feedback;
        }
        public static HybridPlannerActionFeedback Deserialize(MessageDeserializer deserializer) => new HybridPlannerActionFeedback(deserializer);

        HybridPlannerActionFeedback(MessageDeserializer deserializer) : base(deserializer)
        {
            this.feedback = HybridPlannerFeedback.Deserialize(deserializer);
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
