using UnityEngine;
using Erupt.Environment;

/// <summary>
/// Keeps MoveIt's planning scene in step with the environment mirror
/// (<see cref="EnvironmentRegistry"/>). Sits next to <see cref="CollisionObjectsListenerSimple"/>,
/// which handles the other direction (ROS → Unity).
/// </summary>
/// <remarks>
/// Core builds obstacles without knowing about MoveIt; this component reacts to
/// <see cref="EnvironmentRegistry.Added"/> and gives each Unity-owned object a
/// <see cref="CollisionObjectPublisher"/>, and to <see cref="EnvironmentRegistry.Removed"/>
/// so a removal commanded by ROS is not echoed back as a REMOVE. Objects the listener
/// already knows as Unity-owned (scene-authored publishers, anchor visuals that publish
/// themselves) are left alone: their owner handles publishing.
/// </remarks>
[DisallowMultipleComponent]
public class MoveItPlanningSceneSync : MonoBehaviour, IEnvironmentSync
{
    [SerializeField] private EnvironmentRegistry registry;
    [SerializeField] private CollisionObjectsListenerSimple listener;

    public string SyncId => "moveit";
    public EnvironmentRegistry Registry => registry;

    void Awake()
    {
        if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
        if (listener == null) listener = GetComponent<CollisionObjectsListenerSimple>();
        if (listener == null) listener = FindFirstObjectByType<CollisionObjectsListenerSimple>();

        if (registry == null)
        {
            Debug.LogError("[MoveItPlanningSceneSync] No EnvironmentRegistry in the scene; Unity-built obstacles will not reach MoveIt.");
            return;
        }

        registry.Added += OnAdded;
        registry.Removed += OnRemoved;

        // Objects registered before this component woke (e.g. spawned in the same frame).
        foreach (var obj in registry.All) OnAdded(obj);
    }

    void OnDestroy()
    {
        if (registry == null) return;
        registry.Added -= OnAdded;
        registry.Removed -= OnRemoved;
    }

    void OnAdded(EnvironmentObject obj)
    {
        if (obj == null || obj.Owner != EnvironmentOwner.Unity) return;
        if (listener != null && listener.IsUnityOwned(obj.Id)) return;   // owner publishes itself

        GameObject go = obj.gameObject;
        var publisher = go.GetComponent<CollisionObjectPublisher>();
        if (publisher == null)
        {
            publisher = go.AddComponent<CollisionObjectPublisher>();
            publisher.isMesh = obj.IsMesh;
            publisher.objectId = obj.Id;
            publisher.worldOrigin = registry.WorldOriginObject;
        }

        listener?.RegisterUnityOwnedObject(obj.Id, go);
    }

    void OnRemoved(EnvironmentObject obj, RemovalOrigin origin)
    {
        if (obj == null) return;

        if (origin == RemovalOrigin.Remote)
        {
            // ROS commanded this removal; the publisher must not echo a REMOVE back.
            foreach (var pub in obj.GetComponentsInChildren<CollisionObjectPublisher>(true))
                pub.suppressRemoveOnDestroy = true;
        }

        if (obj.Owner == EnvironmentOwner.Unity)
            listener?.UnregisterUnityOwnedObject(obj.Id);
    }
}
