using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using System;
using System.Linq;
using RosMessageTypes.BuiltinInterfaces;

public class CollisionObjectPublisher : MonoBehaviour
{
    public string objectId = "unity_object";
    public string frameId = "world";
    public bool isMesh = false;
    public float publishRateHz = 1f;

    private ROSConnection ros;
    private float lastPublishTime = 0f;
    private Vector3 lastPosition;
    private Quaternion lastRotation;
    private Vector3 lastScale;
    private bool hasBeenPublished = false;

    void Start()
    {
        // Use existing ROS connection if available (pre-warmed by SystemPrewarmer)
        ros = ROSConnection.GetOrCreateInstance();
        
        // Register publisher (should be fast if pre-warmed)
        ros.RegisterPublisher<CollisionObjectMsg>("/collision_object");
        
        // Initialize tracking variables
        lastPosition = transform.position;
        lastRotation = transform.rotation;
        lastScale = transform.localScale;
    }

    void Update()
    {
        // Check if enough time has passed since last publish
        // if (Time.time - lastPublishTime >= 1.0f / publishRateHz)
        // {
            // Check if this is the first publish or if transform has changed
            bool shouldPublish = !hasBeenPublished || HasTransformChanged();
            
            if (shouldPublish)
            {
                PublishCollisionObject();
                UpdateLastTransform();
                hasBeenPublished = true;
                lastPublishTime = Time.time;
            }
        // }
    }

    void PublishCollisionObject()
    {
        CollisionObjectMsg msg = new CollisionObjectMsg
        {
            id = objectId,
            header = new HeaderMsg
            {
                frame_id = frameId,
                stamp = new TimeMsg() // Stamp left blank by default
            },
            operation = CollisionObjectMsg.ADD, // Use REMOVE or MOVE if needed
            pose = new PoseMsg
            {
                position = UnityToRosPosition(transform.position),
                orientation = UnityToRosQuaternion(transform.rotation)
            }
        };

        if (isMesh)
        {
            Mesh mesh = GetComponent<MeshFilter>().mesh;
            msg.meshes = new[] { UnityMeshToRosMesh(mesh) };
        }
        else
        {
            SolidPrimitiveMsg primitive = CreatePrimitiveFromUnityShape();
            if (primitive != null)
            {
                msg.primitives = new[] { primitive };
            }
            else
            {
                // Fallback to mesh if primitive type not supported
                Mesh mesh = GetComponent<MeshFilter>()?.mesh;
                if (mesh != null)
                {
                    msg.meshes = new[] { UnityMeshToRosMesh(mesh) };
                }
            }
        }

        ros.Publish("/collision_object", msg);
    }

    SolidPrimitiveMsg CreatePrimitiveFromUnityShape()
    {
        // Get the mesh filter to determine what Unity primitive this is
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            return null;
        }

        string meshName = meshFilter.mesh.name;
        Vector3 scale = transform.localScale;

        // Match Unity primitive types based on mesh name
        if (meshName.Contains("Cube"))
        {
            return new SolidPrimitiveMsg
            {
                type = 1, // BOX
                dimensions = new double[] { scale.x, scale.z, scale.y } // ROS: x, y, z → Unity: x, z, y
            };
        }
        else if (meshName.Contains("Sphere"))
        {
            // For sphere, ROS expects a single radius dimension
            float radius = Mathf.Max(scale.x, scale.y, scale.z) * 0.5f; // Unity sphere has diameter of 1, so radius is 0.5 * scale
            return new SolidPrimitiveMsg
            {
                type = 2, // SPHERE
                dimensions = new double[] { radius }
            };
        }
        else if (meshName.Contains("Cylinder"))
        {
            // For cylinder, ROS expects height and radius
            float height = scale.y; // Unity cylinder height is along Y axis
            float radius = Mathf.Max(scale.x, scale.z) * 0.5f; // Unity cylinder has diameter of 1, so radius is 0.5 * scale
            return new SolidPrimitiveMsg
            {
                type = 3, // CYLINDER
                dimensions = new double[] { height, radius }
            };
        }
        else if (meshName.Contains("Capsule"))
        {
            // For capsule, ROS expects height and radius
            float height = scale.y; // Unity capsule height is along Y axis
            float radius = Mathf.Max(scale.x, scale.z) * 0.5f; // Unity capsule has diameter of 1, so radius is 0.5 * scale
            return new SolidPrimitiveMsg
            {
                type = 3, // Use CYLINDER as closest match for capsule, or 4 if CONE is available for capsules
                dimensions = new double[] { height, radius }
            };
        }
        else if (meshName.Contains("Plane") || meshName.Contains("Quad"))
        {
            // Planes don't have a direct ROS primitive equivalent, so we'll use a very thin box
            return new SolidPrimitiveMsg
            {
                type = 1, // BOX
                dimensions = new double[] { scale.x, scale.z, 0.001 } // Very thin box to represent plane
            };
        }

        // Unknown primitive type, return null to fall back to mesh
        return null;
    }

    bool HasTransformChanged()
    {
        // Use small epsilon for floating point comparison to avoid precision issues
        const float epsilon = 0.001f;
        
        return Vector3.Distance(transform.position, lastPosition) > epsilon ||
               Quaternion.Angle(transform.rotation, lastRotation) > epsilon ||
               Vector3.Distance(transform.localScale, lastScale) > epsilon;
    }

    void UpdateLastTransform()
    {
        lastPosition = transform.position;
        lastRotation = transform.rotation;
        lastScale = transform.localScale;
    }

    static PointMsg UnityToRosPosition(Vector3 pos)
    {
        return new PointMsg(pos.x, pos.z, pos.y); // Unity Y → ROS Z
    }

    static QuaternionMsg UnityToRosQuaternion(Quaternion q)
    {
        return new QuaternionMsg(q.x, q.z, q.y, q.w);
    }

    static MeshMsg UnityMeshToRosMesh(Mesh unityMesh)
    {
        var rosVertices = unityMesh.vertices.Select(v => new PointMsg(v.x, v.z, v.y)).ToArray();
        var rosTriangles = new MeshTriangleMsg[unityMesh.triangles.Length / 3];

        for (int i = 0; i < rosTriangles.Length; i++)
        {
            rosTriangles[i] = new MeshTriangleMsg
            {
                vertex_indices = new uint[]
                {
                    (uint)unityMesh.triangles[i * 3],
                    (uint)unityMesh.triangles[i * 3 + 1],
                    (uint)unityMesh.triangles[i * 3 + 2]
                }
            };
        }

        return new MeshMsg
        {
            vertices = rosVertices,
            triangles = rosTriangles
        };
    }
}
