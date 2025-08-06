using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class SelectionManager : MonoBehaviour
{
    public static SelectionManager Instance { get; private set; }

    public InputActionReference selectAction;

    [Header("Ray Interactor")]
    public XRRayInteractor rayInteractor; // Assign your controller's ray interactor in the Inspector

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
        if (rayInteractor == null)
        {
            Debug.LogError("Ray Interactor is not assigned in SelectionManager.");
            return;
        }

        selectAction.action.performed += ctx => TrySelect();
    }

    void TrySelect()
    {
        if (rayInteractor == null || !rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            // Clicked on empty space - deselect all
            ClearSelection();
            return;
        }

        GameObject hitObj = hit.collider.gameObject;
        
        if (hitObj.CompareTag("Selectable"))
        {
            // Check if clicking on already selected object - toggle selection
            if (SelectedObject == hitObj)
            {
                ClearSelection(); // Deselect if clicking on selected object
            }
            else
            {
                SetSelectedObject(hitObj); // Select new object
            }
        }
        else
        {
            // Clicked on non-selectable object - deselect all
            ClearSelection();
        }
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
