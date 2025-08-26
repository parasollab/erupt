using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class HybridPlannerActionResult : ActionResult<HybridPlannerResult>
    {
        public const string k_RosMessageName = "moveit_msgs/HybridPlannerActionResult";
        public override string RosMessageName => k_RosMessageName;


        public HybridPlannerActionResult() : base()
        {
            this.result = new HybridPlannerResult();
        }

        public HybridPlannerActionResult(HeaderMsg header, GoalStatusMsg status, HybridPlannerResult result) : base(header, status)
        {
            this.result = result;
        }
        public static HybridPlannerActionResult Deserialize(MessageDeserializer deserializer) => new HybridPlannerActionResult(deserializer);

        HybridPlannerActionResult(MessageDeserializer deserializer) : base(deserializer)
        {
            this.result = HybridPlannerResult.Deserialize(deserializer);
        }
        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.header);
            serializer.Write(this.status);
            serializer.Write(this.result);
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
