using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Geometry;
using RosMessageTypes.Shape;
using RosMessageTypes.Std;
using System.Linq;
using System.Text;
using System.IO;

public class SendCollisionObjectFromWorldOrigin : MonoBehaviour
{
    public string objectId;
    public GameObject worldOrigin; // Assign the "World Origin" GameObject in the inspector
    public bool isMesh = false;
    public sbyte collisionOp = CollisionObjectMsg.ADD; // Use ADD, MOVE, or REMOVE
    public float publishRateHz = 2.0f;

    private ROSConnection ros;
    private float lastPublishTime = 0f;

    void Start()
    {
        objectId = gameObject.name;
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
        ros.RegisterPublisher<CollisionObjectMsg>("/collision_object");
        // ros.RegisterPublisher<StringMsg>("/collision_object_json");
    }

    void Update()
    {
        if (Time.time - lastPublishTime >= 3f)
        {
            PublishCollisionObject();
            lastPublishTime = Time.time;
        }
    }

    void PublishCollisionObject()
    {
        // Compute pose relative to World Origin
        Vector3 localPos = worldOrigin.transform.InverseTransformPoint(transform.position);
        Quaternion localRot = Quaternion.Inverse(worldOrigin.transform.rotation) * transform.rotation;

        var msg = new CollisionObjectMsg
        {
            id = objectId,
            header = new HeaderMsg
            {
                frame_id = "world" // MoveIt's planning frame
            },
            operation = collisionOp,
            pose = new PoseMsg
            {
                position = UnityToRosPosition(localPos),
                orientation = UnityToRosQuaternion(localRot)
            }
        };
        
        if (isMesh)
        {
            var mesh = GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
            {
                Debug.LogWarning("MeshFilter missing for isMesh=true.");
                return;
            }
            msg.meshes = new[] { UnityMeshToRosMesh(mesh) };
        }
        else
        {
            Vector3 scale = transform.lossyScale;
            msg.primitives = new[] {
                new SolidPrimitiveMsg
                {
                    type = SolidPrimitiveMsg.BOX,
                    dimensions = new double[] { scale.x, scale.z, scale.y } // ROS: x, y, z
                }
            };
        }
        
        ros.Publish("/collision_object", msg);
    }

    static PointMsg UnityToRosPosition(Vector3 pos)
    {
        return new PointMsg(pos.x, pos.z, pos.y); // Unity Y → ROS Z
    }

    static QuaternionMsg UnityToRosQuaternion(Quaternion q)
    {
        return new QuaternionMsg(q.x, q.z, q.y, q.w);
    }

    static MeshMsg UnityMeshToRosMesh(Mesh mesh)
    {
        var vertices = mesh.vertices.Select(v => new PointMsg(v.x, v.z, v.y)).ToArray();
        var triangles = new MeshTriangleMsg[mesh.triangles.Length / 3];

        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = new MeshTriangleMsg
            {
                vertex_indices = new uint[] {
                    (uint)mesh.triangles[i * 3],
                    (uint)mesh.triangles[i * 3 + 1],
                    (uint)mesh.triangles[i * 3 + 2]
                }
            };
        }

        return new MeshMsg
        {
            vertices = vertices,
            triangles = triangles
        };
    }
}
