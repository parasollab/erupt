using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class GlobalPlannerActionResult : ActionResult<GlobalPlannerResult>
    {
        public const string k_RosMessageName = "moveit_msgs/GlobalPlannerActionResult";
        public override string RosMessageName => k_RosMessageName;


        public GlobalPlannerActionResult() : base()
        {
            this.result = new GlobalPlannerResult();
        }

        public GlobalPlannerActionResult(HeaderMsg header, GoalStatusMsg status, GlobalPlannerResult result) : base(header, status)
        {
            this.result = result;
        }
        public static GlobalPlannerActionResult Deserialize(MessageDeserializer deserializer) => new GlobalPlannerActionResult(deserializer);

        GlobalPlannerActionResult(MessageDeserializer deserializer) : base(deserializer)
        {
            this.result = GlobalPlannerResult.Deserialize(deserializer);
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
