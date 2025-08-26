using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class LocalPlannerActionResult : ActionResult<LocalPlannerResult>
    {
        public const string k_RosMessageName = "moveit_msgs/LocalPlannerActionResult";
        public override string RosMessageName => k_RosMessageName;


        public LocalPlannerActionResult() : base()
        {
            this.result = new LocalPlannerResult();
        }

        public LocalPlannerActionResult(HeaderMsg header, GoalStatusMsg status, LocalPlannerResult result) : base(header, status)
        {
            this.result = result;
        }
        public static LocalPlannerActionResult Deserialize(MessageDeserializer deserializer) => new LocalPlannerActionResult(deserializer);

        LocalPlannerActionResult(MessageDeserializer deserializer) : base(deserializer)
        {
            this.result = LocalPlannerResult.Deserialize(deserializer);
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
