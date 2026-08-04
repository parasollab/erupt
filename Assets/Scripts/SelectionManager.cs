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

    [Header("Highlighting")]
    public Material highlightMaterial;
    // Alpha applied to the selection highlight (1 = opaque, 0 = invisible)
    private float highlightAlpha = 0.75f;
    private Material transparentHighlightMaterial;
    private Material originalMaterial;
    private Renderer selectedRenderer;
    private MoveItPlanningRequestMenuUI planningMenu;

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
        if (rayInteractor == null)
        {
            Debug.LogError("Ray Interactor is not assigned in SelectionManager.");
            return;
        }

        selectAction.action.performed += ctx => TrySelect();
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
        selectedRenderer = SelectedObject.GetComponent<Renderer>();
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
