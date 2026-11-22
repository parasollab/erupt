using System.Collections.Generic;
using Erupt.Interaction;
using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// One robot's direct manipulation: its end-effector handle, a handle per arm joint, link
    /// grabbing, joint selection and jogging. It acts only on what belongs to its own robot and
    /// is driven by <see cref="RobotIntentDispatcher"/>, which sends each intent to the
    /// controller of the robot its target belongs to. It reads no device: the intents come
    /// from the interaction router, whatever input produced them.
    /// </summary>
    public class RobotInteractionController : MonoBehaviour
    {
        [SerializeField] private DirectArticulationIKController ikController;
        [SerializeField] private Transform endEffector;
        [SerializeField] private Transform handle;
        [SerializeField] private Renderer handleRenderer;
        [SerializeField] private Color handleIdleColor = new Color(0.05f, 0.75f, 1f, 1f);
        [SerializeField] private Color handleActiveColor = new Color(1f, 0.75f, 0.05f, 1f);
        [SerializeField] private Color selectedJointColor = new Color(1f, 0.35f, 0.08f, 1f);
        [Header("Joint IK Handles")]
        [SerializeField] private bool createJointHandles = true;
        [SerializeField, Min(0.01f)] private float jointHandleDiameter = 0.075f;
        [SerializeField] private Color jointHandleColor = new Color(0.1f, 0.85f, 1f, 0.8f);
        [SerializeField, Range(1, 2)] private int maximumConcurrentDrags = 2;
        [Header("Link Grabbing")]
        [SerializeField] private bool allowLinkGrabbing = true;

        [Header("Selection")]
        [Tooltip("Robot-link and end-effector selections are published here so tier 2 can offer their verbs. Found in the scene if empty.")]
        [SerializeField] private SelectionService selectionService;

        [Header("Active robot (shown only while the scene has several robots)")]
        [Tooltip("The links of the robot the user is working with lean towards this colour.")]
        [SerializeField] private Color activeRobotColor = new Color(0.25f, 0.8f, 1f, 1f);
        [SerializeField, Range(0f, 1f)] private float activeRobotTintStrength = 0.18f;

        private const string EndEffectorName = "End Effector";

        private ArticulationBody selectedJoint;
        private Renderer[] selectedRenderers;
        private Renderer[] activeTinted;
        private string labelSuffix;
        private readonly Dictionary<Transform, ArticulationBody> jointByHandle =
            new Dictionary<Transform, ArticulationBody>();
        private readonly List<GameObject> jointHandles = new List<GameObject>();
        private sealed class ActiveDrag
        {
            public ArticulationBody body;
            public Vector3 localGrabPoint;
            public bool isJointPivot;
            public float distance;
            public Vector3 offset;
            public Vector3 target;
        }

        private readonly Dictionary<object, ActiveDrag> activeDrags =
            new Dictionary<object, ActiveDrag>();
        private readonly List<ActiveDrag> orderedDrags = new List<ActiveDrag>(2);

        public Transform Handle => handle;

        /// <summary>The joint selected on this robot (highlighted, jogged by the axis); null when none is.</summary>
        public ArticulationBody SelectedJoint => selectedJoint;

        /// <summary>Whether any input source is dragging something of this robot.</summary>
        public bool IsDragging => activeDrags.Count > 0;

        /// <summary>Whether this robot's links carry the active-robot tint.</summary>
        public bool ShowsActive => activeTinted != null;

        private void Awake()
        {
            if (handle != null && handleRenderer == null)
                handleRenderer = handle.GetComponentInChildren<Renderer>();
            EnsureHandleMaterial();
            SetHandleActive(false);
            if (selectionService == null) selectionService = FindFirstObjectByType<SelectionService>();
            if (selectionService != null) selectionService.SelectionChanged += OnServiceSelectionChanged;
            MarkEndEffector();
        }

        // A robot prefab is made under one render pipeline and used under another: the
        // handle's material is then missing (its asset belongs to the other pipeline) and the
        // handle, and the joint handles that share its material, would show pink. They take
        // the material a new primitive gets under the pipeline that is running.
        private static Material pipelineDefaultMaterial;

        private void EnsureHandleMaterial()
        {
            if (handleRenderer == null) return;
            Material material = handleRenderer.sharedMaterial;
            if (material != null && material.shader != null && material.shader.isSupported) return;

            if (pipelineDefaultMaterial == null)
            {
                GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pipelineDefaultMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                if (Application.isPlaying) Destroy(probe); else DestroyImmediate(probe);
            }
            handleRenderer.sharedMaterial = pipelineDefaultMaterial;
        }

        // --- Typed selection (Guidelines Part 2: tier 2 keys off the selected kind) ------

        private readonly Dictionary<ArticulationBody, SelectableMarker> markerByJoint = new();

        private void MarkEndEffector()
        {
            if (handle != null)
                SelectableMarker.Ensure(handle.gameObject, SelectionKind.EndEffector, EndEffectorName).SetDisplayName(Labelled(EndEffectorName));
        }

        private SelectableMarker MarkerFor(ArticulationBody joint)
        {
            if (joint == null) return null;
            if (!markerByJoint.TryGetValue(joint, out var marker) || marker == null)
            {
                marker = SelectableMarker.Ensure(joint.gameObject, SelectionKind.RobotLink, joint.name);
                marker.SetDisplayName(Labelled(joint.name));
                markerByJoint[joint] = marker;
            }
            return marker;
        }

        private string Labelled(string what) => string.IsNullOrEmpty(labelSuffix) ? what : $"{what} · {labelSuffix}";

        /// <summary>
        /// Name this robot in the display names of its selectables (the tier 2 menu header reads
        /// "End Effector · left"); null or empty names them without a robot.
        /// </summary>
        public void SetLabelSuffix(string suffix)
        {
            if (labelSuffix == suffix) return;
            labelSuffix = suffix;
            if (handle != null && handle.TryGetComponent(out SelectableMarker endEffectorMarker))
                endEffectorMarker.SetDisplayName(Labelled(EndEffectorName));
            foreach (KeyValuePair<ArticulationBody, SelectableMarker> pair in markerByJoint)
                if (pair.Key != null && pair.Value != null) pair.Value.SetDisplayName(Labelled(pair.Key.name));
            foreach (KeyValuePair<Transform, ArticulationBody> pair in jointByHandle)
                if (pair.Key != null && pair.Value != null && pair.Key.TryGetComponent(out SelectableMarker handleMarker))
                    handleMarker.SetDisplayName(Labelled(pair.Value.name));
        }

        /// <summary>
        /// Whether an object is part of this robot's interaction: its end-effector handle, one
        /// of its joint handles, or a body of the chain it controls.
        /// </summary>
        public bool Owns(GameObject go)
        {
            if (go == null) return false;
            Transform t = go.transform;
            if (handle != null && (t == handle || t.IsChildOf(handle))) return true;
            if (jointByHandle.ContainsKey(t)) return true;
            if (ikController == null) return false;
            ArticulationBody body = go.GetComponentInParent<ArticulationBody>();
            return body != null && (ikController.CanControlJoint(body) || ikController.CanControlBodyPoint(body));
        }

        // A robot-kind selection is this controller's only when the selected object is this robot's.
        private bool OwnsSelection(ISelectable s) =>
            s is SelectableMarker m && (m.Kind == SelectionKind.RobotLink || m.Kind == SelectionKind.EndEffector) && Owns(m.gameObject);

        // Something that is not this robot's was selected (an obstacle, another robot's link):
        // drop this robot's highlight without touching the service again.
        private void OnServiceSelectionChanged(ISelectable current)
        {
            if (current == null || !OwnsSelection(current))
            {
                if (selectedJoint != null) ClearJointHighlight();
                if (!HasEndEffectorDrag()) SetHandleActive(false);
                return;
            }
            if (current is SelectableMarker m && m.Kind == SelectionKind.RobotLink)
            {
                ArticulationBody joint = jointByHandle.TryGetValue(m.transform, out ArticulationBody handleJoint)
                    ? handleJoint
                    : m.GetComponent<ArticulationBody>();
                if (joint != null && joint != selectedJoint && ikController != null && ikController.CanControlJoint(joint))
                    SelectJoint(joint);
            }
        }

        public void Configure(DirectArticulationIKController controller, Transform toolTransform, Transform handleTransform)
        {
            ikController = controller;
            endEffector = toolTransform;
            handle = handleTransform;
            handleRenderer = handle != null ? handle.GetComponentInChildren<Renderer>() : null;
            SetHandleActive(false);
            MarkEndEffector();
            EnsureJointHandles();
        }

        private void LateUpdate()
        {
            EnsureJointHandles();
            SolveActiveDrags();
            UpdateJointHandleWorldPositions();

            // A joint-handle drag moves the end effector as a consequence, so its original
            // handle should continue following it rather than being left behind in space.
            if (endEffector != null && handle != null && !HasEndEffectorDrag())
            {
                handle.position = endEffector.position;
            }
        }

        // How far in front of the handle's sphere a link's surface may be for a ray through
        // both to count as pointing at the handle.
        private const float HandleInsideLinkMargin = 0.05f;

        /// <summary>
        /// Whether a ray that hit one of this robot's links is pointing at the end-effector
        /// handle: the ray goes through the handle's sphere, and the surface it hit is right
        /// in front of the handle or around it. The handle sits at the tool flange, and on a
        /// robot without a gripper the last link's body covers part of it; pointing at what
        /// shows of the handle must still pick the handle, not the link it is sunk into.
        /// </summary>
        public bool PointsAtHandle(Ray ray, RaycastHit hit)
        {
            if (handle == null || hit.transform == null) return false;
            if (hit.transform == handle || hit.transform.IsChildOf(handle)) return true;
            // A joint's own handle is something else to point at.
            if (jointByHandle.ContainsKey(hit.transform)) return false;
            float radius = Mathf.Abs(handle.lossyScale.x) * 0.5f;
            if (DistanceToHandle(ray) > radius) return false;
            float along = Vector3.Dot(handle.position - ray.origin, ray.direction);
            // The surface hit lies no further in front of the sphere than the margin (a link
            // that merely passes in front of the handle, further away, is what was pointed at).
            return along > 0f && hit.distance >= along - radius - HandleInsideLinkMargin;
        }

        public void SelectFromHit(RaycastHit hit)
        {
            SelectFromHit(hit, false);
        }

        private void SelectFromHit(RaycastHit hit, bool atHandle)
        {
            if (handle != null && (atHandle || hit.transform == handle || hit.transform.IsChildOf(handle)))
            {
                ClearJointHighlight();
                SetHandleActive(true);
                // The end effector is a selection kind of its own (set-goal lives on it).
                selectionService?.Select(SelectableMarker.Ensure(handle.gameObject, SelectionKind.EndEffector, EndEffectorName));
                return;
            }

            if (TryGetJointHandle(hit.transform, out ArticulationBody handleJoint))
            {
                SetHandleActive(false);
                SelectJoint(handleJoint, hit.transform.GetComponent<Renderer>());
                return;
            }

            SetHandleActive(false);
            ArticulationBody hitJoint = hit.transform.GetComponentInParent<ArticulationBody>();
            if (ikController != null && ikController.CanControlJoint(hitJoint))
            {
                SelectJoint(hitJoint);
            }
            else
            {
                ClearSelection();
            }
        }

        /// <summary>
        /// Start a drag by this input source on what its ray hit, when that is this robot's
        /// end-effector handle, one of its joint handles or a link it can move. False when the
        /// hit is none of those, the source already drags, or the robot has no free drag slot.
        /// </summary>
        public bool TryBeginHandleDrag(InteractionIntent intent)
        {
            return TryBeginHandleDrag(
                intent.Sample.SourceId ?? "default", intent.Ray, intent.HasHit ? intent.Hit : default, nearHandle: false);
        }

        /// <summary>
        /// Start a drag of this robot's end-effector handle by a ray that passes close to it
        /// without hitting it. The dispatcher uses this for the handle nearest the ray when the
        /// ray hit nothing that belongs to a robot.
        /// </summary>
        public bool TryBeginHandleDragNear(InteractionIntent intent)
        {
            return TryBeginHandleDrag(intent.Sample.SourceId ?? "default", intent.Ray, default, nearHandle: true);
        }

        /// <summary>How far a ray passes from this robot's end-effector handle; infinity when it has none.</summary>
        public float DistanceToHandle(Ray ray) =>
            handle != null ? Vector3.Cross(ray.direction, handle.position - ray.origin).magnitude : float.PositiveInfinity;

        private bool TryBeginHandleDrag(object interactor, Ray ray, RaycastHit hit, bool nearHandle)
        {
            if (ikController == null || handle == null || interactor == null ||
                activeDrags.ContainsKey(interactor) || activeDrags.Count >= maximumConcurrentDrags)
            {
                return false;
            }

            // A link's body in front of the handle, with the ray through both: the handle.
            bool hitHandle = hit.transform != null && PointsAtHandle(ray, hit);
            bool hitJointHandle = TryGetJointHandle(hit.transform, out ArticulationBody joint);
            ArticulationBody linkBody = null;
            bool hitLink = false;
            if (allowLinkGrabbing && !hitHandle && !hitJointHandle && hit.transform != null)
            {
                linkBody = hit.transform.GetComponentInParent<ArticulationBody>();
                hitLink = ikController.CanControlBodyPoint(linkBody);
            }

            bool hitDragTarget = hitHandle || hitJointHandle || hitLink;
            if (!hitDragTarget && nearHandle) hitHandle = true;

            if (!hitHandle && !hitJointHandle && !hitLink)
            {
                return false;
            }

            ArticulationBody grabbedBody = hitJointHandle ? joint : linkBody;
            if (IsTargetAlreadyGrabbed(grabbedBody))
                return false;

            Transform jointHandle = hitJointHandle ? hit.transform : null;
            Vector3 draggedPoint = hitJointHandle
                ? jointHandle.position
                : hitLink ? hit.point : handle.position;
            float distance = hitDragTarget
                ? hit.distance
                : Mathf.Max(0.1f, Vector3.Dot(draggedPoint - ray.origin, ray.direction));
            var drag = new ActiveDrag
            {
                body = grabbedBody,
                localGrabPoint = hitJointHandle
                    ? joint.anchorPosition
                    : hitLink ? linkBody.transform.InverseTransformPoint(hit.point) : Vector3.zero,
                isJointPivot = hitJointHandle,
                distance = distance,
                offset = draggedPoint - ray.GetPoint(distance),
                target = draggedPoint
            };

            if (activeDrags.Count == 0) ikController.BeginInteraction();
            activeDrags.Add(interactor, drag);
            if (hitHandle)
            {
                // Dragging the end effector selects it, as dragging a joint selects the joint:
                // its menu (set start, set goal, plan) is there when the drag ends.
                ClearJointHighlight();
                SetHandleActive(true);
                selectionService?.Select(SelectableMarker.Ensure(handle.gameObject, SelectionKind.EndEffector, EndEffectorName));
            }
            else if (hitJointHandle) SelectJoint(joint, jointHandle.GetComponent<Renderer>());
            else if (hitLink)
            {
                int jointIndex = ikController.GetBodyPointEffectorIndex(linkBody) - 1;
                Renderer hitRenderer = hit.transform.GetComponent<Renderer>() ??
                    hit.transform.GetComponentInChildren<Renderer>();
                SelectJoint(ikController.Joints[jointIndex], hitRenderer);
            }
            return true;
        }

        /// <summary>Router-driven equivalent of UpdateHandleDrag.</summary>
        public void UpdateHandleDrag(InteractionIntent intent)
        {
            UpdateHandleDrag(intent.Sample.SourceId ?? "default", intent.Ray);
        }

        private void UpdateHandleDrag(object interactor, Ray ray)
        {
            if (interactor == null || ikController == null || handle == null ||
                !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
            {
                return;
            }

            drag.target = ray.GetPoint(drag.distance) + drag.offset;
            if (drag.body == null) handle.position = drag.target;
        }

        /// <summary>Router-driven equivalent of EndHandleDrag.</summary>
        public void EndHandleDrag(InteractionIntent intent)
        {
            EndHandleDrag(intent.Sample.SourceId ?? "default");
        }

        private void EndHandleDrag(object interactor)
        {
            if (interactor == null || !activeDrags.TryGetValue(interactor, out ActiveDrag drag))
            {
                return;
            }

            bool draggedEndEffector = drag.body == null;
            activeDrags.Remove(interactor);
            if (activeDrags.Count == 0) ikController.EndInteraction();

            if (endEffector != null && handle != null && !HasEndEffectorDrag())
                handle.position = endEffector.position;

            // The handle keeps its active colour while the end effector is the selection.
            if (draggedEndEffector) SetHandleActive(HasEndEffectorDrag() || EndEffectorIsSelected());
        }

        private bool EndEffectorIsSelected() =>
            selectionService != null && handle != null && selectionService.Current is SelectableMarker m
            && m.Kind == SelectionKind.EndEffector && m.gameObject == handle.gameObject;

        public void EndHandleDragBySource(string sourceId)
        {
            EndHandleDrag(sourceId ?? "default");
        }

        public void JogSelectedJoint(float deltaRadians)
        {
            // A jog that does nothing must not leave an earlier manipulation's refusal standing.
            LastRefusal = InteractionRefusal.None;
            if (selectedJoint == null || ikController == null || Mathf.Approximately(deltaRadians, 0f))
            {
                return;
            }

            ikController.BeginInteraction();
            LastRefusal = ikController.TryNudgeJoint(selectedJoint, deltaRadians);
            ikController.EndInteraction();
        }

        /// <summary>Router-driven selection. Target resolution already happened upstream.</summary>
        public void SelectFromIntent(InteractionIntent intent)
        {
            if (intent.HasHit) SelectFromHit(intent.Hit, PointsAtHandle(intent.Ray, intent.Hit));
            else ClearSelection();
        }

        /// <summary>
        /// Why the most recent manipulation could not fully proceed, or
        /// <see cref="InteractionRefusal.None"/>. Guidelines Part 3.
        /// </summary>
        public InteractionRefusal LastRefusal { get; private set; } = InteractionRefusal.None;

        private void SelectJoint(ArticulationBody joint, Renderer preferredRenderer = null)
        {
            if (selectedJoint == joint)
            {
                return;
            }

            ClearJointHighlight();
            selectedJoint = joint;
            selectionService?.Select(MarkerFor(joint));
            // The handle that was hit, or the link the joint moves: that link alone, not the
            // links further down the arm, which are its children in the hierarchy.
            selectedRenderers = preferredRenderer != null
                ? new[] { preferredRenderer }
                : JointLimitIndicator.OwnRenderers(selectedJoint.transform);
            foreach (Renderer renderer in selectedRenderers)
                RendererTint.Set(renderer, RendererTint.Layer.Selected, selectedJointColor);
        }

        /// <summary>
        /// Drop this robot's joint highlight and, when the current selection is this robot's,
        /// the selection itself. Another robot's selection is left alone.
        /// </summary>
        public void ClearSelection()
        {
            ClearJointHighlight();
            if (!HasEndEffectorDrag()) SetHandleActive(false);
            if (selectionService != null && OwnsSelection(selectionService.Current))
                selectionService.ClearSelection();
        }

        private void ClearJointHighlight()
        {
            if (selectedRenderers != null)
                foreach (Renderer renderer in selectedRenderers)
                    RendererTint.Clear(renderer, RendererTint.Layer.Selected);

            selectedJoint = null;
            selectedRenderers = null;
        }

        private void SetHandleActive(bool active)
        {
            if (handleRenderer != null)
                RendererTint.Set(handleRenderer, RendererTint.Layer.Base, active ? handleActiveColor : handleIdleColor);
        }

        /// <summary>
        /// Mark this robot as the one the user is working with: its links lean towards the
        /// active colour. The dispatcher turns it on for the active robot while the scene has
        /// several robots, and off otherwise.
        /// </summary>
        public void SetActiveHighlight(bool on)
        {
            if (on == (activeTinted != null)) return;
            if (!on)
            {
                foreach (Renderer renderer in activeTinted)
                    RendererTint.Clear(renderer, RendererTint.Layer.ActiveRobot);
                activeTinted = null;
                return;
            }

            Transform root = ikController != null && ikController.Root != null ? ikController.Root : transform;
            var tinted = new List<Renderer>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                // The handle has its own colours; only the robot's body is tinted.
                if (handle != null && renderer.transform.IsChildOf(handle)) continue;
                if (renderer.GetComponentInParent<ArticulationBody>() == null) continue;
                Color own = RendererTint.SharedColor(renderer);
                Color tint = Color.Lerp(own, activeRobotColor, activeRobotTintStrength);
                tint.a = own.a;
                RendererTint.Set(renderer, RendererTint.Layer.ActiveRobot, tint);
                tinted.Add(renderer);
            }
            activeTinted = tinted.ToArray();
        }

        private void SolveActiveDrags()
        {
            if (ikController == null || activeDrags.Count == 0)
                return;

            orderedDrags.Clear();
            foreach (ActiveDrag drag in activeDrags.Values)
                orderedDrags.Add(drag);

            orderedDrags.Sort((a, b) => GetEffectorIndex(a).CompareTo(GetEffectorIndex(b)));

            int firstJointIndex = 0;
            LastRefusal = InteractionRefusal.None;
            foreach (ActiveDrag drag in orderedDrags)
            {
                InteractionRefusal refusal;
                if (drag.body != null)
                {
                    int effectorIndex = GetEffectorIndex(drag);
                    refusal = drag.isJointPivot
                        ? ikController.TrySolveJointToTarget(
                            drag.body, drag.target, firstJointIndex)
                        : ikController.TrySolveBodyPointToTarget(
                            drag.body, drag.localGrabPoint, drag.target, firstJointIndex);
                    if (effectorIndex >= 0) firstJointIndex = effectorIndex;
                }
                else
                {
                    refusal = ikController.TrySolveToTarget(drag.target, firstJointIndex);
                    firstJointIndex = ikController.Joints.Count;
                    if (handle != null) handle.position = drag.target;
                }

                if (!LastRefusal.IsRefused && refusal.IsRefused)
                    LastRefusal = refusal;
            }
        }

        private int GetEffectorIndex(ActiveDrag drag)
        {
            if (drag.body == null)
                return ikController.Joints.Count;

            return drag.isJointPivot
                ? ikController.GetJointIndex(drag.body)
                : ikController.GetBodyPointEffectorIndex(drag.body);
        }

        private bool IsTargetAlreadyGrabbed(ArticulationBody body)
        {
            foreach (ActiveDrag drag in activeDrags.Values)
            {
                if (drag.body == body)
                    return true;
            }

            return false;
        }

        private bool HasEndEffectorDrag()
        {
            foreach (ActiveDrag drag in activeDrags.Values)
            {
                if (drag.body == null)
                    return true;
            }

            return false;
        }

        private bool TryGetJointHandle(Transform candidate, out ArticulationBody joint)
        {
            joint = null;
            return candidate != null && jointByHandle.TryGetValue(candidate, out joint);
        }

        private void EnsureJointHandles()
        {
            if (!createJointHandles || jointByHandle.Count > 0 || ikController == null ||
                ikController.Joints.Count == 0)
                return;

            foreach (ArticulationBody joint in ikController.Joints)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Joint IK Handle ({joint.name})";
                sphere.transform.SetParent(null, false);
                sphere.transform.SetPositionAndRotation(GetJointPivot(joint), Quaternion.identity);
                sphere.transform.localScale = Vector3.one * jointHandleDiameter;
                if (handle != null) sphere.layer = handle.gameObject.layer;

                SphereCollider sphereCollider = sphere.GetComponent<SphereCollider>();
                sphereCollider.isTrigger = true;

                // The end-effector handle's material, shared; the colour is a property block.
                Renderer sphereRenderer = sphere.GetComponent<Renderer>();
                if (handleRenderer != null && handleRenderer.sharedMaterial != null)
                    sphereRenderer.sharedMaterial = handleRenderer.sharedMaterial;
                RendererTint.Set(sphereRenderer, RendererTint.Layer.Base, jointHandleColor);
                SelectableMarker.Ensure(sphere, SelectionKind.RobotLink, joint.name).SetDisplayName(Labelled(joint.name));
                // The handle is not parented under the robot; the marker says whose it is.
                RobotOwned.Mark(sphere, GetComponentInParent<RobotInstance>(true));
                MarkerFor(joint);
                jointByHandle.Add(sphere.transform, joint);
                jointHandles.Add(sphere);
            }
        }

        private void UpdateJointHandleWorldPositions()
        {
            foreach (KeyValuePair<Transform, ArticulationBody> pair in jointByHandle)
            {
                if (pair.Key != null && pair.Value != null)
                    pair.Key.position = GetJointPivot(pair.Value);
            }
        }

        private void OnDestroy()
        {
            if (selectionService != null) selectionService.SelectionChanged -= OnServiceSelectionChanged;
            ClearJointHighlight();
            SetActiveHighlight(false);
            foreach (GameObject sphere in jointHandles)
            {
                if (sphere != null) Destroy(sphere);
            }
        }

        private static Vector3 GetJointPivot(ArticulationBody joint)
        {
            return joint.transform.TransformPoint(joint.anchorPosition);
        }

    }
}
