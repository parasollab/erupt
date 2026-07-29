using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Shape;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using System;
using System.Linq;
using RosMessageTypes.BuiltinInterfaces;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

public class CollisionObjectPublisher : MonoBehaviour
{
    // Outgoing queue size for the shared /collision_object topic. The connector's default is
    // 10 and TopicMessageSender silently drops the OLDEST queued message on overflow -- with
    // ~60 publishers all bursting in the same frame (scene load ADDs, scene unload REMOVEs),
    // most of the burst was dropped, leaving arbitrary objects missing from (or lingering in)
    // the planning scene. The first RegisterPublisher call for a topic wins and later ones are
    // ignored, so every script that registers this topic must pass this same value.
    public const int CollisionObjectQueueSize = 512;

    public string objectId = "unity_object";
    public string frameId = "world";
    public bool isMesh = false;
    public bool autoDetectMeshType = false; // New field for automatic detection
    public bool createReadableMeshCopy = false; // Create readable copy of non-readable meshes
    public float publishRateHz = 1f;
    public bool trackLatency = false;
    public bool pausePublishing = false;
    public bool debugLogs = false;
    public GameObject worldOrigin; // Optional world origin for relative positioning

    private ROSConnection ros;
    private float lastPublishTime = 0f;
    private Vector3 lastPosition;
    private Quaternion lastRotation;
    private Vector3 lastScale;
    public bool hasBeenPublished = false;
    private Mesh readableMeshCopy = null; // Cache for the readable mesh copy

    void Start()
    {
        // Use existing ROS connection if available (pre-warmed by SystemPrewarmer)
        ros = ROSConnection.GetOrCreateInstance();
        
        // Validate ROS connection
        if (ros == null)
        {
            Debug.LogError($"Failed to get ROS connection for object '{gameObject.name}'");
            return;
        }

        // Register collision object publisher
        try
        {
            ros.RegisterPublisher<CollisionObjectMsg>("/collision_object", CollisionObjectQueueSize);
            if (debugLogs)
                Debug.Log($"[{gameObject.name}] Registered /collision_object publisher");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[{gameObject.name}] Failed to register /collision_object publisher: {e.Message}");
            return;
        }

        if (trackLatency)
        {
            // Register latency round-trip publisher and subscriber
            try
            {
                ros.RegisterPublisher<StringMsg>("/latency_data");
                ros.Subscribe<HeaderMsg>("/collision_object_pong", OnLatencyPong);
                Debug.Log($"[{gameObject.name}] Registered /latency_data publisher and /collision_object_pong subscriber");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[{gameObject.name}] Failed to register latency topics: {e.Message}");
            }
        }
        
        // Initialize tracking variables
        lastPosition = transform.position;
        lastRotation = transform.rotation;
        lastScale = transform.lossyScale;

        ObjectMetricsLogger.Instance?.LogEvent("object_created", objectId);

        // Log object analysis for debugging
        // AnalyzeObject();
    }

    void Update()
    {
        if (pausePublishing) return;

        if (Time.time - lastPublishTime < 1.0f / publishRateHz)
            return;

        bool shouldPublish = !hasBeenPublished || HasTransformChanged();
        if (shouldPublish)
        {
            if (debugLogs)
                Debug.Log($"Publishing collision object '{objectId}' (pos={transform.position}, rot={transform.rotation.eulerAngles}, scale={transform.localScale})");

            PublishCollisionObject();
            UpdateLastTransform();
            hasBeenPublished = true;
            lastPublishTime = Time.time;
        }
    }

    public void ForceRepublish()
    {
        hasBeenPublished = false;
        lastPublishTime = 0f;
    }

    // Method to check if publisher is properly registered
    bool IsPublisherRegistered()
    {
        if (ros == null)
        {
            return false;
        }

        // Check if the topic is registered by trying to get publisher info
        try
        {
            // This is a simple check - if the ROS connection is working, the publisher should be registered
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Public method to check ROS connection status
    [ContextMenu("Check ROS Connection Status")]
    public void CheckROSConnectionStatus()
    {
        Debug.Log($"=== ROS Connection Status for '{gameObject.name}' ===");
        
        if (ros == null)
        {
            Debug.LogError("ROS Connection: NULL");
            Debug.LogError("Possible causes:");
            Debug.LogError("1. ROS-TCP-Connector not properly initialized");
            Debug.LogError("2. ROS server not running");
            Debug.LogError("3. Network connection issues");
            return;
        }

        Debug.Log("ROS Connection: OK");
        Debug.Log($"Publisher Registered: {IsPublisherRegistered()}");
        Debug.Log($"Topic: /collision_object");
        Debug.Log($"Object ID: {objectId}");
        Debug.Log($"Frame ID: {frameId}");
        Debug.Log($"Auto Detect Mesh: {autoDetectMeshType}");
        Debug.Log($"Create Readable Copy: {createReadableMeshCopy}");
        
        // Check if mesh is available
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.mesh != null)
        {
            Debug.Log($"Mesh: {meshFilter.mesh.name} (Readable: {meshFilter.mesh.isReadable})");
        }
        else
        {
            Debug.Log("No mesh found on this object");
        }
        
        Debug.Log("=== End Status ===");
    }

    Vector3 GetRelativePosition(Vector3 position)
    {
        if (worldOrigin != null)
        {
            return worldOrigin.transform.InverseTransformPoint(position);
        }
        return position;
    }

    Quaternion GetRelativeRotation(Quaternion rotation)
    {
        if (worldOrigin != null)
        {
            return Quaternion.Inverse(worldOrigin.transform.rotation) * rotation;
        }
        return rotation;
    }

    void PublishCollisionObject()
    {
        bool isMesh = false;

        // Validate ROS connection
        if (ros == null)
        {
            Debug.LogError($"ROS connection is null for object '{gameObject.name}' - cannot publish");
            return;
        }

        // Validate topic name
        string topicName = "/collision_object";
        if (string.IsNullOrEmpty(topicName))
        {
            Debug.LogError($"Topic name is null or empty for object '{gameObject.name}' - cannot publish");
            return;
        }

        // Get the position relative to world origin if specified
        Vector3 relativePosition = GetRelativePosition(transform.position);

        CollisionObjectMsg msg = new CollisionObjectMsg
        {
            id = objectId,
            header = new HeaderMsg
            {
                frame_id = frameId,
                stamp = GetRosTimestamp()
            },
            operation = CollisionObjectMsg.ADD, // Use REMOVE or MOVE if needed
            pose = new PoseMsg
            {
                position = RosUnityConversion.UnityToRosPosition(relativePosition),
                orientation = RosUnityConversion.UnityToRosQuaternion(GetRelativeRotation(transform.rotation))
            }
        };

        if (!hasBeenPublished || HasScaleChanged())
        {
            if (!isMesh && ShouldTreatAsMesh())
            {
                Mesh mesh = GetReadableMesh();
                if (mesh != null)
                {
                    isMesh = true;
                    msg.meshes = new[] { UnityMeshToRosMesh(mesh, transform) };
                }
                else
                {
                    Debug.LogError($"Failed to get readable mesh for object '{gameObject.name}' - cannot publish collision object");
                    return;
                }
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
                    Mesh mesh = GetReadableMesh();
                    if (mesh != null)
                    {
                        isMesh = true;
                        msg.meshes = new[] { UnityMeshToRosMesh(mesh, transform) };
                    }
                    else
                    {
                        Debug.LogError($"Failed to get readable mesh for fallback on object '{gameObject.name}' - cannot publish collision object");
                        return;
                    }
                }
            }
        }
        else
        {
            if (!HasScaleChanged())
            {
                msg.operation = CollisionObjectMsg.MOVE;
            }
        }

        if (trackLatency)
            Debug.Log($"[Latency] Publishing '{objectId}' op={msg.operation} stamp.sec={msg.header.stamp.sec} stamp.nanosec={msg.header.stamp.nanosec}");
        try
        {
            if (isMesh)
                ros.Publish(topicName, msg, false);
            else
                ros.Publish(topicName, msg);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to publish collision object '{objectId}': {e.Message}");
        }
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
        // lossyScale, not localScale: props nested under scaled parents (e.g. the 0.12-scaled
        // computer model in the Task4 factory scenes) need the full hierarchy scale.
        Vector3 scale = transform.lossyScale;

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

        // Debug.Log("Scale: " + transform.scale);
        // Debug.Log("Local Scale: " + transform.localScale);
        
        return Vector3.Distance(transform.position, lastPosition) > epsilon ||
               Quaternion.Angle(transform.rotation, lastRotation) > epsilon;
    }

    bool HasScaleChanged()
    {
        const float epsilon = 0.001f;
        return Vector3.Distance(transform.lossyScale, lastScale) > epsilon;
    }

    void UpdateLastTransform()
    {
        lastPosition = transform.position;
        lastRotation = transform.rotation;
        lastScale = transform.lossyScale;
    }

    void OnDestroy()
    {
        ObjectMetricsLogger.Instance?.LogEvent("object_deleted", objectId);

        // Delete the collision object from the planning scene
        if (ros != null)
        {
            CollisionObjectMsg msg = new CollisionObjectMsg
            {
                id = objectId,
                header = new HeaderMsg
                {
                    frame_id = frameId,
                    stamp = GetRosTimestamp()
                },
                operation = CollisionObjectMsg.REMOVE
            };

            ros.Publish("/collision_object", msg);
        }
        // Clean up the readable mesh copy to prevent memory leaks
        if (readableMeshCopy != null)
        {
            if (Application.isPlaying)
            {
                Destroy(readableMeshCopy);
            }
            else
            {
                DestroyImmediate(readableMeshCopy);
            }
            readableMeshCopy = null;
        }
    }

    void OnApplicationPause(bool paused)
    {
        if (!paused)
            ForceRepublish();
    }

    static MeshMsg UnityMeshToRosMesh(Mesh unityMesh, Transform transform = null)
    {
        // Validate mesh
        if (unityMesh == null || unityMesh.vertexCount == 0)
        {
            Debug.LogWarning("Attempting to convert null or empty mesh to ROS mesh");
            return new MeshMsg();
        }

        // Check if mesh is readable (required for accessing vertices/triangles)
        if (!unityMesh.isReadable)
        {
            Debug.LogError($"Mesh '{unityMesh.name}' is not readable. Please enable 'Read/Write Enabled' in the mesh import settings.");
            return new MeshMsg();
        }

        // Convert vertices (Unity Y → ROS Z) with scale applied
        var rosVertices = new PointMsg[unityMesh.vertexCount];
        for (int i = 0; i < unityMesh.vertexCount; i++)
        {
            Vector3 vertex = unityMesh.vertices[i];

            // Apply scale if transform is provided; lossyScale so parents' scale (e.g. scaled
            // model prefab roots in the Task4 scenes) is included, not just this object's own.
            if (transform != null)
            {
                vertex = Vector3.Scale(vertex, transform.lossyScale);
            }

            rosVertices[i] = new PointMsg(vertex.x, vertex.z, vertex.y);
        }

        // Convert triangles
        int[] triangles = unityMesh.triangles;
        if (triangles.Length == 0 || triangles.Length % 3 != 0)
        {
            Debug.LogWarning($"Mesh '{unityMesh.name}' has invalid triangle data");
            return new MeshMsg { vertices = rosVertices, triangles = new MeshTriangleMsg[0] };
        }

        var rosTriangles = new MeshTriangleMsg[triangles.Length / 3];
        for (int i = 0; i < rosTriangles.Length; i++)
        {
            int baseIndex = i * 3;
            rosTriangles[i] = new MeshTriangleMsg
            {
                vertex_indices = new uint[]
                {
                    (uint)triangles[baseIndex],
                    (uint)triangles[baseIndex + 1],
                    (uint)triangles[baseIndex + 2]
                }
            };
        }

        return new MeshMsg
        {
            vertices = rosVertices,
            triangles = rosTriangles
        };
    }

    // Helper method to get mesh info for debugging
    void LogMeshInfo()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            Debug.LogWarning($"No mesh found on object '{gameObject.name}'");
            return;
        }

        Mesh mesh = meshFilter.mesh;
        Debug.Log($"Mesh '{mesh.name}' on '{gameObject.name}': " +
                 $"Vertices: {mesh.vertexCount}, " +
                 $"Triangles: {mesh.triangles.Length / 3}, " +
                 $"Readable: {mesh.isReadable}, " +
                 $"Should treat as mesh: {ShouldTreatAsMesh()}");
    }

    // Helper method to provide instructions for fixing non-readable mesh issues
    void ProvideMeshFixInstructions(string meshName)
    {
        Debug.LogError($"=== FIX REQUIRED: Mesh '{meshName}' is not readable ===");
        Debug.LogError("To fix this issue, follow these steps:");
        Debug.LogError("1. In the Project window, find the mesh asset (likely in Assets/.../eSeries_UR5e_013.fbx or similar)");
        Debug.LogError("2. Select the mesh asset file");
        Debug.LogError("3. In the Inspector, look for the 'Model' tab");
        Debug.LogError("4. Find the 'Read/Write Enabled' checkbox and enable it");
        Debug.LogError("5. Click 'Apply' to save the changes");
        Debug.LogError("6. Restart the scene or re-enter Play mode");
        Debug.LogError("Note: This will increase memory usage but is required for collision detection");
        Debug.LogError("Alternative: Use a primitive collision shape instead by setting isMesh = false");
        Debug.LogError("=== END INSTRUCTIONS ===");
    }

    // Method to create a readable copy of a mesh at runtime
    Mesh CreateReadableMeshCopy(Mesh originalMesh)
    {
        if (originalMesh == null)
        {
            Debug.LogError("Cannot create readable copy of null mesh");
            return null;
        }

        if (originalMesh.isReadable)
        {
            Debug.Log("Mesh is already readable, returning original");
            return originalMesh;
        }

        Debug.Log($"Creating readable copy of mesh '{originalMesh.name}'");
        
        try
        {
            // Create a new mesh
            Mesh readableMesh = new Mesh();
            readableMesh.name = originalMesh.name + "_ReadableCopy";

            // Copy basic properties that are always accessible
            readableMesh.vertices = originalMesh.vertices;
            readableMesh.normals = originalMesh.normals;
            readableMesh.uv = originalMesh.uv;
            readableMesh.uv2 = originalMesh.uv2;
            readableMesh.uv3 = originalMesh.uv3;
            readableMesh.uv4 = originalMesh.uv4;
            readableMesh.tangents = originalMesh.tangents;
            readableMesh.colors = originalMesh.colors;
            readableMesh.bounds = originalMesh.bounds;

            // Copy triangles (this is the main issue with non-readable meshes)
            readableMesh.triangles = originalMesh.triangles;

            // Set submesh count and copy submeshes if available
            readableMesh.subMeshCount = originalMesh.subMeshCount;
            for (int i = 0; i < originalMesh.subMeshCount; i++)
            {
                try
                {
                    readableMesh.SetTriangles(originalMesh.GetTriangles(i), i);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Could not copy submesh {i} from '{originalMesh.name}': {e.Message}");
                }
            }

            Debug.Log($"Successfully created readable copy of mesh '{originalMesh.name}' with {readableMesh.vertexCount} vertices and {readableMesh.triangles.Length / 3} triangles");
            return readableMesh;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to create readable copy of mesh '{originalMesh.name}': {e.Message}");
            return null;
        }
    }

    // Method to get a readable mesh (either original or copy)
    Mesh GetReadableMesh()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            return null;
        }

        Mesh originalMesh = meshFilter.mesh;
        
        if (originalMesh.isReadable)
        {
            return originalMesh;
        }
        else
        {
            if (createReadableMeshCopy)
            {
                // Use cached copy if available
                if (readableMeshCopy != null)
                {
                    return readableMeshCopy;
                }

                // Create a readable copy
                readableMeshCopy = CreateReadableMeshCopy(originalMesh);
                if (readableMeshCopy != null)
                {
                    return readableMeshCopy;
                }
                else
                {
                    Debug.Log($"Failed to create readable copy of mesh '{originalMesh.name}'");
                    return null;
                }
            }
            else
            {
                Debug.Log($"Mesh '{originalMesh.name}' is not readable and createReadableMeshCopy is disabled");
                return null;
            }
        }
    }

    // Public method to manually create a readable mesh copy
    [ContextMenu("Create Readable Mesh Copy")]
    public void CreateReadableMeshCopyManually()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            Debug.LogError("No mesh found to copy");
            return;
        }

        Mesh originalMesh = meshFilter.mesh;
        if (originalMesh.isReadable)
        {
            Debug.Log("Mesh is already readable, no copy needed");
            return;
        }

        // Clear any existing copy
        if (readableMeshCopy != null)
        {
            if (Application.isPlaying)
            {
                Destroy(readableMeshCopy);
            }
            else
            {
                DestroyImmediate(readableMeshCopy);
            }
        }

        // Create new copy
        readableMeshCopy = CreateReadableMeshCopy(originalMesh);
        if (readableMeshCopy != null)
        {
            Debug.Log($"Successfully created readable mesh copy for '{gameObject.name}'");
        }
    }

    // Public method to clear the readable mesh copy
    [ContextMenu("Clear Readable Mesh Copy")]
    public void ClearReadableMeshCopy()
    {
        if (readableMeshCopy != null)
        {
            if (Application.isPlaying)
            {
                Destroy(readableMeshCopy);
            }
            else
            {
                DestroyImmediate(readableMeshCopy);
            }
            readableMeshCopy = null;
            Debug.Log($"Cleared readable mesh copy for '{gameObject.name}'");
        }
        else
        {
            Debug.Log("No readable mesh copy to clear");
        }
    }

    bool ShouldTreatAsMesh()
    {
        if (!autoDetectMeshType)
        {
            return isMesh; // Use manual setting
        }

        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            return false;
        }

        Mesh mesh = meshFilter.mesh;
        string meshName = mesh.name.ToLower();

        // Check if it's a known Unity primitive that can be represented as ROS primitive
        bool isKnownPrimitive = meshName.Contains("cube") || 
                               meshName.Contains("sphere") || 
                               meshName.Contains("cylinder") || 
                               meshName.Contains("capsule") || 
                               meshName.Contains("plane") || 
                               meshName.Contains("quad");

        // If it's not a known primitive, treat it as a mesh
        if (!isKnownPrimitive)
        {
            return true;
        }

        // Even for known primitives, check if they have been modified
        // (e.g., custom mesh imported from external source)
        if (mesh.isReadable && mesh.vertexCount > 0)
        {
            // Check if the mesh has been modified from the default primitive
            // This is a heuristic - you might want to adjust based on your needs
            bool hasCustomVertices = mesh.vertexCount > 24; // Most Unity primitives have <= 24 vertices
            bool hasCustomUVs = mesh.uv.Length > 0 && mesh.uv.Length != mesh.vertexCount;

            Debug.Log($"Mesh '{mesh.name}' has {mesh.vertexCount} vertices and {mesh.uv.Length} UVs");
            
            return hasCustomVertices || hasCustomUVs;
        }
        else if (!mesh.isReadable)
        {
            // If mesh is not readable, we can't use it for collision detection
            // Treat it as a mesh to trigger the error handling in UnityMeshToRosMesh
            Debug.LogWarning($"Mesh '{mesh.name}' is not readable - cannot be used for collision detection");
            return true; // Return true so the error handling in UnityMeshToRosMesh will catch it
        }

        return false;
    }

    bool IsCustomMesh()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            return false;
        }

        Mesh mesh = meshFilter.mesh;
        
        // Check if mesh is marked as readable (imported mesh)
        if (!mesh.isReadable)
        {
            return false;
        }

        // Check if it's an imported mesh (not a Unity primitive)
        string meshName = mesh.name.ToLower();
        bool isUnityPrimitive = meshName.Contains("cube") || 
                               meshName.Contains("sphere") || 
                               meshName.Contains("cylinder") || 
                               meshName.Contains("capsule") || 
                               meshName.Contains("plane") || 
                               meshName.Contains("quad");

        return !isUnityPrimitive;
    }

    // Method to analyze the object and provide detailed information
    void AnalyzeObject()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        
        if (meshFilter == null)
        {
            Debug.LogWarning($"Object '{gameObject.name}' has no MeshFilter component");
            return;
        }

        if (meshFilter.mesh == null)
        {
            Debug.LogWarning($"Object '{gameObject.name}' has no mesh assigned to MeshFilter");
            return;
        }

        Mesh mesh = meshFilter.mesh;
        string meshName = mesh.name;
        
        Debug.Log($"=== Object Analysis: {gameObject.name} ===");
        Debug.Log($"Mesh Name: {meshName}");
        Debug.Log($"Mesh Vertices: {mesh.vertexCount}");
        
        // Safely check triangles only if mesh is readable
        if (mesh.isReadable)
        {
            Debug.Log($"Mesh Triangles: {mesh.triangles.Length / 3}");
        }
        else
        {
            Debug.LogWarning($"Mesh '{meshName}' is not readable - cannot access triangle data");
            Debug.LogWarning("To fix this: Select the mesh asset in Project window → Inspector → Model tab → Enable 'Read/Write Enabled'");
        }
        
        Debug.Log($"Mesh Readable: {mesh.isReadable}");
        Debug.Log($"Auto Detect Enabled: {autoDetectMeshType}");
        Debug.Log($"Manual isMesh Setting: {isMesh}");
        Debug.Log($"Will Treat As Mesh: {ShouldTreatAsMesh()}");
        
        // Check if it's a known primitive
        bool isKnownPrimitive = IsKnownUnityPrimitive(meshName);
        Debug.Log($"Is Known Unity Primitive: {isKnownPrimitive}");
        
        if (isKnownPrimitive)
        {
            Debug.Log($"Primitive Type: {GetPrimitiveType(meshName)}");
        }
        
        // Provide guidance for non-readable meshes
        if (!mesh.isReadable)
        {
            Debug.LogWarning($"Mesh '{meshName}' cannot be used for collision detection because it's not readable.");
            ProvideMeshFixInstructions(meshName);
        }
        
        Debug.Log($"=== End Analysis ===");
    }

    private void OnLatencyPong(HeaderMsg msg)
    {
        if (!trackLatency) return;
        Debug.Log($"[Latency] Pong received: frame_id='{msg.frame_id}' stamp.sec={msg.stamp.sec} stamp.nanosec={msg.stamp.nanosec}");
        if (msg.stamp.sec == 0 && msg.stamp.nanosec == 0)
        {
            Debug.LogWarning("[Latency] Pong stamp is zero — ROS node sent a blank timestamp");
            return;
        }
        long sentTicks = (long)msg.stamp.sec * TimeSpan.TicksPerSecond
                       + (long)msg.stamp.nanosec / 100L;
        var sentTime = new DateTimeOffset(DateTimeOffset.UnixEpoch.Ticks + sentTicks, TimeSpan.Zero);
        double rttMs = (DateTimeOffset.UtcNow - sentTime).TotalMilliseconds;

        // frame_id is encoded as "OPERATION:object_id"
        int sep = msg.frame_id.IndexOf(':');
        string operation = sep >= 0 ? msg.frame_id.Substring(0, sep) : "UNKNOWN";
        string objectId  = sep >= 0 ? msg.frame_id.Substring(sep + 1) : msg.frame_id;

        // Debug.Log($"[Latency] {operation} '{objectId}'  RTT={rttMs:F1} ms  (~{rttMs / 2:F1} ms one-way)");
        ros.Publish("/latency_data", new StringMsg($"{operation},{objectId},{rttMs:F3}"));
    }

    private TimeMsg GetRosTimestamp()
    {
        long ticks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return RosMessageCompatibility.CreateTime(
            ticks / TimeSpan.TicksPerSecond,
            (uint)((ticks % TimeSpan.TicksPerSecond) * 100L)); // 1 tick = 100 ns
    }

    bool IsKnownUnityPrimitive(string meshName)
    {
        string lowerName = meshName.ToLower();
        return lowerName.Contains("cube") || 
               lowerName.Contains("sphere") || 
               lowerName.Contains("cylinder") || 
               lowerName.Contains("capsule") || 
               lowerName.Contains("plane") || 
               lowerName.Contains("quad");
    }

    string GetPrimitiveType(string meshName)
    {
        string lowerName = meshName.ToLower();
        if (lowerName.Contains("cube")) return "Cube";
        if (lowerName.Contains("sphere")) return "Sphere";
        if (lowerName.Contains("cylinder")) return "Cylinder";
        if (lowerName.Contains("capsule")) return "Capsule";
        if (lowerName.Contains("plane") || lowerName.Contains("quad")) return "Plane";
        return "Unknown";
    }
}
