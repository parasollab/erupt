using Erupt.Interaction;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections.Generic;

public class SelectionManager : MonoBehaviour
{
    public static SelectionManager Instance { get; private set; }

    public InputActionReference selectAction;

    [Header("Ray Interactor")]
    public Quest3ControllerRayInteractor rayInteractor;

    [Header("Interaction Router (opt-in)")]
    [Tooltip("When assigned, selection is driven by the router instead of a bound input " +
             "action, and target resolution happens there. Leave empty to keep the " +
             "pre-refactor behavior.")]
    [SerializeField] private InteractionRouter interactionRouter;

    [Tooltip("Only accept select intents from this source id (\"left\", \"right\"). Empty " +
             "means any source. Set to \"right\" on migration because the pre-refactor " +
             "binding was right-trigger only; Guidelines Part 3's busy-hand rule argues " +
             "for allowing both, which is a Phase 2 change.")]
    [SerializeField] private string selectionSourceId = "";

    [Tooltip("When assigned, selection is mirrored into the shared SelectionService so " +
             "tier 2 menus can key off the selected object's kind. Leave empty to keep " +
             "the pre-refactor behavior.")]
    [SerializeField] private SelectionService selectionService;

    [Header("Highlighting")]
    public Material highlightMaterial;
    // Alpha applied to the selection highlight (1 = opaque, 0 = invisible)
    private float highlightAlpha = 0.75f;
    private Material transparentHighlightMaterial;
    private Material originalMaterial;
    private Renderer selectedRenderer;
    private bool selectActionSubscribed;
    private MoveItPlanningRequestMenuUI planningMenu;

    public GameObject SelectedObject { get; private set; }
    
    // Events for selection changes
    public System.Action<GameObject> OnObjectSelected;
    public System.Action OnSelectionCleared;

    private void Awake()
    {
        // Additive transitions briefly keep both content scenes alive. The newly loaded scene
        // must take ownership immediately; destroying it here would leave no manager after the
        // outgoing scene is retired.
        Instance = this;
    }

    private void Start()
    {
        if (RouterDrivesSelection)
        {
            interactionRouter.Select += OnRouterSelect;
            return;
        }

        if (interactionRouter != null)
        {
            Debug.LogWarning($"SelectionManager: assigned InteractionRouter '{interactionRouter.name}' is " +
                             "inactive, so selection falls back to the bound select action. This happens when " +
                             "the router sits inside a scene's XR rig that is disabled or replaced by the " +
                             "persistent rig at load time.");
        }

        if (rayInteractor == null)
        {
            Debug.LogError("Ray Interactor is not assigned in SelectionManager.");
        }

        SubscribeToSelectAction();
    }

    private void OnEnable()
    {
        SubscribeToSelectAction();
    }

    private void OnDisable()
    {
        UnsubscribeFromSelectAction();
    }

    public void BindRayInteractor(Quest3ControllerRayInteractor interactor)
    {
        rayInteractor = interactor;
    }

    // The router only drives selection when it can actually run. A router assigned in the
    // Inspector but sitting under an inactive rig (KitchenFR3 keeps its XR Origin disabled,
    // and PersistentXRInfrastructure disables duplicate rigs on load) never raises Select,
    // and binding to it alone would leave objects unselectable and therefore ungrabbable.
    private bool RouterDrivesSelection => interactionRouter != null && interactionRouter.isActiveAndEnabled;

    private void SubscribeToSelectAction()
    {
        // Router mode resolves targets itself, so the raw action must stay unbound there or
        // a single trigger pull would drive selection twice.
        if (RouterDrivesSelection)
            return;

        if (selectActionSubscribed || selectAction == null || selectAction.action == null)
            return;

        selectAction.action.performed += OnSelectPerformed;
        selectActionSubscribed = true;
    }

    private void UnsubscribeFromSelectAction()
    {
        if (!selectActionSubscribed || selectAction == null || selectAction.action == null)
            return;

        selectAction.action.performed -= OnSelectPerformed;
        selectActionSubscribed = false;
    }

    private void OnDestroy()
    {
        if (interactionRouter != null)
            interactionRouter.Select -= OnRouterSelect;
        UnsubscribeFromSelectAction();

        // Guarded so the outgoing scene's manager cannot clear the incoming one during an
        // additive transition, where both are alive for a frame.
        if (Instance == this)
            Instance = null;
    }

    private void OnSelectPerformed(InputAction.CallbackContext _) => TrySelect();

    // Bridges the legacy tag-based selection onto the typed SelectionService. Objects
    // without a SelectableMarker are reported as Obstacle, which is what everything
    // tagged "Selectable" was before kinds existed.
    private void MirrorToService(GameObject selected)
    {
        if (selectionService == null) return;

        if (selected == null)
        {
            selectionService.ClearSelection();
            return;
        }

        ISelectable selectable = SelectionService.Resolve(selected);
        if (selectable == null)
        {
            var marker = selected.AddComponent<SelectableMarker>();
            marker.SetKind(SelectionKind.Obstacle);
            selectable = marker;
        }

        selectionService.Select(selectable);
    }

    // Target resolution and UI rejection already happened in the router, so this is the
    // selection rule only — no raycasting and no name matching.
    private void OnRouterSelect(InteractionIntent intent)
    {
        if (!string.IsNullOrEmpty(selectionSourceId) && intent.Sample.SourceId != selectionSourceId)
            return;

        GameObject hitObj = intent.Target;

        if (hitObj == null || !hitObj.CompareTag("Selectable"))
        {
            ClearSelection();
            return;
        }

        if (SelectedObject == hitObj)
            return; // Already selected — keep it so the user can grab it

        SetSelectedObject(hitObj);
    }

    void TrySelect()
    {
        if (IsInteractingWithUI())
            return;

        if (rayInteractor == null || !rayInteractor.TryGetCurrentHit(out RaycastHit hit))
        {
            // Ray hitting nothing — could be UI interaction with misaligned physics ray; don't clear
            return;
        }

        GameObject hitObj = hit.collider.gameObject;

        // Clicking the planning request menu, a robot link, or the end-effector handle
        // drops the current selection outright. This must run before IsHittingWristUI:
        // its name heuristic ("menu"/"ui"/"wrist") also matches those hierarchies
        // (MoveItPlanningRequestMenu, XRI UIDocument, wrist_*_link) and would keep the
        // selection alive instead.
        if (IsDeselectSurface(hitObj))
        {
            ClearSelection();
            return;
        }

        if (IsHittingWristUI(hitObj))
            return;

        GameObject selectableObject = FindSelectableObject(hitObj.transform);
        if (selectableObject != null)
        {
            if (SelectedObject == selectableObject)
                return; // Already selected — keep it so the user can grab it
            SetSelectedObject(selectableObject);
        }
        else
        {
            ClearSelection();
        }
    }

    private static GameObject FindSelectableObject(Transform hitTransform)
    {
        Transform current = hitTransform;
        while (current != null)
        {
            if (current.CompareTag("Selectable"))
                return current.gameObject;
            current = current.parent;
        }

        return null;
    }

    bool IsInteractingWithUI()
    {
        return false;
    }

    bool IsDeselectSurface(GameObject hitObject)
    {
        // Any robot link: URDF-spawned links all carry ArticulationBody in their parent chain.
        if (hitObject.GetComponentInParent<ArticulationBody>() != null)
            return true;

        // The end-effector handle (and anything else under the Robot IK Manager).
        if (hitObject.GetComponentInParent<Quest3RobotInteractionController>() != null)
            return true;

        // The planning request menu: the UI script sits on the prefab's "XRI UIDocument"
        // child, so its parent is the prefab root -- covering both the panel collider and
        // the root grab bar.
        if (planningMenu == null)
            planningMenu = FindFirstObjectByType<MoveItPlanningRequestMenuUI>(FindObjectsInactive.Include);
        if (planningMenu != null)
        {
            Transform menuRoot = planningMenu.transform.parent != null ? planningMenu.transform.parent : planningMenu.transform;
            if (hitObject.transform.IsChildOf(menuRoot))
                return true;
        }

        return false;
    }

    bool IsHittingWristUI(GameObject hitObject)
    {
        Transform current = hitObject.transform;
        while (current != null)
        {
            if (current.GetComponent<WristMenuController>() != null)
                return true;

            if (current.name.ToLower().Contains("wrist") ||
                current.name.ToLower().Contains("ui") ||
                current.name.ToLower().Contains("menu") ||
                current.name.ToLower().Contains("panel") ||
                current.name.ToLower().Contains("button"))
                return true;

            Canvas canvas = current.GetComponent<Canvas>();
            if (canvas != null)
            {
                Transform canvasParent = canvas.transform.parent;
                while (canvasParent != null)
                {
                    if (canvasParent.GetComponent<WristMenuController>() != null)
                        return true;
                    canvasParent = canvasParent.parent;
                }
            }

            current = current.parent;
        }
        return false;
    }

    public void SetSelectedObject(GameObject newSelection)
    {
        if (SelectedObject == newSelection)
            return;

        if (selectedRenderer != null)
        {
            selectedRenderer.material = originalMaterial;
        }

        SelectedObject = newSelection;
        selectedRenderer = SelectedObject.GetComponentInChildren<Renderer>();
        if (selectedRenderer != null)
        {
            originalMaterial = selectedRenderer.material;
            // One shared transparent copy of the highlight material, built lazily so the
            // Teal asset itself stays opaque for its other users and repeated selections
            // don't each instantiate a new material.
            if (transparentHighlightMaterial == null && highlightMaterial != null)
            {
                transparentHighlightMaterial = new Material(highlightMaterial);
                WristMenuController.MakeMaterialTransparent(transparentHighlightMaterial, highlightAlpha);
            }
            selectedRenderer.material = transparentHighlightMaterial != null ? transparentHighlightMaterial : highlightMaterial;
        }
        
        MirrorToService(SelectedObject);

        // Notify listeners that an object was selected
        OnObjectSelected?.Invoke(SelectedObject);
    }

    public void ClearSelection()
    {
        if (selectedRenderer != null && selectedRenderer.gameObject != null)
        {
            selectedRenderer.material = originalMaterial;
        }

        selectedRenderer = null;
        SelectedObject = null;

        MirrorToService(null);

        // Notify listeners that selection was cleared
        OnSelectionCleared?.Invoke();
    }

    public void DeleteSelectedObject()
    {
        if (SelectedObject != null)
        {
            // Clear highlight *before* destroying
            if (selectedRenderer != null)
            {
                selectedRenderer.material = originalMaterial;
            }

            GameObject toDestroy = SelectedObject;

            selectedRenderer = null;
            SelectedObject = null;

            Destroy(toDestroy);
        }
    }
}
