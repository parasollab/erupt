using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class HybridPlannerActionGoal : ActionGoal<HybridPlannerGoal>
    {
        public const string k_RosMessageName = "moveit_msgs/HybridPlannerActionGoal";
        public override string RosMessageName => k_RosMessageName;


        public HybridPlannerActionGoal() : base()
        {
            this.goal = new HybridPlannerGoal();
        }

        public HybridPlannerActionGoal(HeaderMsg header, GoalIDMsg goal_id, HybridPlannerGoal goal) : base(header, goal_id)
        {
            this.goal = goal;
        }
        public static HybridPlannerActionGoal Deserialize(MessageDeserializer deserializer) => new HybridPlannerActionGoal(deserializer);

        HybridPlannerActionGoal(MessageDeserializer deserializer) : base(deserializer)
        {
            this.goal = HybridPlannerGoal.Deserialize(deserializer);
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
