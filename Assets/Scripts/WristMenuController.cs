using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class WristMenuController : MonoBehaviour
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;
    
    [Header("Input Actions")]
    public InputActionAsset inputActions;
    
    [Header("Materials")]
    public Material litMaterial;
    
    [Header("Selection Manager")]
    public SelectionManager selectionManager;
    
    // UI Elements
    private VisualElement root;
    private VisualElement wristMenuMainPanel;
    private VisualElement wristMenuOptionsPanel;
    private VisualElement wristMenuAddShapePanel;
    
    // Buttons
    private Button addShapeButton;
    private Button resizeShapeButton;
    private Button deleteShapeButton;
    private Button addShapeBackButton;
    private Button addCubeButton;
    private Button addSphereButton;
    private Button addCylinderButton;
    
    // Input Actions
    private InputAction menuAction;
    
    // State
    private bool isMenuVisible = false;
    
    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();
            
        root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("WristMenuController: No UIDocument/rootVisualElement found.");
            return;
        }
        
        InitializeUIElements();
        SetupEventHandlers();
        SetupInputActions();
        
        // Initially hide the menu
        SetMenuVisibility(false);
    }
    
    private void InitializeUIElements()
    {
        // Get main panels
        wristMenuMainPanel = root.Q<VisualElement>("wristMenuMainPanel");
        wristMenuOptionsPanel = root.Q<VisualElement>("wristMenuOptionsPanel");
        wristMenuAddShapePanel = root.Q<VisualElement>("wristMenuAddShapePanel");
        
        // Get buttons from options panel
        addShapeButton = root.Q<Button>("wristMenuAddShapeButton");
        resizeShapeButton = root.Q<Button>("wristMenuResizeShapeButton");
        deleteShapeButton = root.Q<Button>("wristMenuDeleteShapeButton");
        
        // Get buttons from add shape panel
        addShapeBackButton = root.Q<Button>("wristMenuAddShapeBackButton");
        addCubeButton = root.Q<Button>("wristMenuAddCubeButton");
        addSphereButton = root.Q<Button>("wristMenuAddSphereButton");
        addCylinderButton = root.Q<Button>("wristMenuAddCylinderButton");
        
        // Validate UI elements
        if (wristMenuOptionsPanel == null || wristMenuAddShapePanel == null)
        {
            Debug.LogError("WristMenuController: Main panels not found in UXML.");
            return;
        }
        
        if (addShapeButton == null || resizeShapeButton == null || deleteShapeButton == null)
        {
            Debug.LogError("WristMenuController: Main buttons not found in UXML.");
            return;
        }
        
        if (addShapeBackButton == null || addCubeButton == null || addSphereButton == null || addCylinderButton == null)
        {
            Debug.LogError("WristMenuController: Add shape panel buttons not found in UXML.");
            return;
        }
        
        // Initially hide the add shape panel
        ShowOptionsPanel();
    }
    
    private void SetupEventHandlers()
    {
        // Main options panel buttons
        addShapeButton.clicked += OnAddShapeClicked;
        resizeShapeButton.clicked += OnResizeShapeClicked;
        deleteShapeButton.clicked += OnDeleteShapeClicked;
        
        // Add shape panel buttons
        addShapeBackButton.clicked += OnAddShapeBackClicked;
        addCubeButton.clicked += OnAddCubeClicked;
        addSphereButton.clicked += OnAddSphereClicked;
        addCylinderButton.clicked += OnAddCylinderClicked;
    }

    private void SetupInputActions()
    {
        if (inputActions == null)
        {
            Debug.LogError("WristMenuController: InputActionAsset is not assigned.");
            return;
        }

        // Find the menu action from the input actions
        menuAction = inputActions.FindActionMap("XRI Left Interaction")?.FindAction("Menu");
        if (menuAction == null)
        {
            Debug.LogError("WristMenuController: Menu action not found in input actions.");
            return;
        }

        menuAction.performed += OnMenuToggle;
        menuAction.Enable();

        // if (root != null)
        // {
        //     root.SetEnabled(false);
        // }
    }
    
    private void OnMenuToggle(InputAction.CallbackContext context)
    {
        ToggleMenu();
    }
    
    public void ToggleMenu()
    {
        SetMenuVisibility(!isMenuVisible);
    }
    
    public void SetMenuVisibility(bool visible)
    {
        isMenuVisible = visible;

        if (root != null)
        {
            wristMenuMainPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            // root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            // root.SetEnabled(visible);
            // root.visible = visible;
            // if (visible == false)
            // {
            //     DisablePanels();
            // }
            // else
            // {
            //     ShowOptionsPanel();
            // }

            Collider collider = GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = visible;
            }
        }
        
        Debug.Log($"WristMenuController: Menu visibility set to {visible}");
    }
    
    private void ShowOptionsPanel()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.Flex;
            wristMenuOptionsPanel.SetEnabled(true);
        }

        if (wristMenuAddShapePanel != null)
        {
            wristMenuAddShapePanel.style.display = DisplayStyle.None;
            wristMenuAddShapePanel.SetEnabled(false);
        }
    }
    
    private void ShowAddShapePanel()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.None;
            wristMenuOptionsPanel.SetEnabled(false);
        }

        if (wristMenuAddShapePanel != null)
        {
            wristMenuAddShapePanel.style.display = DisplayStyle.Flex;
            wristMenuAddShapePanel.SetEnabled(true);
        }
    }

    private void DisablePanels()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.None;
            // wristMenuOptionsPanel.SetEnabled(false);
        }

        if (wristMenuAddShapePanel != null)
        {
            wristMenuAddShapePanel.style.display = DisplayStyle.None;
            // wristMenuAddShapePanel.SetEnabled(false);
        }
    }

    // Event Handlers
    private void OnAddShapeClicked()
    {
        ShowAddShapePanel();
        Debug.Log("WristMenuController: Add Shape panel opened");
    }
    
    private void OnResizeShapeClicked()
    {
        // TODO: Implement resize functionality
        Debug.Log("WristMenuController: Resize Shape functionality not yet implemented");
    }
    
    private void OnDeleteShapeClicked()
    {
        if (selectionManager != null)
        {
            selectionManager.DeleteSelectedObject();
            Debug.Log("WristMenuController: Delete selected object requested");
        }
        else
        {
            Debug.LogWarning("WristMenuController: SelectionManager not assigned - cannot delete object");
        }
    }
    
    private void OnAddShapeBackClicked()
    {
        ShowOptionsPanel();
        Debug.Log("WristMenuController: Back to options panel");
    }
    
    private void OnAddCubeClicked()
    {
        AddPrimitiveShape(PrimitiveType.Cube);
        ShowOptionsPanel(); // Return to main menu after adding shape
    }
    
    private void OnAddSphereClicked()
    {
        AddPrimitiveShape(PrimitiveType.Sphere);
        ShowOptionsPanel(); // Return to main menu after adding shape
    }
    
    private void OnAddCylinderClicked()
    {
        AddPrimitiveShape(PrimitiveType.Cylinder);
        ShowOptionsPanel(); // Return to main menu after adding shape
    }
    
    // Shape Creation Methods
    private void AddPrimitiveShape(PrimitiveType primitiveType)
    {
        // Create the primitive using Unity's built-in method
        GameObject shape = GameObject.CreatePrimitive(primitiveType);
        
        // Position the shape in front of the user
        if (Camera.main != null)
        {
            shape.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        }
        else
        {
            // Fallback position if no main camera
            shape.transform.position = Vector3.forward * 2f;
        }
        
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
        
        // Apply material
        var meshRenderer = shape.GetComponent<MeshRenderer>();
        if (meshRenderer != null && litMaterial != null)
        {
            meshRenderer.material = litMaterial;
        }
        
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
        
        Debug.Log($"WristMenuController: Created {primitiveType} with ID {publisher.objectId}");
    }
    
    // Public convenience methods for external access
    public void AddCube()
    {
        AddPrimitiveShape(PrimitiveType.Cube);
    }
    
    public void AddSphere()
    {
        AddPrimitiveShape(PrimitiveType.Sphere);
    }
    
    public void AddCylinder()
    {
        AddPrimitiveShape(PrimitiveType.Cylinder);
    }
    
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
        if (selectionManager != null)
        {
            selectionManager.DeleteSelectedObject();
        }
    }
    
    // Cleanup
    private void OnDisable()
    {
        if (menuAction != null)
        {
            menuAction.performed -= OnMenuToggle;
            menuAction.Disable();
        }
    }
    
    private void OnDestroy()
    {
        if (menuAction != null)
        {
            menuAction.performed -= OnMenuToggle;
            menuAction.Disable();
        }
    }
}
