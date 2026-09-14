using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using UnityEngine;
using Erupt.Ros;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Samples the meshes of tagged scene objects into a <c>sensor_msgs/PointCloud2</c> and
    /// republishes the cached cloud periodically. RADER's <c>PointCloudPublisher</c> on
    /// <see cref="IRosBus"/>; the plugin calls <see cref="Initialise"/>, otherwise
    /// <c>RosBus.Instance</c> is used. Does nothing when no tags are configured.
    /// </summary>
    public class PointCloudPublisher : MonoBehaviour
    {
        [SerializeField] private string topicName = "/point_cloud";
        [Tooltip("Barycentric step between sampled points on each triangle.")]
        [SerializeField, Range(0.01f, 1f)] private float samplingResolution = 0.1f;
        [Tooltip("Tags of objects to include in the point cloud.")]
        [SerializeField] private string[] includeTags = System.Array.Empty<string>();
        [Tooltip("Seconds between republishes of the cached cloud.")]
        [SerializeField, Min(0.1f)] private float publishInterval = 5.0f;
        [SerializeField] private string frameId = "map";

        private IRosBus ros;
        private byte[] cachedPointCloudData;
        private uint pointCount;
        private float nextPublishAt;
        private bool registered;

        public string Topic => topicName;
        public uint PointCount => pointCount;

        public void Initialise(IRosBus bus)
        {
            ros = bus;
            Register();
        }

        private void Start()
        {
            ros ??= RosBus.Instance;
            Register();
            if (includeTags == null || includeTags.Length == 0) return;
            Rebuild();
            PublishCached();
            nextPublishAt = Time.time + publishInterval;
        }

        private void Update()
        {
            if (cachedPointCloudData == null || Time.time < nextPublishAt) return;
            PublishCached();
            nextPublishAt = Time.time + publishInterval;
        }

        private void Register()
        {
            if (registered || ros == null) return;
            registered = true;
            ros.RegisterPublisher<PointCloud2Msg>(topicName);
        }

        /// <summary>Re-sample the tagged meshes (call after the scene changes).</summary>
        public void Rebuild()
        {
            var pointList = new List<Vector3>();
            foreach (string tag in includeTags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                foreach (GameObject obj in GameObject.FindGameObjectsWithTag(tag))
                {
                    var meshFilter = obj.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                    SampleMesh(meshFilter.sharedMesh, meshFilter.transform, pointList);
                }
            }

            var points = new float[pointList.Count * 3];
            for (int i = 0; i < pointList.Count; i++)
            {
                points[i * 3] = pointList[i].x;
                points[i * 3 + 1] = pointList[i].y;
                points[i * 3 + 2] = pointList[i].z;
            }

            pointCount = (uint)pointList.Count;
            cachedPointCloudData = new byte[points.Length * 4];
            System.Buffer.BlockCopy(points, 0, cachedPointCloudData, 0, cachedPointCloudData.Length);
        }

        private void SampleMesh(Mesh mesh, Transform meshTransform, List<Vector3> into)
        {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            float step = Mathf.Max(samplingResolution, 0.01f);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector3 v0 = meshTransform.TransformPoint(vertices[triangles[i]]);
                Vector3 v1 = meshTransform.TransformPoint(vertices[triangles[i + 1]]);
                Vector3 v2 = meshTransform.TransformPoint(vertices[triangles[i + 2]]);
                for (float u = 0; u < 1.0f; u += step)
                    for (float v = 0; u + v < 1.0f; v += step)
                        into.Add(ToRos((1 - u - v) * v0 + u * v1 + v * v2));
            }
        }

        /// <summary>Unity (x right, y up, z forward) → ROS (x forward, y left, z up), as RADER did it.</summary>
        private static Vector3 ToRos(Vector3 unityPoint) => new(unityPoint.x, unityPoint.z, -unityPoint.y);

        private void PublishCached()
        {
            if (ros == null || cachedPointCloudData == null) return;
            float t = Time.time;
            var pointCloud = new PointCloud2Msg
            {
                header = new HeaderMsg
                {
                    frame_id = frameId,
                    stamp = new TimeMsg { sec = (int)t, nanosec = (uint)((t - (int)t) * 1e9) }
                },
                height = 1,
                width = pointCount,
                fields = new[]
                {
                    new PointFieldMsg { name = "x", offset = 0, datatype = PointFieldMsg.FLOAT32, count = 1 },
                    new PointFieldMsg { name = "y", offset = 4, datatype = PointFieldMsg.FLOAT32, count = 1 },
                    new PointFieldMsg { name = "z", offset = 8, datatype = PointFieldMsg.FLOAT32, count = 1 }
                },
                is_bigendian = false,
                point_step = 12,
                row_step = pointCount * 12,
                data = cachedPointCloudData,
                is_dense = true
            };
            ros.Publish(topicName, pointCloud);
        }
    }
}
