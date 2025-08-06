using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Geometry;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;
using System.Linq;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;


public class ShapePublisher : MonoBehaviour
{
    public GameObject worldOrigin; // Assign the "World Origin" GameObject in the inspector
    
    private ROSConnection ros;
    private float lastPublishTime = 0f;
    private MessageSerializer ms;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        ms = new MessageSerializer();
        if (worldOrigin == null)
        {
            worldOrigin = GameObject.Find("World Origin");
        }

        if (worldOrigin == null)
        {
            Debug.LogError("World Origin object not assigned or found in scene.");
            enabled = false;
            return;
        }

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<SolidPrimitiveMsg>("/shape_primitive");
        // ros.RegisterPublisher<StringMsg>("/shape_primitive_str");
    }
    
    static PointMsg UnityToRosPosition(Vector3 pos)
    {
        return new PointMsg(pos.x, pos.z, pos.y); // Unity Y → ROS Z
    }

    static QuaternionMsg UnityToRosQuaternion(Quaternion q)
    {
        return new QuaternionMsg(q.x, q.z, q.y, q.w);
    }

    // Update is called once per frame
    void Update()
    {
        // Compute pose relative to World Origin
        Vector3 localPosition = worldOrigin.transform.InverseTransformPoint(transform.position);
        Quaternion localRotation = Quaternion.Inverse(worldOrigin.transform.rotation) * transform.rotation;

        Vector3 scale = transform.lossyScale;
        var msg = new SolidPrimitiveMsg
        {
            type = SolidPrimitiveMsg.BOX,
            dimensions = new double[] { scale.x, scale.z, scale.y } // ROS: x, y, z
        };

        if (Time.time - lastPublishTime >= 2f)
        {
            ros.Publish("/shape_primitive", msg);
            Debug.Log($"ms len: {ms.Length}");
            ms.SerializeMessageWithLength(msg);
            Debug.Log($"new ms len: {ms.Length}");
            
            ms.Clear();
            lastPublishTime = Time.time;
        }
    }
}
