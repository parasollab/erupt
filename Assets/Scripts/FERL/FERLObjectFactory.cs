using UnityEngine;

// Builds a grabbable, typed scene-graph object out of Unity primitives. Sizes follow the
// FERL laptop world (preference_rl.preference.laptop_scene) and preference_rl.ros.scene_world's
// default scene, in Unity axes: x = ROS x, y = ROS z (up), z = ROS y.
public class FERLObjectFactory : MonoBehaviour
{
    [SerializeField] private Material litMaterial;
    [SerializeField, Range(0.1f, 1f)] private float alpha = 0.85f;
    [Tooltip("Optional parent for spawned objects (e.g. the FERL Objects container).")]
    [SerializeField] private Transform container;

    [Header("Colors")]
    [SerializeField] private Color laptopColor = new Color(0.25f, 0.25f, 0.28f);
    [SerializeField] private Color cupColor = new Color(0.85f, 0.85f, 0.90f);
    [SerializeField] private Color tableColor = new Color(0.55f, 0.40f, 0.25f);
    [SerializeField] private Color humanColor = new Color(0.90f, 0.70f, 0.55f);
    [SerializeField] private Color markerColor = new Color(0.20f, 0.50f, 1.00f);
    [SerializeField] private Color otherColor = new Color(0.60f, 0.60f, 0.60f);

    public Transform Container => container;

    public SceneGraphObject Spawn(FerlObjectType type, Vector3 worldPosition, string objectId = null)
    {
        GameObject root = BuildVisual(type);
        root.name = string.IsNullOrEmpty(objectId) ? UniqueId(FerlObjectTypes.ToName(type)) : objectId;
        root.transform.SetParent(container, false);
        root.transform.position = worldPosition;
        root.transform.rotation = container != null ? container.rotation : Quaternion.identity;

        GrabbableShapeSetup.Configure(root, litMaterial, alpha);
        Tint(root, ColorFor(type));

        SceneGraphObject node = root.AddComponent<SceneGraphObject>();
        node.ObjectId = root.name;
        node.type = type;
        node.editable = type != FerlObjectType.Marker;
        if (type == FerlObjectType.Laptop)
            node.damageSensitive = true;
        return node;
    }

    public Color ColorFor(FerlObjectType type)
    {
        switch (type)
        {
            case FerlObjectType.Laptop: return laptopColor;
            case FerlObjectType.Cup: return cupColor;
            case FerlObjectType.Table: return tableColor;
            case FerlObjectType.Human: return humanColor;
            case FerlObjectType.Marker: return markerColor;
            default: return otherColor;
        }
    }

    public static void Tint(GameObject root, Color color)
    {
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            Material material = renderer.material;
            Color c = color;
            c.a = material.color.a;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", c);
            material.color = c;
        }
    }

    public static string UniqueId(string prefix)
    {
        var used = new System.Collections.Generic.HashSet<string>();
        foreach (SceneGraphObject obj in SceneGraphObject.All)
            if (obj != null) used.Add(obj.ObjectId);
        int index = 1;
        string candidate = prefix;
        while (used.Contains(candidate) || GameObject.Find(candidate) != null)
            candidate = $"{prefix}_{index++}";
        return candidate;
    }

    // The pivot is the object's scene-graph position: at the floor contact for the laptop (so
    // position.z = 0 puts it on the ground plane), at the centre for everything else.
    private static GameObject BuildVisual(FerlObjectType type)
    {
        switch (type)
        {
            case FerlObjectType.Laptop:
            {
                var root = new GameObject("laptop");
                GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slab.name = "base";
                slab.transform.SetParent(root.transform, false);
                slab.transform.localScale = new Vector3(0.22f, 0.02f, 0.30f);
                slab.transform.localPosition = new Vector3(0f, 0.01f, 0f);
                GameObject screen = GameObject.CreatePrimitive(PrimitiveType.Cube);
                screen.name = "screen";
                screen.transform.SetParent(root.transform, false);
                screen.transform.localScale = new Vector3(0.22f, 0.18f, 0.016f);
                screen.transform.localPosition = new Vector3(0f, 0.11f, 0.142f);
                return root;
            }
            case FerlObjectType.Cup:
            {
                GameObject cup = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cup.transform.localScale = new Vector3(0.08f, 0.05f, 0.08f);
                return cup;
            }
            case FerlObjectType.Table:
            {
                var root = new GameObject("table");
                GameObject top = GameObject.CreatePrimitive(PrimitiveType.Cube);
                top.name = "top";
                top.transform.SetParent(root.transform, false);
                top.transform.localScale = new Vector3(0.80f, 0.04f, 1.00f);
                // Pivot at the slab centre: the default scene places the table at z = -0.02 so
                // its top is the ground plane the bridge plans over.
                top.transform.localPosition = Vector3.zero;
                return root;
            }
            case FerlObjectType.Human:
            {
                GameObject human = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                human.transform.localScale = new Vector3(0.12f, 0.80f, 0.12f);
                return human;
            }
            case FerlObjectType.Marker:
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.transform.localScale = Vector3.one * 0.05f;
                return marker;
            }
            default:
            {
                GameObject other = GameObject.CreatePrimitive(PrimitiveType.Cube);
                other.transform.localScale = Vector3.one * 0.10f;
                return other;
            }
        }
    }
}
