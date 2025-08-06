using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.Moveit;

public class GetInitialPlanningScene : MonoBehaviour
{
    ROSConnection ros;
    public string planningSceneServiceTopic = "/get_planning_scene";
    public string monitoredPlanningSceneTopic = "/monitored_planning_scene";
    
    public GameObject worldOrigin; // Assign the "World Origin" GameObject in the inspector
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<PlanningSceneMsg>(monitoredPlanningSceneTopic, OnMonitoredPlanningSceneReceived);
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnMonitoredPlanningSceneReceived(PlanningSceneMsg msg)
    {
        Debug.Log($"Num Collision Objects: {msg.world.collision_objects.Length}");
        for (int i = 0; i < msg.world.collision_objects.Length; i++)
        {
            if (GameObject.Find(msg.world.collision_objects[i].id) == null)
            {
                var obj = msg.world.collision_objects[i];
                GameObject go = new GameObject(obj.id);
                
                // Pose (ROS: x,y,z -> Unity: x,z,y)
                Vector3 pos = new Vector3((float)obj.pose.position.x, (float)obj.pose.position.z, (float)obj.pose.position.y);
                Quaternion rot = new Quaternion((float)obj.pose.orientation.x, (float)obj.pose.orientation.z, (float)obj.pose.orientation.y, (float)obj.pose.orientation.w);

                go.transform.position = worldOrigin.transform.TransformPoint(pos);
                go.transform.rotation = worldOrigin.transform.rotation * rot;

                if (obj.primitives.Length > 0)
                {
                    // Assume BOX for now
                    var scale = new Vector3((float)obj.primitives[0].dimensions[0], (float)obj.primitives[0].dimensions[2], (float)obj.primitives[0].dimensions[1]);
                    go.transform.localScale = scale;
                    
                    // Add basic mesh
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.transform.SetParent(go.transform, false);
                }
            }
        }
    }
}
