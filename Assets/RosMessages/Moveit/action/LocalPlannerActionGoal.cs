using System.Collections.Generic;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Std;
using RosMessageTypes.Actionlib;

namespace RosMessageTypes.Moveit
{
    public class LocalPlannerActionGoal : ActionGoal<LocalPlannerGoal>
    {
        public const string k_RosMessageName = "moveit_msgs/LocalPlannerActionGoal";
        public override string RosMessageName => k_RosMessageName;


        public LocalPlannerActionGoal() : base()
        {
            this.goal = new LocalPlannerGoal();
        }

        public LocalPlannerActionGoal(HeaderMsg header, GoalIDMsg goal_id, LocalPlannerGoal goal) : base(header, goal_id)
        {
            this.goal = goal;
        }
        public static LocalPlannerActionGoal Deserialize(MessageDeserializer deserializer) => new LocalPlannerActionGoal(deserializer);

        LocalPlannerActionGoal(MessageDeserializer deserializer) : base(deserializer)
        {
            this.goal = LocalPlannerGoal.Deserialize(deserializer);
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
