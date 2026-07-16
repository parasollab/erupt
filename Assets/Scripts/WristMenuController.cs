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

    [Header("CollisionObjectsListener")]
    public CollisionObjectsListenerSimple collisionObjectsListener;
    public GameObject worldOrigin;

    [Header("MTC")]
    [SerializeField] private bool enableMTC = false;
    [SerializeField] private PickPlaceTaskRecorder pickPlaceRecorder;
    [SerializeField] private GameObject mtcDashboardPanel;

    [Header("Shape Spawning")]
    [SerializeField] private float shapeSpawnDistance = 0.75f;
    
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
    private Button snapSurfaceButton;
    private Button deleteShapeButton;
    private Button addShapeBackButton;
    private Button addCubeButton;
    private Button addSphereButton;
    private Button addCylinderButton;
    private Button editShapeBackButton;
    private Button duplicateShapeButton;
    private Button recordPickPlaceButton;
    private Label recordStatusLabel;
    private Button mtcButton;
    
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
    
    public VisualElement CreateToggleStack(string shape, string label)
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
            var selected = selectionManager.SelectedObject;
            if (selected != null)
            {
                var axisLock = selected.GetComponent<XRGrabTransformerScaleAxisLock>();
                if (axisLock != null)
                {
                    Debug.Log($"WristMenuController: Found XRGrabTransformerScaleAxisLock on selected object");
                    if (shape.Contains("Cube"))
                    {
                        Debug.Log($"WristMenuController: Toggling axis lock for Cube");
                        if (label == "X")
                        {
                            axisLock.freezeXScale = !evt.newValue;
                        }
                        else if (label == "Y")
                        {
                            axisLock.freezeYScale = !evt.newValue;
                        }
                        else if (label == "Z")
                        {
                            axisLock.freezeZScale = !evt.newValue;
                        }
                    }
                    else if (shape.Contains("Sphere"))
                    {
                        Debug.Log($"WristMenuController: Toggling axis lock for Sphere");
                        axisLock.freezeXScale = !evt.newValue;
                        axisLock.freezeYScale = !evt.newValue;
                        axisLock.freezeZScale = !evt.newValue;
                    }
                    else if (shape.Contains("Cylinder"))
                    {
                        Debug.Log($"WristMenuController: Toggling axis lock for Cylinder");
                        axisLock.freezeXScale = !evt.newValue;
                        axisLock.freezeYScale = !evt.newValue;
                        axisLock.freezeZScale = !evt.newValue;
                    }
                    else if (shape.Contains("Mesh"))
                    {
                        Debug.Log($"WristMenuController: Toggling axis lock for Mesh");
                        axisLock.freezeXScale = !evt.newValue;
                        axisLock.freezeYScale = !evt.newValue;
                        axisLock.freezeZScale = !evt.newValue;
                    }
                }
            }
            
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

        float prevValue = 100f;
        bool gestureActive = false;
        float gestureStartValue = 100f;
        IVisualElementScheduledItem gestureEndCheck = null;
        const float minScale = 0.01f;
        const long gestureEndDebounceMs = 200;

        void ResetToCenterDeferred()
        {
            slider.schedule.Execute(() =>
            {
                slider.SetValueWithoutNotify(100f);
                prevValue = 100f;
            }).ExecuteLater(0);
        }

        // Logs the resize once the gesture is considered finished (see the debounce comment
        // below), then recenters the slider for the next nudge.
        void FinishGesture()
        {
            gestureActive = false;

            float totalDeltaPercent = (slider.value - gestureStartValue) / 100f;
            if (!Mathf.Approximately(totalDeltaPercent, 0f))
            {
                GameObject selected = selectionManager.SelectedObject;
                CollisionObjectPublisher publisher = selected != null ? selected.GetComponent<CollisionObjectPublisher>() : null;
                if (publisher != null)
                {
                    string sign = totalDeltaPercent >= 0 ? "+" : "";
                    ObjectMetricsLogger.Instance?.LogEvent("edit_operation", publisher.objectId,
                        details: $"resize:{shape}:{label}:{sign}{totalDeltaPercent:F3}");
                }
                else
                {
                    Debug.LogWarning($"WristMenuController: resize on '{(selected != null ? selected.name : "null")}' " +
                        "not logged -- no CollisionObjectPublisher component (only wrist-menu-spawned shapes have one).");
                }
            }

            ResetToCenterDeferred();
        }

        Vector3 MakeDelta(string shapeName, string axisLabel, float delta)
        {
            if (shapeName.Contains("Cube"))
            {
                if (axisLabel == "X") return new Vector3(delta, 0f, 0f);
                if (axisLabel == "Y") return new Vector3(0f, delta, 0f);
                if (axisLabel == "Z") return new Vector3(0f, 0f, delta);
                return Vector3.zero;
            }
            else if (shapeName.Contains("Cylinder"))
            {
                // Support your "Height" and "Radius" UI
                if (axisLabel == "Height") return new Vector3(0f, delta, 0f);
                if (axisLabel == "Radius") return new Vector3(delta, 0f, delta);
                // fallback uniform
                return new Vector3(delta, delta, delta);
            }
            else
            {
                // Sphere / Mesh => uniform
                return new Vector3(delta, delta, delta);
            }
        }

        // Gesture start/end is detected purely from ValueChanged plus a debounce timer, not from
        // Pointer{Down,Up,Cancel}Events -- confirmed via on-device logcat that the XR poke input
        // bridge drives ValueChanged continuously and reliably during a drag, but never sends a
        // terminating PointerUp (even registered on the capture/TrickleDown phase, which does fix
        // PointerDown -- the thumb still swallows Up somewhere in the XR->UI Toolkit pipeline).
        slider.RegisterValueChangedCallback(evt =>
        {
            var selected = selectionManager.SelectedObject;
            if (selected == null) { prevValue = evt.newValue; return; }

            if (!gestureActive)
            {
                gestureActive = true;
                gestureStartValue = prevValue;
            }

            gestureEndCheck?.Pause();
            gestureEndCheck = slider.schedule.Execute(FinishGesture);
            gestureEndCheck.ExecuteLater(gestureEndDebounceMs);

            float delta = (evt.newValue - prevValue) / 100f;
            prevValue = evt.newValue;
            if (Mathf.Approximately(delta, 0f)) return;

            var d = MakeDelta(shape, label, delta);

            // Respect axis locks
            var axisLock = selected.GetComponent<XRGrabTransformerScaleAxisLock>();
            bool allowX = axisLock == null || !axisLock.freezeXScale;
            bool allowY = axisLock == null || !axisLock.freezeYScale;
            bool allowZ = axisLock == null || !axisLock.freezeZScale;
            if (!allowX) d.x = 0f;
            if (!allowY) d.y = 0f;
            if (!allowZ) d.z = 0f;

            var gi  = selected.GetComponent<XRGrabInteractable>();
            var uiT = selected.GetComponent<XRUIScaleTransformer>();

            if (gi != null && gi.isSelected && uiT != null)
            {
                // Object is grabbed → let the XRI pipeline apply it this frame
                uiT.queuedDelta += d;
            }
            else
            {
                // Not grabbed → apply directly
                var s = selected.transform.localScale + d;
                s.x = Mathf.Max(minScale, s.x);
                s.y = Mathf.Max(minScale, s.y);
                s.z = Mathf.Max(minScale, s.z);
                selected.transform.localScale = s;
            }
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
        snapSurfaceButton = root.Q<Button>("wristMenuSnapSurfaceButton");
        deleteShapeButton = root.Q<Button>("wristMenuDeleteShapeButton");
        duplicateShapeButton = root.Q<Button>("wristMenuDuplicateShapeButton");
        recordPickPlaceButton = root.Q<Button>("wristMenuRecordPickPlaceButton");
        recordStatusLabel = root.Q<Label>("wristMenuRecordStatusLabel");
        mtcButton = root.Q<Button>("wristMenuMTCButton");
        if (mtcButton != null)
            mtcButton.style.display = enableMTC ? DisplayStyle.Flex : DisplayStyle.None;
        if (recordPickPlaceButton != null)
            recordPickPlaceButton.style.display = enableMTC ? DisplayStyle.Flex : DisplayStyle.None;
        if (recordStatusLabel != null)
            recordStatusLabel.style.display = enableMTC ? DisplayStyle.Flex : DisplayStyle.None;

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
        snapSurfaceButton.clicked += OnSnapSurfaceClicked;
        deleteShapeButton.clicked += OnDeleteShapeClicked;
        duplicateShapeButton.clicked += OnDuplicateShapeClicked;
        if (recordPickPlaceButton != null && enableMTC)
            recordPickPlaceButton.clicked += OnRecordPickPlaceClicked;
        if (mtcButton != null && enableMTC)
            mtcButton.clicked += OnMTCClicked;

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
        
        if (wristMenuEditShapePanel != null)
        {
            wristMenuEditShapePanel.style.display = DisplayStyle.None;
            wristMenuEditShapePanel.SetEnabled(false);
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
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        string meshName = meshFilter.sharedMesh.name;

        if (meshName.Contains("Cube"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Cube");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "X"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "Y"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "Z"));
        }
        else if (meshName.Contains("Sphere"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Sphere");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Sphere", "Radius"));
        }
        else if (meshName.Contains("Cylinder"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Cylinder");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Cylinder", "Height"));
            wristMenuEditSliderPanel.Add(CreateToggleStack("Cylinder", "Radius"));
        }
        else if (meshName.Contains("Mesh"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Mesh");
            wristMenuEditSliderPanel.Add(CreateToggleStack("Mesh", "Scale"));
        }
        else
        {
            Debug.LogWarning($"WristMenuController: Unsupported shape '{meshName}' for editing.");
        }
    }
    
    private void OnEditShapeClicked()
    {
        if (selectionManager == null || selectionManager.SelectedObject == null)
        {
            Debug.LogWarning("WristMenuController: No object selected for editing.");
            return;
        }

        PopulateEditShapePanel(selectionManager.SelectedObject);
        ShowEditShapePanel();
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

    private void OnDuplicateShapeClicked()
    {
        DuplicateSelectedShape();
    }

    private void DuplicateSelectedShape()
    {
        if (selectionManager == null)
        {
            Debug.LogWarning("WristMenuController: SelectionManager not assigned - cannot duplicate object.");
            return;
        }

        GameObject original = selectionManager.SelectedObject;
        if (original == null)
        {
            Debug.LogWarning("WristMenuController: No object selected to duplicate.");
            return;
        }

        MeshFilter meshFilter = original.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.mesh == null)
        {
            Debug.LogWarning("WristMenuController: Selected object has no mesh - cannot duplicate.");
            return;
        }

        string meshName = meshFilter.mesh.name;
        bool isPrimitive = meshName.Contains("Cube") || meshName.Contains("Sphere") ||
                           meshName.Contains("Cylinder") || meshName.Contains("Capsule");

        Vector3 originalScale = original.transform.localScale;
        original.transform.GetPositionAndRotation(out Vector3 originalPosition, out Quaternion originalRotation);

        if (isPrimitive)
        {
            PrimitiveType primitiveType;
            if      (meshName.Contains("Cube"))     primitiveType = PrimitiveType.Cube;
            else if (meshName.Contains("Sphere"))   primitiveType = PrimitiveType.Sphere;
            else if (meshName.Contains("Cylinder")) primitiveType = PrimitiveType.Cylinder;
            else                                    primitiveType = PrimitiveType.Capsule;

            AddPrimitiveShape(primitiveType);

            GameObject duplicate = selectionManager.SelectedObject;
            if (duplicate == null) return;

            duplicate.transform.localScale = originalScale;
            duplicate.transform.SetPositionAndRotation(originalPosition + new Vector3(0.2f, 0f, 0f), originalRotation);

            Debug.Log($"WristMenuController: Duplicated '{original.name}' as '{duplicate.name}'");
        }
        else
        {
            // Non-primitive mesh — deep clone via Instantiate
            GameObject duplicate = Instantiate(original);
            duplicate.transform.SetPositionAndRotation(originalPosition + new Vector3(0.2f, 0f, 0f), originalRotation);
            duplicate.transform.localScale = originalScale;
            duplicate.tag = "Selectable";

            if (duplicate.TryGetComponent<CollisionObjectPublisher>(out var publisher))
            {
                string newId = $"unity_mesh_{System.DateTime.Now.Ticks}";
                publisher.objectId = newId;
                publisher.hasBeenPublished = false;
                publisher.worldOrigin = worldOrigin;
                if (collisionObjectsListener != null)
                    collisionObjectsListener.objectsById.Add(newId, duplicate);
            }

            if (selectionManager != null)
                selectionManager.SetSelectedObject(duplicate);

            Debug.Log($"WristMenuController: Duplicated mesh '{original.name}' as '{duplicate.name}'");
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
        GameObject shape = GameObject.CreatePrimitive(primitiveType);

        // Position in front of the user, within easy reach
        shape.transform.position = Camera.main != null
            ? Camera.main.transform.position + Camera.main.transform.forward * shapeSpawnDistance
            : Vector3.forward * shapeSpawnDistance;

        // Physics
        Rigidbody rb = shape.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;

        // XR interaction — Single mode so only the NearFarInteractor (one hand) holds it.
        // This keeps the InteractionAttachController's thumbstick push/pull working correctly.
        shape.AddComponent<XRGrabInteractable>();
        var gi = shape.GetComponent<XRGrabInteractable>();
        gi.selectMode = InteractableSelectMode.Single;

        // Controls grabbing based on SelectionManager selection state
        shape.AddComponent<SelectableGrabController>();

        shape.transform.localScale = Vector3.one * 0.5f;
        shape.tag = "Selectable";

        shape.AddComponent<XRGrabTransformerScaleAxisLock>();
        shape.AddComponent<XRGrabTransformerLockPose>();

        shape.AddComponent<XRGeneralGrabTransformer>();
        shape.GetComponent<XRGeneralGrabTransformer>().allowTwoHandedScaling = false;
        shape.GetComponent<XRGeneralGrabTransformer>().clampScaling = false;

        shape.AddComponent<XRTwoHandedScaleTransformer>();
        shape.AddComponent<XRUIScaleTransformer>();

        gi.AddMultipleGrabTransformer(shape.GetComponent<XRGeneralGrabTransformer>());
        gi.AddMultipleGrabTransformer(shape.GetComponent<XRTwoHandedScaleTransformer>());
        gi.AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerScaleAxisLock>());
        gi.AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerLockPose>());
        gi.AddMultipleGrabTransformer(shape.GetComponent<XRUIScaleTransformer>());

        var meshRenderer = shape.GetComponent<MeshRenderer>();
        if (meshRenderer != null && litMaterial != null)
            meshRenderer.material = litMaterial;

        Collider collider = shape.GetComponent<Collider>();
        if (collider != null)
            collider.enabled = true;

        CollisionObjectPublisher publisher = shape.AddComponent<CollisionObjectPublisher>();
        publisher.isMesh = false;
        publisher.objectId = $"unity_{primitiveType.ToString().ToLower()}_{System.DateTime.Now.Ticks}";
        publisher.worldOrigin = worldOrigin;

        if (selectionManager != null)
            selectionManager.SetSelectedObject(shape);
        if (collisionObjectsListener != null)
            collisionObjectsListener.objectsById.Add(publisher.objectId, shape);
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
    
    private void OnMTCClicked()
    {
        if (mtcDashboardPanel == null) return;
        mtcDashboardPanel.SetActive(!mtcDashboardPanel.activeSelf);
    }

    private void OnRecordPickPlaceClicked()
    {
        if (pickPlaceRecorder == null) return;

        if (!pickPlaceRecorder.IsRecording)
        {
            pickPlaceRecorder.OnRecordingComplete = OnPickPlaceRecorded;
            pickPlaceRecorder.StartRecording();
            if (recordPickPlaceButton != null)
            {
                recordPickPlaceButton.text = "Recording...";
                recordPickPlaceButton.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.yellow);
            }
            if (recordStatusLabel != null)
                recordStatusLabel.text = "Waiting for grab...";
        }
        else
        {
            pickPlaceRecorder.StopRecording();
            ResetRecordUI();
        }
    }

    private void OnPickPlaceRecorded(string objectId)
    {
        if (recordStatusLabel != null)
            recordStatusLabel.text = $"Sent: {objectId}";
        ResetRecordUI(resetLabel: false);
    }

    private void ResetRecordUI(bool resetLabel = true)
    {
        if (recordPickPlaceButton != null)
        {
            recordPickPlaceButton.text = "Record Pick & Place";
            recordPickPlaceButton.style.color = new UnityEngine.UIElements.StyleColor(StyleKeyword.Null);
        }
        if (resetLabel && recordStatusLabel != null)
            recordStatusLabel.text = "Idle";
    }

    private void OnSnapSurfaceClicked()
    {
        SnapSelectedToSurface();
    }

    private void SnapSelectedToSurface()
    {
        GameObject selected = selectionManager?.SelectedObject;
        if (selected == null)
        {
            Debug.LogWarning("WristMenuController: No object selected to snap.");
            return;
        }

        Collider col = selected.GetComponent<Collider>();
        Vector3 rayOrigin = col != null ? col.bounds.center : selected.transform.position;

        // Temporarily disable collider so the raycast doesn't self-hit
        if (col != null) col.enabled = false;
        RaycastHit[] hits = Physics.RaycastAll(rayOrigin, Vector3.down, Mathf.Infinity);
        if (col != null) col.enabled = true;

        // Find the closest hit tagged "SnapSurface"
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit h in hits)
        {
            if (!h.collider.CompareTag("SnapSurface")) continue;

            // Align object's +Y with the surface normal (minimal rotation, preserves yaw)
            Quaternion alignmentRot = Quaternion.FromToRotation(selected.transform.up, h.normal);
            selected.transform.rotation = alignmentRot * selected.transform.rotation;

            // Place the object so its bottom face center sits at the hit point
            float halfHeight = GetColliderLocalHalfHeight(col, selected.transform);
            selected.transform.position = h.point + h.normal * halfHeight;

            // Update the lock-pose transformer's cached rotation so grabs don't revert it
            var lockPose = selected.GetComponent<XRGrabTransformerLockPose>();
            if (lockPose != null) lockPose.SyncInitialRotation();

            Debug.Log($"WristMenuController: Snapped '{selected.name}' to '{h.collider.name}' normal={h.normal}");

            CollisionObjectPublisher publisher = selected.GetComponent<CollisionObjectPublisher>();
            if (publisher != null)
            {
                ObjectMetricsLogger.Instance?.LogEvent("edit_operation", publisher.objectId, details: "snap");
            }
            else
            {
                Debug.LogWarning($"WristMenuController: snap on '{selected.name}' not logged -- " +
                    "no CollisionObjectPublisher component (only wrist-menu-spawned shapes have one).");
            }
            return;
        }

        Debug.LogWarning("WristMenuController: No 'SnapSurface' tagged object found below selected object.");
    }

    private float GetColliderLocalHalfHeight(Collider col, Transform t)
    {
        if (col == null) return t.lossyScale.y * 0.5f;
        float scaleY = Mathf.Abs(t.lossyScale.y);
        if (col is BoxCollider box)     return box.size.y * 0.5f * scaleY;
        if (col is SphereCollider sph)  return sph.radius * scaleY;
        if (col is CapsuleCollider cap) return cap.height * 0.5f * scaleY;
        return col.bounds.extents.y; // fallback
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
