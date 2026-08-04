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
    private Material originalMaterial;
    private Renderer selectedRenderer;
    private bool selectActionSubscribed;

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

    private void SubscribeToSelectAction()
    {
        if (selectActionSubscribed || selectAction == null || selectAction.action == null)
            return;

        selectAction.action.performed += OnSelectPerformed;
        selectActionSubscribed = true;
    }

    private void OnSelectPerformed(InputAction.CallbackContext context)
    {
        TrySelect();
    }

    private void OnDestroy()
    {
        UnsubscribeFromSelectAction();

        if (Instance == this)
            Instance = null;
    }

    private void UnsubscribeFromSelectAction()
    {
        if (!selectActionSubscribed || selectAction == null || selectAction.action == null)
            return;

        selectAction.action.performed -= OnSelectPerformed;
        selectActionSubscribed = false;
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
            selectedRenderer.material = highlightMaterial;
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
