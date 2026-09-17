using System;
using UnityEngine;

// Spawns the scene's hand-authored default objects through the factory at startup, so they
// carry the same grab/scene-graph component stack as palette-spawned ones. The defaults
// mirror preference_rl.ros.scene_world.default_scene() (positions in the ROS base frame).
public class FERLDefaultObjects : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public string id = "object";
        public FerlObjectType type = FerlObjectType.Other;
        [Tooltip("Position in the ROS base frame (x forward, y left, z up), metres.")]
        public Vector3 rosPosition;
        public bool editable = true;
        public bool open;
        public bool damageSensitive;
        public bool fragile;
        public bool hot;
        public Color colorOverride = Color.clear;
    }

    [SerializeField] private FERLObjectFactory factory;
    [SerializeField] private SceneGraphPublisher publisher;
    [SerializeField] private bool spawnOnStart = true;
    [SerializeField] private Entry[] entries =
    {
        new Entry { id = "table", type = FerlObjectType.Table, rosPosition = new Vector3(0.50f, 0.00f, -0.02f), editable = false },
        new Entry { id = "laptop", type = FerlObjectType.Laptop, rosPosition = new Vector3(0.50f, 0.20f, 0.00f), editable = true, damageSensitive = true },
        new Entry { id = "cup", type = FerlObjectType.Cup, rosPosition = new Vector3(0.55f, -0.10f, 0.05f), editable = true },
        new Entry { id = "blue_marker", type = FerlObjectType.Marker, rosPosition = new Vector3(0.26f, 0.20f, 0.25f), editable = false, colorOverride = new Color(0.20f, 0.50f, 1.00f) },
        new Entry { id = "orange_marker", type = FerlObjectType.Marker, rosPosition = new Vector3(0.72f, 0.20f, 0.25f), editable = false, colorOverride = new Color(1.00f, 0.45f, 0.10f) },
    };

    public bool Spawned { get; private set; }

    private void Start()
    {
        if (spawnOnStart)
            SpawnAll();
    }

    public void SpawnAll()
    {
        if (Spawned)
            return;
        if (factory == null)
            factory = FindFirstObjectByType<FERLObjectFactory>();
        if (publisher == null)
            publisher = SceneGraphPublisher.Instance ?? FindFirstObjectByType<SceneGraphPublisher>();
        Transform baseTransform = publisher != null ? publisher.BaseTransform : null;
        if (factory == null || baseTransform == null)
        {
            Debug.LogError("[FERLDefaultObjects] Needs a FERLObjectFactory and a SceneGraphPublisher with a BaseTransform.");
            return;
        }

        foreach (Entry entry in entries)
        {
            // ROS (x, y, z) -> BaseTransform local (x, z, y) -> world.
            Vector3 local = new Vector3(entry.rosPosition.x, entry.rosPosition.z, entry.rosPosition.y);
            SceneGraphObject obj = factory.Spawn(entry.type, baseTransform.TransformPoint(local), entry.id);
            obj.editable = entry.editable;
            obj.open = entry.open;
            obj.damageSensitive = entry.damageSensitive;
            obj.fragile = entry.fragile;
            obj.hot = entry.hot;
            if (entry.colorOverride.a > 0f)
                FERLObjectFactory.Tint(obj.gameObject, entry.colorOverride);
        }
        Spawned = true;
        publisher.MarkDirty();
    }
}
