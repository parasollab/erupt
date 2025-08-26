using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class GlobalPlannerActionGoal : ActionGoal<GlobalPlannerGoal>
    {
        public const string k_RosMessageName = "moveit_msgs/GlobalPlannerActionGoal";
        public override string RosMessageName => k_RosMessageName;


        public GlobalPlannerActionGoal() : base()
        {
            this.goal = new GlobalPlannerGoal();
        }

        public GlobalPlannerActionGoal(HeaderMsg header, GoalIDMsg goal_id, GlobalPlannerGoal goal) : base(header, goal_id)
        {
            this.goal = goal;
        }
        public static GlobalPlannerActionGoal Deserialize(MessageDeserializer deserializer) => new GlobalPlannerActionGoal(deserializer);

        GlobalPlannerActionGoal(MessageDeserializer deserializer) : base(deserializer)
        {
            this.goal = GlobalPlannerGoal.Deserialize(deserializer);
        }
        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.header);
            serializer.Write(this.goal_id);
            serializer.Write(this.goal);
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
