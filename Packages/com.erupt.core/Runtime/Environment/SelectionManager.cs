using Erupt.Interaction;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.EventSystems;
using System.Collections.Generic;

public class SelectionManager : MonoBehaviour
{
    public static SelectionManager Instance { get; private set; }

    public InputActionReference selectAction;

    [Header("Ray Interactor")]
    public XRRayInteractor rayInteractor; // Assign your controller's ray interactor in the Inspector

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
    private Material originalMaterial;
    private Renderer selectedRenderer;

    public GameObject SelectedObject { get; private set; }
    
    // Events for selection changes
    public System.Action<GameObject> OnObjectSelected;
    public System.Action OnSelectionCleared;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
        }
        Instance = this;
    }

    private void Start()
    {
        if (interactionRouter != null)
        {
            interactionRouter.Select += OnRouterSelect;
            return;
        }

        if (rayInteractor == null)
        {
            Debug.LogError("Ray Interactor is not assigned in SelectionManager.");
            return;
        }

        selectAction.action.performed += OnSelectPerformed;
    }

    private void OnDestroy()
    {
        if (interactionRouter != null)
            interactionRouter.Select -= OnRouterSelect;
        else if (selectAction != null && selectAction.action != null)
            selectAction.action.performed -= OnSelectPerformed;
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

        if (rayInteractor == null || !rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            ClearSelection();
            return;
        }

        GameObject hitObj = hit.collider.gameObject;

        if (IsHittingWristUI(hitObj))
            return;

        if (hitObj.CompareTag("Selectable"))
        {
            if (SelectedObject == hitObj)
                return; // Already selected — keep it so the user can grab it
            SetSelectedObject(hitObj);
        }
        else
        {
            ClearSelection();
        }
    }

    bool IsInteractingWithUI()
    {
        if (rayInteractor == null) return false;
        if (rayInteractor.TryGetCurrentUIRaycastResult(out RaycastResult uiHit))
            return IsPartOfWristUIHierarchy(uiHit.gameObject);
        return false;
    }

    // The legacy wrist menu lives in the reference app, which core cannot reference;
    // until Phase 3 retires it, recognise it by component name so behaviour is unchanged.
    static bool IsWristMenuRoot(Transform t) => t.GetComponent("WristMenuController") != null;

    bool IsPartOfWristUIHierarchy(GameObject obj)
    {
        Transform current = obj.transform;
        while (current != null)
        {
            if (IsWristMenuRoot(current))
            {
                return true;
            }
            current = current.parent;
        }
        return false;
    }

    bool IsHittingWristUI(GameObject hitObject)
    {
        Transform current = hitObject.transform;
        while (current != null)
        {
            if (IsWristMenuRoot(current))
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
                    if (IsWristMenuRoot(canvasParent))
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
        selectedRenderer = SelectedObject.GetComponent<Renderer>();
        if (selectedRenderer != null)
        {
            originalMaterial = selectedRenderer.material;
            selectedRenderer.material = highlightMaterial;
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
