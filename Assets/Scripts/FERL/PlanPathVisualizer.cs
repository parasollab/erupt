using System;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Draws the current plan's end-effector path (/ferl/plan_path, robot base frame) as a line
// under the BaseTransform, and keeps the previous plan's path in a faded colour so a replan
// after a correction or a learned feature is visible next to what it replaced.
public class PlanPathVisualizer : MonoBehaviour
{
    [SerializeField] private Transform baseTransform;
    [SerializeField] private string pathTopic = "/ferl/plan_path";
    [SerializeField] private float lineWidth = 0.008f;
    [SerializeField] private Color currentColor = new Color(0.2f, 1f, 0.3f, 0.95f);
    [SerializeField] private Color previousColor = new Color(1f, 0.35f, 0.25f, 0.5f);
    [SerializeField] private bool showPreviousPlan = true;
    [SerializeField] private float waypointMarkerSize = 0.012f;

    public int PlanSeq { get; private set; }
    public int PointCount { get; private set; }

    private ROSConnection ros;
    private LineRenderer current;
    private LineRenderer previous;
    private readonly List<Vector3> currentPoints = new List<Vector3>();
    private readonly List<GameObject> markers = new List<GameObject>();
    private Material markerMaterial;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        StringMsg.Register();
        ros.Subscribe<StringMsg>(pathTopic, OnPath);
        if (baseTransform == null && SceneGraphPublisher.Instance != null)
            baseTransform = SceneGraphPublisher.Instance.BaseTransform;
    }

    private void OnDestroy()
    {
        if (ros != null)
            ros.Unsubscribe<StringMsg>(pathTopic, OnPath);
    }

    public void Clear()
    {
        if (current != null) current.positionCount = 0;
        if (previous != null) previous.positionCount = 0;
        foreach (GameObject marker in markers) Destroy(marker);
        markers.Clear();
        currentPoints.Clear();
        PointCount = 0;
    }

    private LineRenderer MakeLine(string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(baseTransform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = color;
        line.endColor = color;
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.numCornerVertices = 4;
        line.numCapVertices = 4;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.positionCount = 0;
        return line;
    }

    private void OnPath(StringMsg msg)
    {
        List<Vector3> points;
        int seq, waypoints;
        try
        {
            points = Parse(msg.data, out seq, out waypoints);
        }
        catch (Exception error)
        {
            Debug.LogWarning($"[PlanPathVisualizer] bad path JSON: {error.Message}");
            return;
        }
        if (baseTransform == null && SceneGraphPublisher.Instance != null)
            baseTransform = SceneGraphPublisher.Instance.BaseTransform;
        if (current == null) current = MakeLine("PlanPath (current)", currentColor);
        if (previous == null) previous = MakeLine("PlanPath (previous)", previousColor);

        // The old current path becomes the faded previous one.
        if (showPreviousPlan && currentPoints.Count > 0 && seq != PlanSeq)
        {
            previous.positionCount = currentPoints.Count;
            previous.SetPositions(currentPoints.ToArray());
        }
        currentPoints.Clear();
        currentPoints.AddRange(points);
        current.positionCount = points.Count;
        current.SetPositions(points.ToArray());
        PlanSeq = seq;
        PointCount = points.Count;

        // A small marker at each waypoint (every `substeps`-th point).
        foreach (GameObject marker in markers) Destroy(marker);
        markers.Clear();
        int stride = waypoints > 1 ? Mathf.Max(1, (points.Count - 1) / (waypoints - 1)) : 1;
        if (markerMaterial == null)
        {
            markerMaterial = new Material(Shader.Find("Sprites/Default"));
            markerMaterial.color = currentColor;
        }
        for (int i = 0; i < points.Count; i += stride)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"waypoint {i / stride}";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(baseTransform, false);
            marker.transform.localPosition = points[i];
            marker.transform.localScale = Vector3.one * waypointMarkerSize;
            marker.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;
            markers.Add(marker);
        }
        Debug.Log($"[PlanPathVisualizer] plan {seq}: {points.Count} path points, {markers.Count} waypoints.");
    }

    // {"plan_seq": N, "waypoints": W, "points": [[x, y, z], ...]} in ROS base coordinates.
    private static List<Vector3> Parse(string json, out int seq, out int waypoints)
    {
        seq = ReadInt(json, "\"plan_seq\"");
        waypoints = ReadInt(json, "\"waypoints\"");
        var result = new List<Vector3>();
        int start = json.IndexOf("\"points\"", StringComparison.Ordinal);
        if (start < 0) return result;
        int i = json.IndexOf('[', start) + 1;
        var numbers = new List<float>(3);
        while (i < json.Length)
        {
            char c = json[i];
            if (c == ']' && json[i - 1] == ']') break;
            if (c == '-' || c == '.' || char.IsDigit(c))
            {
                int j = i;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-' || json[j] == 'e' || json[j] == 'E' || json[j] == '+')) j++;
                numbers.Add(float.Parse(json.Substring(i, j - i), System.Globalization.CultureInfo.InvariantCulture));
                i = j;
                if (numbers.Count == 3)
                {
                    // ROS (x, y, z) -> BaseTransform local (x, z, y).
                    result.Add(new Vector3(numbers[0], numbers[2], numbers[1]));
                    numbers.Clear();
                }
                continue;
            }
            i++;
        }
        return result;
    }

    private static int ReadInt(string json, string key)
    {
        int k = json.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return 0;
        int i = json.IndexOf(':', k) + 1;
        while (i < json.Length && json[i] == ' ') i++;
        int j = i;
        while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
        return int.TryParse(json.Substring(i, j - i), out int value) ? value : 0;
    }
}
