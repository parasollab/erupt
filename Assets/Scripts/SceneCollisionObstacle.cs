using UnityEngine;

/// <summary>
/// Turns a scene-authored primitive (a Cube/Sphere/Cylinder with its collider and renderer) into
/// a MoveIt collision object at runtime, using the same grab/publish stack the wrist menu gives
/// user-created shapes (<see cref="GrabbableCollisionObjectFactory"/>). Meant for test obstacles
/// and fixed furniture in demo scenes (e.g. ReachabilityVR): author the shape in the editor, add
/// this component, point <see cref="worldOrigin"/> at the robot's BaseTransform.
///
/// Runs in Awake so the CollisionObjectPublisher exists before CollisionObjectsListenerSimple
/// registers Unity-owned ids in its Start (otherwise the watcher's echo would spawn a duplicate).
/// </summary>
[DisallowMultipleComponent]
public class SceneCollisionObstacle : MonoBehaviour
{
    [Tooltip("MoveIt collision object id (unique in the planning scene). Empty = scene_<name>.")]
    public string objectId;

    [Tooltip("Object whose frame the pose is published in: the robot's BaseTransform. " +
             "Empty = the first GameObject named 'BaseTransform' in the scene.")]
    public GameObject worldOrigin;

    [Tooltip("Add the XRI grab stack so the shape can be selected and moved (VR or simulator).")]
    public bool grabbable = true;

    [Tooltip("Keep the authored rotation while grabbed (grabbable only).")]
    public bool freezePose = true;

    public CollisionObjectPublisher Publisher { get; private set; }

    void Awake()
    {
        if (string.IsNullOrEmpty(objectId))
            objectId = "scene_" + gameObject.name.Replace(' ', '_').ToLowerInvariant();

        if (worldOrigin == null)
        {
            GameObject anchor = GameObject.Find("BaseTransform");
            if (anchor != null)
                worldOrigin = anchor;
            else
                Debug.LogWarning($"SceneCollisionObstacle '{name}': no worldOrigin; poses will be " +
                                 "published in raw Unity coordinates.", this);
        }

        if (GetComponent<Collider>() == null)
            Debug.LogWarning($"SceneCollisionObstacle '{name}': no collider; grabbing needs one.", this);

        if (grabbable)
        {
            Publisher = GrabbableCollisionObjectFactory.Configure(
                gameObject, objectId, worldOrigin, markAlreadyPublished: false, freezePose: freezePose);
        }
        else
        {
            CollisionObjectPublisher pub = GetComponent<CollisionObjectPublisher>();
            if (pub == null) pub = gameObject.AddComponent<CollisionObjectPublisher>();
            pub.isMesh = false;
            pub.objectId = objectId;
            pub.worldOrigin = worldOrigin;
            Publisher = pub;
        }
    }
}
