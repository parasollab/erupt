using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// One node of the scene graph the FERL bridge learns over: a typed, attributed, movable
// object. Its pose (position + yaw about the ROS z axis) is read from the transform relative
// to the BaseTransform anchor and its footprint from the renderer bounds in the object's own
// frame, so turning an object while dragging it changes its pose, never its shape (the
// bridge recompiles its world only when shapes change). Anything grabbable can be a node.
public class SceneGraphObject : MonoBehaviour
{
    // Registration order is the raw-state slot order the bridge builds, so it is kept stable.
    public static readonly List<SceneGraphObject> All = new List<SceneGraphObject>();
    private static int creationCounter;

    [Tooltip("Unique id and MuJoCo body name on the ROS side: letters, digits and underscores only.")]
    [SerializeField] private string objectId = "";
    public FerlObjectType type = FerlObjectType.Other;

    [Header("Attributes (preference_rl ATTRIBUTE_NAMES order)")]
    public bool open;
    public bool damageSensitive;
    public bool fragile;
    public bool hot;

    [Tooltip("Editable objects are the ones FERL's environmental edits may displace or toggle.")]
    public bool editable = true;

    public event Action<SceneGraphObject> Changed;

    private XRGrabInteractable grabInteractable;
    private bool moving;
    private Vector3 moveStartRos;
    private double moveStartYaw;
    private int creationIndex;

    public string ObjectId
    {
        get => objectId;
        set => objectId = value;
    }

    public bool IsMoving => moving;
    // The drag since the grab began, or since the last SampleMove, in the ROS base frame.
    public Vector3 PendingDisplacement => moving ? CurrentRosPosition() - moveStartRos : Vector3.zero;
    public double PendingTurn => moving ? WrapAngle(CurrentRosYaw() - moveStartYaw) : 0.0;
    public bool IsCarried { get; private set; }

    private Transform carryRestoreParent;
    private ObjectNodeData carriedFootprint;
    private readonly List<(Material material, float alpha)> carriedAlphas = new List<(Material, float)>();
    private readonly List<Collider> carriedColliders = new List<Collider>();

    private void OnEnable()
    {
        creationIndex = creationCounter++;
        All.Add(this);
        All.Sort((a, b) => a.creationIndex.CompareTo(b.creationIndex));
        SceneGraphPublisher.Instance?.MarkDirty();
    }

    private void OnDisable()
    {
        All.Remove(this);
        SceneGraphPublisher.Instance?.MarkDirty();
    }

    private void Start()
    {
        if (string.IsNullOrEmpty(objectId))
            objectId = SanitizeId(gameObject.name);

        grabInteractable = GetComponent<XRGrabInteractable>();
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.AddListener(OnGrabEntered);
            grabInteractable.selectExited.AddListener(OnGrabExited);
        }
    }

    private void OnDestroy()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnGrabEntered);
            grabInteractable.selectExited.RemoveListener(OnGrabExited);
        }
    }

    private void OnGrabEntered(SelectEnterEventArgs args) => BeginMove();

    private void OnGrabExited(SelectExitEventArgs args)
    {
        // With multi-select the second controller may still be holding on.
        if (grabInteractable == null || !grabInteractable.isSelected)
            EndMove();
    }

    // The two hooks every input path (XRI grab, mouse drag) goes through, so a move is
    // reported as one displacement edit however it was made.
    public void BeginMove()
    {
        if (moving)
            return;
        moving = true;
        moveStartRos = CurrentRosPosition();
        moveStartYaw = CurrentRosYaw();
    }

    public void EndMove()
    {
        if (!moving)
            return;
        moving = false;
        Vector3 delta = CurrentRosPosition() - moveStartRos;
        double turn = WrapAngle(CurrentRosYaw() - moveStartYaw);
        SceneGraphPublisher.Instance?.NotifyMoved(this, delta, turn);
        Changed?.Invoke(this);
    }

    // Report the drag so far as an edit without letting go, and measure the next leg from
    // here, so one long grab can yield several waypoints (the env-trace recorder calls this).
    // EndMove then reports only the leg since the last sample. Returns false when the drag so
    // far was too small for the publisher to count as an edit.
    public bool SampleMove()
    {
        if (!moving || SceneGraphPublisher.Instance == null)
            return false;
        Vector3 here = CurrentRosPosition();
        double yaw = CurrentRosYaw();
        if (!SceneGraphPublisher.Instance.NotifyMoved(this, here - moveStartRos, WrapAngle(yaw - moveStartYaw)))
            return false;
        moveStartRos = here;
        moveStartYaw = yaw;
        Changed?.Invoke(this);
        return true;
    }

    public bool GetAttribute(int index)
    {
        switch (index)
        {
            case 0: return open;
            case 1: return damageSensitive;
            case 2: return fragile;
            case 3: return hot;
            default: return false;
        }
    }

    public void SetAttribute(int index, bool value)
    {
        if (GetAttribute(index) == value)
            return;
        switch (index)
        {
            case 0: open = value; break;
            case 1: damageSensitive = value; break;
            case 2: fragile = value; break;
            case 3: hot = value; break;
            default: return;
        }
        SceneGraphPublisher.Instance?.NotifyEdit(new SceneEditData
        {
            objectId = objectId,
            attribute = FerlObjectTypes.AttributeNames[index],
        });
        Changed?.Invoke(this);
    }

    public void SetEditable(bool value)
    {
        if (editable == value)
            return;
        editable = value;
        SceneGraphPublisher.Instance?.MarkDirty();
        Changed?.Invoke(this);
    }

    public void SetType(FerlObjectType value)
    {
        if (type == value)
            return;
        type = value;
        SceneGraphPublisher.Instance?.MarkDirty();
        Changed?.Invoke(this);
    }

    // Snap the object into the robot's hand: it follows the anchor (the TCP), is drawn
    // semi-transparent and stops colliding so it never blocks the end-effector handle or
    // selection rays. Release restores parent, colliders and opacity, keeping the world pose.
    public void SetCarried(bool carried, Transform anchor, float carriedAlpha = 0.35f)
    {
        if (carried == IsCarried)
            return;
        if (carried)
        {
            if (anchor == null)
            {
                Debug.LogWarning($"[SceneGraphObject] No carry anchor for '{objectId}'.");
                return;
            }
            if (moving)
                EndMove();
            Transform baseTransform = SceneGraphPublisher.Instance != null ? SceneGraphPublisher.Instance.BaseTransform : null;
            carriedFootprint = ComputeNode(baseTransform);
            carryRestoreParent = transform.parent;
            transform.SetParent(anchor, true);
            transform.position = anchor.position;
            carriedAlphas.Clear();
            foreach (MeshRenderer renderer in GetComponentsInChildren<MeshRenderer>())
            {
                Material material = renderer.material;
                carriedAlphas.Add((material, material.color.a));
                SetAlpha(material, carriedAlpha);
            }
            carriedColliders.Clear();
            foreach (Collider collider in GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                carriedColliders.Add(collider);
            }
            IsCarried = true;
        }
        else
        {
            transform.SetParent(carryRestoreParent, true);
            foreach ((Material material, float alpha) in carriedAlphas)
                if (material != null)
                    SetAlpha(material, alpha);
            carriedAlphas.Clear();
            foreach (Collider collider in carriedColliders)
                if (collider != null)
                    collider.enabled = true;
            carriedColliders.Clear();
            carriedFootprint = null;
            IsCarried = false;
        }
        Changed?.Invoke(this);
    }

    private static void SetAlpha(Material material, float alpha)
    {
        Color c = material.color;
        c.a = alpha;
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", c);
        material.color = c;
    }

    public double[] AttributeVector()
    {
        return new double[] { open ? 1 : 0, damageSensitive ? 1 : 0, fragile ? 1 : 0, hot ? 1 : 0 };
    }

    // Unity base-frame local coordinates -> ROS base frame: the -90 deg yaw BaseTransform plus
    // the (x, z, y) swap is exactly what CollisionObjectPublisher + RosUnityConversion do.
    public static Vector3 LocalToRos(Vector3 local) => new Vector3(local.x, local.z, local.y);

    public Vector3 CurrentRosPosition()
    {
        Transform baseTransform = SceneGraphPublisher.Instance != null ? SceneGraphPublisher.Instance.BaseTransform : null;
        Vector3 local = baseTransform != null ? baseTransform.InverseTransformPoint(transform.position) : transform.position;
        return LocalToRos(local);
    }

    // The object's heading in the ROS base frame: the angle of its local x axis about ROS z.
    // Derived from the axis direction rather than Euler angles so the left-handed Unity ->
    // right-handed ROS swap in LocalToRos is applied once, to a vector, and the sign is right.
    public double CurrentRosYaw()
    {
        Transform baseTransform = SceneGraphPublisher.Instance != null ? SceneGraphPublisher.Instance.BaseTransform : null;
        return CurrentRosYaw(baseTransform);
    }

    private double CurrentRosYaw(Transform baseTransform)
    {
        Vector3 axis = baseTransform != null ? baseTransform.InverseTransformDirection(transform.right) : transform.right;
        Vector3 ros = LocalToRos(axis);
        if (ros.x * ros.x + ros.y * ros.y < 1e-8f)
            return 0.0; // Standing on end: no heading to speak of.
        return Math.Atan2(ros.y, ros.x);
    }

    public static double WrapAngle(double radians)
    {
        return radians - 2.0 * Math.PI * Math.Floor((radians + Math.PI) / (2.0 * Math.PI));
    }

    public ObjectNodeData ComputeNode(Transform baseTransform)
    {
        Vector3 pos = LocalToRos(baseTransform != null ? baseTransform.InverseTransformPoint(transform.position) : transform.position);
        if (IsCarried && carriedFootprint != null)
        {
            // In the hand the object follows the TCP; keep the footprint and heading it had
            // when it was picked up so the bridge sees a carried object, not a reshaped one.
            var carried = new ObjectNodeData
            {
                id = objectId,
                type = FerlObjectTypes.ToName(type),
                editable = editable,
                attributes = AttributeVector(),
                yaw = carriedFootprint.yaw,
                zLow = carriedFootprint.zLow,
                zHigh = carriedFootprint.zHigh,
            };
            carried.position[0] = pos.x;
            carried.position[1] = pos.y;
            carried.position[2] = pos.z;
            foreach (double[] vertex in carriedFootprint.vertices)
                carried.vertices.Add(new[] { vertex[0], vertex[1] });
            return carried;
        }
        // The footprint is the bounding box of the renderers in the object's own (unscaled)
        // frame, as offsets from the pivot: a world-axis-aligned box would grow whenever a
        // grab turned or tilted the object, and the bridge would take that for a new shape.
        // The bridge places this box at the position and turns it by the yaw; any pitch or
        // roll from the hand is dropped, the object is modelled as standing level.
        Quaternion unturn = Quaternion.Inverse(transform.rotation);
        Vector3 pivot = transform.position;
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        bool any = false;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled)
                continue;
            Bounds bounds = renderer.localBounds;
            Transform rendererTransform = renderer.transform;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 offset = unturn * (rendererTransform.TransformPoint(local) - pivot);
                Vector3 ros = LocalToRos(offset);
                min = Vector3.Min(min, ros);
                max = Vector3.Max(max, ros);
                any = true;
            }
        }
        if (!any)
        {
            min = -Vector3.one * 0.01f;
            max = Vector3.one * 0.01f;
        }

        const float minimumHalf = 0.001f;
        double lowX = Math.Min(min.x, -minimumHalf);
        double highX = Math.Max(max.x, minimumHalf);
        double lowY = Math.Min(min.y, -minimumHalf);
        double highY = Math.Max(max.y, minimumHalf);

        var node = new ObjectNodeData
        {
            id = objectId,
            type = FerlObjectTypes.ToName(type),
            editable = editable,
            attributes = AttributeVector(),
            yaw = CurrentRosYaw(baseTransform),
            zLow = Math.Min(min.z, 0.0),
            zHigh = Math.Max(max.z, minimumHalf),
        };
        node.position[0] = pos.x;
        node.position[1] = pos.y;
        node.position[2] = pos.z;
        node.vertices.Add(new[] { lowX, lowY });
        node.vertices.Add(new[] { highX, lowY });
        node.vertices.Add(new[] { highX, highY });
        node.vertices.Add(new[] { lowX, highY });
        return node;
    }

    public static string SanitizeId(string name)
    {
        var chars = new System.Text.StringBuilder();
        foreach (char c in name ?? "")
            chars.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string cleaned = chars.Length > 0 ? chars.ToString() : "object";
        return char.IsDigit(cleaned[0]) ? "o_" + cleaned : cleaned;
    }
}
