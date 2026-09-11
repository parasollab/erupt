using Erupt.Ros;
using Erupt.Environment;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;

/// <summary>
/// Mirrors MoveIt's attach state onto Unity GameObjects. The ROS-side
/// planning_scene_watcher publishes an AttachedCollisionObject on
/// /attached_collision_objects_ros whenever an object is attached to (op ADD) or
/// detached from (op REMOVE) a robot link. On attach the mirrored GameObject is
/// reparented under the link Transform so it follows the gripper; on detach it is
/// reparented back under the world origin at its current world pose.
/// </summary>
public class AttachedCollisionObjectListener : MonoBehaviour
{
    [Header("ROS")]
    public string topic = "/attached_collision_objects_ros";

    [Header("References")]
    [SerializeField] private CollisionObjectsListenerSimple sceneListener;
    [SerializeField] private EnvironmentRegistry registry;
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private GameObject worldOrigin; // parent restored on detach

    // id -> parent Transform before the attach, restored on detach
    private readonly Dictionary<string, Transform> originalParent = new();
    // Attach messages that arrived before the object existed: id -> link_name
    private readonly Dictionary<string, string> pendingAttach = new();
    // Detached ids whose publishers stay paused until the next inbound update
    // (so Unity's possibly-stale pose isn't pushed back into MoveIt)
    private readonly HashSet<string> pendingResume = new();

    void Start()
    {
        if (sceneListener == null) sceneListener = FindFirstObjectByType<CollisionObjectsListenerSimple>();
        if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
        if (ikController == null) ikController = FindFirstObjectByType<DirectArticulationIKController>();

        if (sceneListener != null)
            sceneListener.OnObjectUpdated += OnObjectUpdated;

        RosBus.Instance.Subscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);
    }

    void OnDestroy()
    {
        if (sceneListener != null)
            sceneListener.OnObjectUpdated -= OnObjectUpdated;
    }

    void OnAttachedCollisionObject(AttachedCollisionObjectMsg msg)
    {
        string id = msg.@object.id;
        if (string.IsNullOrEmpty(id)) return;

        if (msg.@object.operation == CollisionObjectMsg.REMOVE)
            Detach(id);
        else
            Attach(id, msg.link_name);
    }

    void Attach(string id, string linkName)
    {
        if (sceneListener == null) return;

        if (!sceneListener.TryGetObject(id, out var go))
        {
            Debug.LogWarning($"[ACO Listener] Attach for unknown object '{id}'; will retry when it appears.");
            pendingAttach[id] = linkName;
            sceneListener.attachedIds.Add(id); // protect it from a racing REMOVE
            return;
        }

        Transform link = ikController != null ? ikController.FindLinkTransform(linkName) : null;
        if (link == null)
        {
            Debug.LogError($"[ACO Listener] Cannot attach '{id}': no link transform for '{linkName}'.");
            return;
        }

        sceneListener.attachedIds.Add(id);
        pendingAttach.Remove(id);
        if (!originalParent.ContainsKey(id))
            originalParent[id] = go.transform.parent;

        go.transform.SetParent(link, worldPositionStays: true);
        SetPublishingPaused(go, true);
        registry?.Attach(id, link);
        Debug.Log($"[ACO Listener] Attached '{id}' to link '{linkName}'.");
    }

    void Detach(string id)
    {
        pendingAttach.Remove(id);
        if (sceneListener == null) return;
        sceneListener.attachedIds.Remove(id);

        if (!sceneListener.TryGetObject(id, out var go))
        {
            originalParent.Remove(id);
            return;
        }

        Transform parent = originalParent.TryGetValue(id, out var saved) && saved
            ? saved
            : (worldOrigin != null ? worldOrigin.transform : null);
        originalParent.Remove(id);

        go.transform.SetParent(parent, worldPositionStays: true);
        registry?.Detach(id);
        // Keep the publisher paused until ROS sends the authoritative post-detach pose;
        // resumed in OnObjectUpdated.
        pendingResume.Add(id);
        Debug.Log($"[ACO Listener] Detached '{id}'.");
    }

    void OnObjectUpdated(string id)
    {
        if (pendingAttach.TryGetValue(id, out var linkName))
        {
            Attach(id, linkName);
            return;
        }

        if (pendingResume.Remove(id) && sceneListener.TryGetObject(id, out var go))
            SetPublishingPaused(go, false);
    }

    static void SetPublishingPaused(GameObject go, bool paused)
    {
        foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
            pub.pausePublishing = paused;
    }
}
