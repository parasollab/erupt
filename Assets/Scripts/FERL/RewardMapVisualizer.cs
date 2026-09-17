using System;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Draws the bridge's reward map (/ferl/reward_map): sampled end-effector positions in the
// robot base frame, coloured by the value of one feature or the weighted total cost, as one
// vertex-coloured mesh of small cubes under the BaseTransform. Blue = low, red = high.
public class RewardMapVisualizer : MonoBehaviour
{
    [Serializable]
    private class MapPayload
    {
        public string feature = "";
        public string[] features = Array.Empty<string>();
        public float[] weights = Array.Empty<float>();
        public int count;
        public float min;
        public float max;
        public int plan_seq;
        public float[][] points;   // JsonUtility cannot read nested arrays; parsed by hand below
    }

    [SerializeField] private Transform baseTransform;
    [SerializeField] private string mapTopic = "/ferl/reward_map";
    [SerializeField] private float cubeSize = 0.015f;
    [SerializeField, Range(0.1f, 1f)] private float alpha = 0.55f;
    [SerializeField] private Gradient colors;

    public string CurrentFeature { get; private set; } = "";
    public int PointCount { get; private set; }
    public float MinValue { get; private set; }
    public float MaxValue { get; private set; }
    public bool IsVisible => meshObject != null && meshObject.activeSelf;
    public event Action<RewardMapVisualizer> Updated;

    private ROSConnection ros;
    private GameObject meshObject;
    private Mesh mesh;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        StringMsg.Register();
        ros.Subscribe<StringMsg>(mapTopic, OnMap);
        if (baseTransform == null && SceneGraphPublisher.Instance != null)
            baseTransform = SceneGraphPublisher.Instance.BaseTransform;
        if (colors == null || colors.colorKeys.Length == 0)
        {
            colors = new Gradient();
            colors.SetKeys(
                new[] { new GradientColorKey(new Color(0.15f, 0.35f, 1f), 0f), new GradientColorKey(new Color(0.2f, 0.9f, 0.4f), 0.5f), new GradientColorKey(new Color(1f, 0.2f, 0.1f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        }
    }

    private void OnDestroy()
    {
        if (ros != null)
            ros.Unsubscribe<StringMsg>(mapTopic, OnMap);
    }

    public void Clear()
    {
        if (meshObject != null)
            meshObject.SetActive(false);
        CurrentFeature = "";
        PointCount = 0;
        Updated?.Invoke(this);
    }

    private void OnMap(StringMsg msg)
    {
        MapPayload header;
        List<Vector4> points;
        try
        {
            header = JsonUtility.FromJson<MapPayload>(msg.data);
            points = ParsePoints(msg.data);
        }
        catch (Exception error)
        {
            Debug.LogWarning($"[RewardMapVisualizer] bad map JSON: {error.Message}");
            return;
        }
        if (header == null || header.feature == "off" || points.Count == 0)
        {
            Clear();
            return;
        }
        CurrentFeature = header.feature;
        PointCount = points.Count;
        MinValue = header.min;
        MaxValue = header.max;
        Build(points, header.min, header.max);
        Debug.Log($"[RewardMapVisualizer] {header.feature}: {points.Count} points, {header.min:F3}..{header.max:F3}");
        Updated?.Invoke(this);
    }

    // "points": [[x, y, z, v], ...] — a flat numeric scan is enough for this fixed schema.
    private static List<Vector4> ParsePoints(string json)
    {
        var result = new List<Vector4>();
        int start = json.IndexOf("\"points\"", StringComparison.Ordinal);
        if (start < 0) return result;
        start = json.IndexOf('[', start);
        var numbers = new List<float>(4);
        int i = start + 1;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == ']' && json[i - 1] == ']') break;          // end of the outer array
            if (c == '-' || c == '.' || char.IsDigit(c))
            {
                int j = i;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-' || json[j] == 'e' || json[j] == 'E' || json[j] == '+')) j++;
                numbers.Add(float.Parse(json.Substring(i, j - i), System.Globalization.CultureInfo.InvariantCulture));
                i = j;
                if (numbers.Count == 4)
                {
                    result.Add(new Vector4(numbers[0], numbers[1], numbers[2], numbers[3]));
                    numbers.Clear();
                }
                continue;
            }
            i++;
        }
        return result;
    }

    private void Build(List<Vector4> points, float min, float max)
    {
        if (meshObject == null)
        {
            meshObject = new GameObject("RewardMap");
            meshObject.transform.SetParent(baseTransform, false);
            var filter = meshObject.AddComponent<MeshFilter>();
            var renderer = meshObject.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Sprites/Default");
            renderer.material = new Material(shader);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            filter.sharedMesh = mesh;
        }
        meshObject.transform.SetParent(baseTransform, false);
        meshObject.transform.localPosition = Vector3.zero;
        meshObject.transform.localRotation = Quaternion.identity;
        meshObject.SetActive(true);

        float range = Mathf.Max(1e-6f, max - min);
        float h = cubeSize * 0.5f;
        var verts = new Vector3[points.Count * 8];
        var cols = new Color[points.Count * 8];
        var tris = new int[points.Count * 36];
        int[] cube = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        for (int p = 0; p < points.Count; p++)
        {
            Vector4 pt = points[p];
            // ROS (x, y, z) -> BaseTransform local (x, z, y).
            Vector3 c = new Vector3(pt.x, pt.z, pt.y);
            Color color = colors.Evaluate((pt.w - min) / range);
            color.a = alpha;
            int b = p * 8;
            verts[b + 0] = c + new Vector3(-h, -h, -h); verts[b + 1] = c + new Vector3(h, -h, -h);
            verts[b + 2] = c + new Vector3(h, -h, h);  verts[b + 3] = c + new Vector3(-h, -h, h);
            verts[b + 4] = c + new Vector3(-h, h, -h);  verts[b + 5] = c + new Vector3(h, h, -h);
            verts[b + 6] = c + new Vector3(h, h, h);   verts[b + 7] = c + new Vector3(-h, h, h);
            for (int k = 0; k < 8; k++) cols[b + k] = color;
            for (int k = 0; k < 36; k++) tris[p * 36 + k] = b + cube[k];
        }
        mesh.Clear();
        mesh.vertices = verts;
        mesh.colors = cols;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
    }
}
