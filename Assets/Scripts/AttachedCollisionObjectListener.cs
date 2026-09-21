using Erupt.Ros;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using RosMessageTypes.Moveit;
using PoseMsg = RosMessageTypes.Geometry.PoseMsg;

/// <summary>
/// Mirrors MoveIt's attach state onto Unity GameObjects. The ROS-side
/// planning_scene_watcher publishes an AttachedCollisionObject on
/// /attached_collision_objects_ros whenever an object is attached to (op ADD) or
/// detached from (op REMOVE) a robot link. On attach the mirrored GameObject is
/// reparented under the link Transform at the link-relative pose from the message, so it
/// follows the gripper; on detach it is reparented back at its current world pose and the
/// ADD that follows on /collision_objects_ros sets the exact place pose.
/// While attached the object is locked: nothing is published for its id, physics cannot
/// move it, and the participant cannot grab it.
/// </summary>
public class AttachedCollisionObjectListener : MonoBehaviour
{
    [Header("ROS")]
    public string topic = "/attached_collision_objects_ros";

    [Header("References")]
    [SerializeField] private CollisionObjectsListenerSimple sceneListener;
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private GameObject worldOrigin; // parent restored on detach when the original is gone

    [Header("Robot name mapping")]
    [Tooltip("Link prefix used by the ROS robot, e.g. \"panda_\". Link names the Unity robot " +
             "does not know are retried with Unity Name Prefix instead.")]
    [SerializeField] private string rosNamePrefix = "panda_";
    [Tooltip("Link prefix of the Unity robot, e.g. \"fr3_\".")]
    [SerializeField] private string unityNamePrefix = "fr3_";

    [Tooltip("Seconds to wait after a detach for the re-ADD carrying the place pose before " +
             "letting the object publish again anyway.")]
    [SerializeField] private float resumeTimeout = 2f;

    // Everything changed on attach, so detach can put it back.
    private class AttachState
    {
        public GameObject go;
        public bool reparented;
        public Transform originalParent;
        public Vector3 originalLocalScale;
        public readonly List<(Rigidbody rb, bool wasKinematic)> bodies = new();
        public readonly List<(XRGrabInteractable grab, bool wasEnabled)> grabs = new();
        public readonly List<SelectableGrabController> grabControllers = new();
    }

    private readonly Dictionary<string, AttachState> attached = new();
    // Detached ids whose publishers stay silent until the authoritative post-detach pose
    // arrives (so Unity's possibly-stale pose isn't pushed back into MoveIt): id -> deadline
    private readonly Dictionary<string, float> pendingResume = new();
    private readonly List<string> expired = new();

    private IRosBus ros;

    void Start()
    {
        if (sceneListener == null) sceneListener = FindFirstObjectByType<CollisionObjectsListenerSimple>();
        if (ikController == null) ikController = FindFirstObjectByType<DirectArticulationIKController>();

        if (sceneListener != null)
        {
            sceneListener.OnObjectUpdated += OnObjectUpdated;
            sceneListener.OnRegistryCleared += OnRegistryCleared;
        }

        ros = RosBus.Instance;
        ros.Subscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);
    }

    void OnDestroy()
    {
        if (ros != null)
            ros.Unsubscribe<AttachedCollisionObjectMsg>(topic, OnAttachedCollisionObject);

        if (sceneListener != null)
        {
            sceneListener.OnObjectUpdated -= OnObjectUpdated;
            sceneListener.OnRegistryCleared -= OnRegistryCleared;
        }
        OnRegistryCleared(tearingDown: true);
    }

    void Update()
    {
        if (pendingResume.Count == 0) return;

        expired.Clear();
        foreach (var kv in pendingResume)
            if (Time.time >= kv.Value) expired.Add(kv.Key);

        foreach (string id in expired)
        {
            Debug.LogWarning($"[ACO Listener] No re-ADD for '{id}' within {resumeTimeout}s of its detach; resuming its publisher.");
            Resume(id);
        }
    }

    void OnAttachedCollisionObject(AttachedCollisionObjectMsg msg)
    {
        string id = msg.@object?.id;
        if (string.IsNullOrEmpty(id)) return;

        if (msg.@object.operation == CollisionObjectMsg.REMOVE)
            Detach(id);
        else
            Attach(msg);
    }

    // Idempotent: a repeated attach (new link or relative pose) re-places the object and
    // keeps the state saved by the first one.
    void Attach(AttachedCollisionObjectMsg msg)
    {
        if (sceneListener == null) return;
        string id = msg.@object.id;

        if (!sceneListener.TryGetObject(id, out var go))
        {
            // Unity joined mid-carry: build it from the geometry in the attach message.
            go = sceneListener.SpawnFromAttachedObject(msg.@object);
            if (go == null) return;
            Debug.Log($"[ACO Listener] Built '{id}' from its attach message (not seen on the scene topic yet).");
        }

        pendingResume.Remove(id);
        sceneListener.awaitingDetachPose.Remove(id);
        sceneListener.attachedIds.Add(id);

        if (!attached.TryGetValue(id, out var state) || state.go != go)
        {
            state = new AttachState
            {
                go = go,
                originalParent = go.transform.parent,
                originalLocalScale = go.transform.localScale,
            };
            attached[id] = state;
            Lock(state);
        }

        Transform link = ResolveLink(msg.link_name);
        if (link == null)
        {
            // Still locked and silent on /collision_object (MoveIt has it attached either
            // way), but left visible where it is.
            Debug.LogWarning($"[ACO Listener] No link transform for '{msg.link_name}'; '{id}' stays where it is while attached.");
            return;
        }

        Vector3 jumpFrom = go.transform.position;
        AttachedObjectPlacement.Place(go.transform, link, msg.@object.pose);
        state.reparented = true;
        Debug.Log($"[ACO Listener] Attached '{id}' to link '{link.name}' (moved {Vector3.Distance(jumpFrom, go.transform.position) * 1000f:F1} mm onto the link-relative pose).");
    }

    void Detach(string id)
    {
        if (sceneListener == null) return;
        bool wasAttached = sceneListener.attachedIds.Remove(id);

        if (!attached.TryGetValue(id, out var state))
        {
            if (wasAttached) Debug.LogWarning($"[ACO Listener] Detach for '{id}' with no saved attach state.");
            return;
        }
        attached.Remove(id);
        if (state.go == null) return;

        Restore(state, keepSilent: true);

        // The re-ADD that follows on the scene topic carries the exact place pose; the
        // publishers stay silent until it lands. Resumed in OnObjectUpdated.
        sceneListener.awaitingDetachPose.Add(id);
        pendingResume[id] = Time.time + resumeTimeout;
        Debug.Log($"[ACO Listener] Detached '{id}'.");
    }

    void OnObjectUpdated(string id)
    {
        if (pendingResume.ContainsKey(id)) Resume(id);
    }

    void Resume(string id)
    {
        pendingResume.Remove(id);
        if (sceneListener == null) return;
        sceneListener.awaitingDetachPose.Remove(id);
        if (sceneListener.attachedIds.Contains(id)) return; // picked up again meanwhile
        if (sceneListener.TryGetObject(id, out var go))
            SetAttachedToRobot(go, false);
    }

    // Scene change / teardown: leave nothing parented under a robot link.
    // On teardown the original parent may itself be mid-destroy (Unity refuses to reparent
    // under it), and nothing is left to track the object, so it is removed from the link
    // by destroying it; it is rebuilt from the next ADD if a listener comes back.
    void OnRegistryCleared(bool tearingDown)
    {
        // When the whole Unity scene is unloading the objects die with it; reparenting
        // them mid-teardown only raises errors.
        if (gameObject.scene.isLoaded)
            foreach (var state in attached.Values)
            {
                if (state.go == null) continue;
                if (tearingDown) Destroy(state.go); // attachedToRobot keeps its REMOVE off /collision_object
                else Restore(state, keepSilent: false);
            }

        attached.Clear();
        pendingResume.Clear();
        if (ikController != null) ikController.ClearLinkCache();
    }

    Transform ResolveLink(string linkName)
    {
        if (ikController == null || string.IsNullOrEmpty(linkName)) return null;
        if (ikController.TryFindLinkTransform(linkName, out var link)) return link;

        if (!string.IsNullOrEmpty(rosNamePrefix) && linkName.StartsWith(rosNamePrefix)
            && ikController.TryFindLinkTransform(unityNamePrefix + linkName.Substring(rosNamePrefix.Length), out link))
            return link;

        return null;
    }

    void Lock(AttachState state)
    {
        var go = state.go;
        SetAttachedToRobot(go, true);

        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
        {
            state.bodies.Add((rb, rb.isKinematic));
            rb.isKinematic = true;
        }

        // SelectableGrabController re-enables its interactable on selection, so it needs its
        // own lock; interactables without one are simply disabled.
        foreach (var grab in go.GetComponentsInChildren<XRGrabInteractable>(true))
        {
            if (grab.TryGetComponent<SelectableGrabController>(out var controller))
            {
                state.grabControllers.Add(controller);
                controller.SetLocked(true);
            }
            else
            {
                state.grabs.Add((grab, grab.enabled));
                grab.enabled = false;
            }
        }

        // Drop the selection too, so the wrist menu cannot edit or delete it mid-carry.
        var selection = SelectionManager.Instance;
        if (selection != null && selection.SelectedObject != null
            && selection.SelectedObject.transform.IsChildOf(go.transform))
            selection.ClearSelection();
    }

    void Restore(AttachState state, bool keepSilent)
    {
        var go = state.go;
        if (state.reparented)
        {
            Transform parent = state.originalParent
                ? state.originalParent
                : (worldOrigin != null ? worldOrigin.transform : null);
            go.transform.SetParent(parent, worldPositionStays: true);
            go.transform.localScale = state.originalLocalScale;
        }

        foreach (var (rb, wasKinematic) in state.bodies)
            if (rb != null) rb.isKinematic = wasKinematic;
        foreach (var (grab, wasEnabled) in state.grabs)
            if (grab != null) grab.enabled = wasEnabled;
        foreach (var controller in state.grabControllers)
            if (controller != null) controller.SetLocked(false);

        if (!keepSilent) SetAttachedToRobot(go, false);
    }

    static void SetAttachedToRobot(GameObject go, bool value)
    {
        foreach (var pub in go.GetComponentsInChildren<CollisionObjectPublisher>(true))
        {
            pub.attachedToRobot = value;
            // Coming back: the pose ROS just set is already known to it — don't echo a MOVE.
            if (!value) pub.MarkTransformAsPublished();
        }
    }
}

/// <summary>
/// Places an object under a robot link at the link-relative pose of an
/// AttachedCollisionObject. Shared by the live listener and the solution preview.
/// </summary>
public static class AttachedObjectPlacement
{
    // Link frames come from the URDF importer and are FLU, but mirrored objects are laid out
    // in this project's world convention (RosUnityConversion: ROS x,y,z -> Unity x,z,y). The
    // two differ by a fixed quarter turn about Unity Y, which has to be folded into the
    // object's link-local rotation or boxes and meshes ride the gripper turned by 90 degrees.
    private static readonly Quaternion WorldConventionFromFlu = Quaternion.Euler(0f, -90f, 0f);

    public static void Place(Transform obj, Transform link, PoseMsg rosPoseInLink)
    {
        Vector3 worldScale = obj.lossyScale;
        obj.SetParent(link, worldPositionStays: false);

        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;
        if (rosPoseInLink != null)
        {
            position = rosPoseInLink.position.From<FLU>();
            var q = rosPoseInLink.orientation;
            // An all-zero quaternion means "unset"; treat it as identity.
            if (q.x != 0 || q.y != 0 || q.z != 0 || q.w != 0)
                rotation = q.From<FLU>();
        }

        // Cancel any non-unit scale up the link chain so the object keeps its size and its
        // offset stays in metres.
        Vector3 linkScale = link.lossyScale;
        obj.localPosition = Divide(position, linkScale);
        obj.localRotation = rotation * WorldConventionFromFlu;
        obj.localScale = Divide(worldScale, linkScale);
    }

    static Vector3 Divide(Vector3 v, Vector3 by) => new Vector3(
        Mathf.Approximately(by.x, 0f) ? v.x : v.x / by.x,
        Mathf.Approximately(by.y, 0f) ? v.y : v.y / by.y,
        Mathf.Approximately(by.z, 0f) ? v.z : v.z / by.z);
}
