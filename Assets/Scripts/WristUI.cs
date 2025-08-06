using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class WristUI : MonoBehaviour
{

    public InputActionAsset inputActions;
    private Canvas _wristUICanvas;
    private InputAction _menu;

    private GameObject addShapePanel;
    private GameObject resizePanel;

    public Material litMaterial;

    // Remove individual prefab references since we'll use Unity primitives
    // public GameObject spherePrefab;
    // public GameObject cubePrefab;
    // public GameObject cylinderPrefab;

    public SelectionManager selectionManager;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _wristUICanvas = GetComponent<Canvas>();
        _menu = inputActions.FindActionMap("XRI Left Interaction").FindAction("Menu");
        _menu.performed += ToggleMenu;

        // Set the game object this script is attached to as inactive initially
        if (_wristUICanvas != null)
        {
            _wristUICanvas.enabled = false;
        }

        // Get panels as children of the game object this script is attached to
        addShapePanel = transform.Find("ShapeSelectorPanel")?.gameObject;
        resizePanel = transform.Find("ResizePanel")?.gameObject;

        if (addShapePanel != null)
        {
            addShapePanel.SetActive(false);
        }

        if (resizePanel != null)
        {
            resizePanel.SetActive(false);
        }
    }

    public void OpenAddShapePanel()
    {
        if (addShapePanel != null)
        {
            addShapePanel.SetActive(true);
            if (resizePanel != null)
            {
                resizePanel.SetActive(false);
            }
        }
    }

    public void OpenResizePanel()
    {
        if (resizePanel != null)
        {
            resizePanel.SetActive(true);
            if (addShapePanel != null)
            {
                addShapePanel.SetActive(false);
            }
        }
    }

    public void ClosePanels()
    {
        if (addShapePanel != null)
        {
            addShapePanel.SetActive(false);
        }

        if (resizePanel != null)
        {
            resizePanel.SetActive(false);
        }
    }

    // Generalized method to create any primitive shape
    public void AddPrimitiveShape(PrimitiveType primitiveType)
    {
        // Create the primitive using Unity's built-in method
        GameObject shape = GameObject.CreatePrimitive(primitiveType);
        
        // Position the shape in front of the user
        shape.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        
        // Add physics components
        Rigidbody rb = shape.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;
        
        // Add XR interaction (will be controlled by SelectableGrabController)
        shape.AddComponent<XRGrabInteractable>();
        
        // Add component to control grabbing based on selection state
        shape.AddComponent<SelectableGrabController>();
        
        // Set default scale
        shape.transform.localScale = Vector3.one;
        
        // Add tag for selection
        shape.tag = "Selectable";

        var meshRenderer = shape.GetComponent<MeshRenderer>();
        if (meshRenderer != null && litMaterial != null)
            meshRenderer.material = litMaterial;
        
        // Ensure collider is enabled (should already be there from CreatePrimitive)
        Collider collider = shape.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = true;
        }

        // Add CollisionObjectPublisher to automatically publish to ROS
        CollisionObjectPublisher publisher = shape.AddComponent<CollisionObjectPublisher>();
        // Generate unique ID for each object
        publisher.objectId = $"unity_{primitiveType.ToString().ToLower()}_{System.DateTime.Now.Ticks}";
        
        // Automatically select the newly created object so user can immediately grab it
        if (selectionManager != null)
        {
            selectionManager.SetSelectedObject(shape);
        }
    }

    // Convenience methods for specific shapes
    public void AddSphere()
    {
        AddPrimitiveShape(PrimitiveType.Sphere);
    }

    public void AddCube()
    {
        AddPrimitiveShape(PrimitiveType.Cube);
    }

    public void AddCylinder()
    {
        AddPrimitiveShape(PrimitiveType.Cylinder);
    }

    // Additional primitive types you might want
    public void AddCapsule()
    {
        AddPrimitiveShape(PrimitiveType.Capsule);
    }

    public void AddPlane()
    {
        AddPrimitiveShape(PrimitiveType.Plane);
    }

    public void DeleteSelectedObject()
    {
        selectionManager.DeleteSelectedObject();
    }

    private void ToggleMenu(InputAction.CallbackContext context)
    {
        if (_wristUICanvas != null)
        {
            _wristUICanvas.enabled = !_wristUICanvas.enabled;
        }
    }

    private void OnDestroy()
    {
        _menu.performed -= ToggleMenu;
    }
}
