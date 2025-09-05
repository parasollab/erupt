using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

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
    private VisualElement wristMenuEditShapePanel;
    private VisualElement wristMenuEditSliderPanel;

    // Buttons
    private Button addShapeButton;
    private Button editShapeButton;
    private Button deleteShapeButton;
    private Button addShapeBackButton;
    private Button addCubeButton;
    private Button addSphereButton;
    private Button addCylinderButton;
    private Button editShapeBackButton;
    
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
    
    public VisualElement CreateToggleStack(string label)
    {
        // <ui:VisualElement name="toggle-stack" style="flex-direction: column; align-items: stretch;">
        var container = new VisualElement { name = "wristMenuEditScale" + label + "Stack" };
        container.style.flexDirection = FlexDirection.Column;
        container.style.alignItems = Align.Stretch;

        // <ui:Toggle name="my-toggle" label="Enable Feature"
        //    style="flex-direction: row-reverse; overflow: hidden; right: auto; justify-content: flex-start; padding-left: 10px; padding-right: 10px;" />
        var toggle = new Toggle("Scale " + label + ": ") { name = "wristMenuEditScale" + label + "Toggle"};
        toggle.style.flexDirection = FlexDirection.RowReverse;
        toggle.style.overflow = Overflow.Hidden;
        toggle.style.right = new StyleLength(StyleKeyword.Auto);
        toggle.style.justifyContent = Justify.FlexStart;
        toggle.style.paddingLeft = 10;
        toggle.style.paddingRight = 10;
        toggle.value = true; // Default to enabled
        toggle.RegisterCallback<ChangeEvent<bool>>((evt) =>
        {
            Debug.Log($"WristMenuController: Toggle '{label}' changed to {evt.newValue}");
        });

        // <ui:Slider name="my-slider" low-value="0" high-value="200" value="50"
        //    style="margin-top: 8px; flex-grow: 1; padding-left: 10px; padding-right: 10px;" />
        var slider = new Slider(0, 200) { name = "wristMenuEditScale" + label + "Slider" };
        slider.value = 100f;
        slider.style.marginTop = 8;
        slider.style.flexGrow = 1;
        slider.style.paddingLeft = 10;
        slider.style.paddingRight = 10;
        slider.RegisterCallback<ChangeEvent<float>>((evt) =>
        {
            Debug.Log($"WristMenuController: Slider '{label}' changed to {evt.newValue}");
        });
        slider.RegisterCallback<PointerCancelEvent>(_ =>
        {
            slider.SetValueWithoutNotify(100f);
            Debug.Log($"WristMenuController: Slider '{label}' interaction ended at {slider.value}");
        });

        container.Add(toggle);
        container.Add(slider);

        return container;
    }
    
    private void InitializeUIElements()
    {
        // Get main panels
        wristMenuMainPanel = root.Q<VisualElement>("wristMenuMainPanel");
        wristMenuOptionsPanel = root.Q<VisualElement>("wristMenuOptionsPanel");
        wristMenuAddShapePanel = root.Q<VisualElement>("wristMenuAddShapePanel");
        wristMenuEditShapePanel = root.Q<VisualElement>("wristMenuEditShapePanel");
        wristMenuEditSliderPanel = root.Q<VisualElement>("wristMenuEditSliderPanel");

        // Get buttons from options panel
        addShapeButton = root.Q<Button>("wristMenuAddShapeButton");
        editShapeButton = root.Q<Button>("wristMenuEditShapeButton");
        deleteShapeButton = root.Q<Button>("wristMenuDeleteShapeButton");

        // Get buttons from add shape panel
        addShapeBackButton = root.Q<Button>("wristMenuAddShapeBackButton");
        addCubeButton = root.Q<Button>("wristMenuAddCubeButton");
        addSphereButton = root.Q<Button>("wristMenuAddSphereButton");
        addCylinderButton = root.Q<Button>("wristMenuAddCylinderButton");

        // Get buttons from edit shape panel
        editShapeBackButton = root.Q<Button>("wristMenuEditShapeBackButton");

        // Validate UI elements
        if (wristMenuOptionsPanel == null || wristMenuAddShapePanel == null || wristMenuEditShapePanel == null)
        {
            Debug.LogError("WristMenuController: Main panels not found in UXML.");
            return;
        }

        if (addShapeButton == null || editShapeButton == null || deleteShapeButton == null)
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
        editShapeButton.clicked += OnEditShapeClicked;
        deleteShapeButton.clicked += OnDeleteShapeClicked;

        // Add shape panel buttons
        addShapeBackButton.clicked += OnAddShapeBackClicked;
        addCubeButton.clicked += OnAddCubeClicked;
        addSphereButton.clicked += OnAddSphereClicked;
        addCylinderButton.clicked += OnAddCylinderClicked;

        // Edit shape panel buttons
        editShapeBackButton.clicked += OnEditShapeBackClicked;
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

    private void ShowEditShapePanel()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.None;
            wristMenuOptionsPanel.SetEnabled(false);
        }

        if (wristMenuEditShapePanel != null)
        {
            wristMenuEditShapePanel.style.display = DisplayStyle.Flex;
            wristMenuEditShapePanel.SetEnabled(true);
        }
    }

    // Event Handlers
    private void OnAddShapeClicked()
    {
        ShowAddShapePanel();
        Debug.Log("WristMenuController: Add Shape panel opened");
    }

    private void PopulateEditShapePanel(GameObject selectedObject)
    {
        if (wristMenuEditSliderPanel == null)
        {
            Debug.LogWarning("WristMenuController: Edit slider panel not found.");
            return;
        }

        // Clear existing elements
        wristMenuEditSliderPanel.Clear();

        if (selectedObject == null)
        {
            Debug.LogWarning("WristMenuController: No object selected for editing.");
            return;
        }

        MeshFilter meshFilter = selectedObject.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            return;
        }

        string meshName = meshFilter.mesh.name;

        if (meshName.Contains("Cube"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Cube");
            wristMenuEditSliderPanel.Add(CreateToggleStack("X"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Y"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Z"));
        }
        else if (meshName.Contains("Sphere"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Sphere");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Radius"));
        }
        else if (meshName.Contains("Cylinder"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Cylinder");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Height"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Radius"));
        }
        else if (meshName.Contains("Mesh"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Mesh");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Scale"));
        }
        else
        {
            Debug.LogWarning($"WristMenuController: Unsupported shape '{meshName}' for editing.");
        }
    }
    
    private void OnEditShapeClicked()
    {
        // TODO: Implement edit functionality

        PopulateEditShapePanel(selectionManager.SelectedObject);

        ShowEditShapePanel();
        Debug.Log("WristMenuController: Edit Shape functionality not yet implemented");
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

    private void OnEditShapeBackClicked()
    {
        // Clear elements added to edit shape when shown
        wristMenuEditSliderPanel.Clear();
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
        shape.GetComponent<XRGrabInteractable>().selectMode = InteractableSelectMode.Multiple;
        
        // Add component to control grabbing based on selection state
        shape.AddComponent<SelectableGrabController>();
        
        // Set default scale
        shape.transform.localScale = Vector3.one;
        
        // Add tag for selection
        shape.tag = "Selectable";

        shape.AddComponent<XRGrabTransformerScaleAxisLock>();
        shape.AddComponent<XRGrabTransformerLockPose>();

        // Add an XR General Grab Transformer
        shape.AddComponent<XRGeneralGrabTransformer>();
        shape.GetComponent<XRGeneralGrabTransformer>().allowTwoHandedScaling = true;

        shape.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(shape.GetComponent<XRGeneralGrabTransformer>());

        shape.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerScaleAxisLock>());

        shape.GetComponent<XRGrabInteractable>().AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerLockPose>());

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
        publisher.isMesh = false;
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
