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
        // First check if we're interacting with UI elements
        if (IsInteractingWithUI())
        {
            Debug.Log("SelectionManager: Detected UI interaction - preventing deselection");
            return;
        }

        if (rayInteractor == null || !rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            // Clicked on empty space - deselect all
            Debug.Log("SelectionManager: No 3D raycast hit - clearing selection");
            ClearSelection();
            return;
        }

        GameObject hitObj = hit.collider.gameObject;
        Debug.Log($"SelectionManager: 3D raycast hit object '{hitObj.name}'");
        
        // Check if hitting WristUI menu - don't deselect in this case
        if (IsHittingWristUI(hitObj))
        {
            // Don't change selection when interacting with WristUI
            return;
        }
        
        if (hitObj.CompareTag("Selectable"))
        {
            // Check if clicking on already selected object - toggle selection
            if (SelectedObject == hitObj)
            {
                Debug.Log($"SelectionManager: Toggling selection off for '{hitObj.name}'");
                ClearSelection(); // Deselect if clicking on selected object
            }
            else
            {
                Debug.Log($"SelectionManager: Selecting object '{hitObj.name}'");
                SetSelectedObject(hitObj); // Select new object
            }
        }
        else
        {
            // Clicked on non-selectable object - deselect all
            Debug.Log($"SelectionManager: Hit non-selectable object '{hitObj.name}' - clearing selection");
            ClearSelection();
        }
    }

    bool IsInteractingWithUI()
    {
        // Check if the ray interactor is currently hitting UI elements
        if (rayInteractor == null) return false;

        // Check if we have a UI raycast hit
        if (rayInteractor.TryGetCurrentUIRaycastResult(out RaycastResult uiHit))
        {
            Debug.Log($"SelectionManager: UI raycast hit '{uiHit.gameObject.name}'");
            
            // Check if the UI hit is part of WristUI
            if (IsPartOfWristUIHierarchy(uiHit.gameObject))
            {
                Debug.Log($"SelectionManager: UI hit is part of WristUI - preventing deselection");
                return true;
            }
        }
        
        return false;
    }

    bool IsPartOfWristUIHierarchy(GameObject obj)
    {
        Transform current = obj.transform;
        while (current != null)
        {
            if (current.GetComponent<WristMenuController>() != null)
            {
                return true;
            }
            current = current.parent;
        }
        return false;
    }

    bool IsHittingWristUI(GameObject hitObject)
    {
        // Debug logging to help troubleshoot
        Debug.Log($"SelectionManager: Checking if hit object '{hitObject.name}' is part of WristUI");
        
        // Check if the hit object is part of the WristUI hierarchy
        Transform current = hitObject.transform;
        
        while (current != null)
        {
            Debug.Log($"SelectionManager: Checking object '{current.name}' in hierarchy");

            // Check if this object has a WristMenuController component
            WristMenuController wristUI = current.GetComponent<WristMenuController>();
            if (wristUI != null)
            {
                Debug.Log($"SelectionManager: Found WristUI component on '{current.name}' - preventing deselection");
                return true;
            }
            
            // Check for common UI component names that indicate WristUI
            if (current.name.ToLower().Contains("wrist") || 
                current.name.ToLower().Contains("ui") ||
                current.name.ToLower().Contains("menu") ||
                current.name.ToLower().Contains("panel") ||
                current.name.ToLower().Contains("button"))
            {
                Debug.Log($"SelectionManager: Found UI-related object '{current.name}' - preventing deselection");
                return true;
            }
            
            // Check if this object has a Canvas component (UI elements)
            Canvas canvas = current.GetComponent<Canvas>();
            if (canvas != null)
            {
                Debug.Log($"SelectionManager: Found Canvas on '{current.name}' - checking if it's part of WristUI");
                
                // Check if the canvas belongs to WristUI by looking for WristUI in parent hierarchy
                Transform canvasParent = canvas.transform.parent;
                while (canvasParent != null)
                {
                    if (canvasParent.GetComponent<WristMenuController>() != null)
                    {
                        Debug.Log($"SelectionManager: Canvas belongs to WristUI - preventing deselection");
                        return true;
                    }
                    canvasParent = canvasParent.parent;
                }
            }
            
            // Move up the hierarchy
            current = current.parent;
        }
        
        Debug.Log($"SelectionManager: Object '{hitObject.name}' is not part of WristUI - allowing deselection");
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
