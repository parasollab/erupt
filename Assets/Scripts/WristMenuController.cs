using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class WristMenuController : MonoBehaviour
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;
    
    [Header("Input Actions")]
    public InputActionAsset inputActions;

    [Header("VisionOS Palm Menu Gesture")]
    [SerializeField] private bool enablePalmMenuGesture = true;
    [SerializeField] private bool palmMenuDebugLogs = false;

    [Header("VisionOS Runtime Menu Layout")]
    [SerializeField] private Vector2 visionOSWristMenuSizeMeters = new Vector2(1.05f, 0.8f);
    [SerializeField] private Vector3 visionOSWristMenuOffsetMeters = new Vector3(0f, -0.18f, 0f);
    [SerializeField] private Vector3 visionOSGrabHandleSizeMeters = new Vector3(0.9f, 0.07f, 0.025f);
    [SerializeField] private Vector3 visionOSGrabHandleOffsetMeters = new Vector3(0f, 0.28f, -0.02f);
    [SerializeField] private Color visionOSGrabHandleColor = new Color(0.18f, 0.62f, 0.82f, 0.95f);
    
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

    // Runtime uGUI fallback used for PolySpatial/visionOS. UI Toolkit world-space panels are not
    // rendered reliably in RealityKit immersion, while world-space Canvas is the supported path.
    private Canvas uguiCanvas;
    private GameObject uguiRoot;
    private GameObject uguiGrabHandle;
    private GameObject uguiOptionsPanel;
    private GameObject uguiAddShapePanel;
    private GameObject uguiEditShapePanel;
    private RectTransform uguiEditSliderPanel;
    private UnityEngine.UI.Text uguiRecordButtonText;
    private UnityEngine.UI.Text uguiRecordStatusLabel;
    private bool useUGUIRuntimeMenu;
    
    // Input Actions
    private InputAction menuAction;
    
    // State
    private bool isMenuVisible = false;
    public bool IsMenuVisible => isMenuVisible;
    
    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        useUGUIRuntimeMenu = ShouldUseUGUIRuntimeMenu() || uiDocument == null;

        if (useUGUIRuntimeMenu)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            EnsureUGUIWristMenu();
        }
        else
        {
            root = uiDocument?.rootVisualElement;
            if (root == null)
            {
                Debug.LogError("WristMenuController: No UIDocument/rootVisualElement found.");
                return;
            }

            InitializeUIElements();
            SetupEventHandlers();
        }

        SetupInputActions();
        EnsurePalmMenuGesture();
        
        // Initially hide the menu
        SetMenuVisibility(false);
    }

    private static bool ShouldUseUGUIRuntimeMenu()
    {
#if UNITY_EDITOR
        return EditorUserBuildSettings.activeBuildTarget.ToString() == "VisionOS";
#elif UNITY_VISIONOS
        return true;
#else
        return false;
#endif
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
                        scale: selected.transform.localScale,
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

    private void EnsureUGUIWristMenu()
    {
        if (uguiRoot != null)
            return;

        uguiCanvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS Wrist Menu Canvas",
            new Vector2(1050f, 800f),
            visionOSWristMenuSizeMeters,
            visionOSWristMenuOffsetMeters,
            sortingOrder: 150);
        EnsureVisionOSGrabHandle();

        if (uguiCanvas.worldCamera == null && Camera.main != null)
            uguiCanvas.worldCamera = Camera.main;

        uguiRoot = uguiCanvas.gameObject;
        VisionOSSampleControlsUI.ClearChildren(uguiRoot.transform);

        uguiOptionsPanel = CreateUGUIPanel("Options");
        CreateUGUIButton(uguiOptionsPanel.transform, "Add Shape", OnAddShapeClicked);
        CreateUGUIButton(uguiOptionsPanel.transform, "Edit Shape", OnEditShapeClicked);
        CreateUGUIButton(uguiOptionsPanel.transform, "Snap Surface", OnSnapSurfaceClicked);
        CreateUGUIButton(uguiOptionsPanel.transform, "Duplicate Shape", OnDuplicateShapeClicked);
        CreateUGUIButton(uguiOptionsPanel.transform, "Delete Shape", OnDeleteShapeClicked);
        if (enableMTC)
        {
            var recordButton = CreateUGUIButton(uguiOptionsPanel.transform, "Record Pick & Place", OnRecordPickPlaceClicked);
            uguiRecordButtonText = recordButton.GetComponentInChildren<UnityEngine.UI.Text>();
            uguiRecordStatusLabel = CreateUGUIText(uguiOptionsPanel.transform, "Idle", 18, TextAnchor.MiddleCenter);
            CreateUGUIButton(uguiOptionsPanel.transform, "MTC", OnMTCClicked);
        }

        uguiAddShapePanel = CreateUGUIPanel("Add Shape");
        CreateUGUIButton(uguiAddShapePanel.transform, "Back", OnAddShapeBackClicked);
        CreateUGUIButton(uguiAddShapePanel.transform, "Cube", OnAddCubeClicked);
        CreateUGUIButton(uguiAddShapePanel.transform, "Sphere", OnAddSphereClicked);
        CreateUGUIButton(uguiAddShapePanel.transform, "Cylinder", OnAddCylinderClicked);

        uguiEditShapePanel = CreateUGUIPanel("Edit Shape");
        CreateUGUIButton(uguiEditShapePanel.transform, "Back", OnEditShapeBackClicked);
        var sliderContainer = new GameObject("Sliders");
        sliderContainer.transform.SetParent(uguiEditShapePanel.transform, false);
        uguiEditSliderPanel = sliderContainer.AddComponent<RectTransform>();
        var layout = sliderContainer.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;
        layout.spacing = 8f;

        ShowOptionsPanel();
        Debug.Log("WristMenuController: Created visionOS uGUI wrist menu.");
    }

    private Canvas FindDirectChildCanvas(string canvasName)
    {
        Transform child = transform.Find(canvasName);
        return child != null ? child.GetComponent<Canvas>() : null;
    }

    private void EnsureVisionOSGrabHandle()
    {
        const string handleName = "VisionOS Grab Handle";

        Transform handleTransform = transform.Find(handleName);
        GameObject handleObject;
        if (handleTransform == null)
        {
            handleObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handleObject.name = handleName;
            handleObject.transform.SetParent(transform, false);

            var primitiveCollider = handleObject.GetComponent<Collider>();
            if (primitiveCollider != null)
            {
                if (Application.isPlaying)
                    Destroy(primitiveCollider);
                else
                    DestroyImmediate(primitiveCollider);
            }
        }
        else
        {
            handleObject = handleTransform.gameObject;
        }

        Vector3 localOffset = MetersToLocal(visionOSGrabHandleOffsetMeters);
        Vector3 localSize = MetersToLocal(visionOSGrabHandleSizeMeters);
        handleObject.transform.localPosition = localOffset;
        handleObject.transform.localRotation = Quaternion.identity;
        handleObject.transform.localScale = localSize;
        uguiGrabHandle = handleObject;

        var renderer = handleObject.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.enabled = true;
            renderer.sharedMaterial = CreateVisionOSHandleMaterial();
        }

        var grabCollider = GetComponent<BoxCollider>();
        if (grabCollider != null)
        {
            grabCollider.center = localOffset;
            grabCollider.size = localSize;
        }
    }

    private Vector3 MetersToLocal(Vector3 meters)
    {
        return meters / VisionOSSampleControlsUI.GetUniformScale(transform.lossyScale);
    }

    private Material CreateVisionOSHandleMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");

        var material = new Material(shader);
        material.name = "VisionOS Grab Handle Material";
        material.color = visionOSGrabHandleColor;
        return material;
    }

    private GameObject CreateUGUIPanel(string panelName)
    {
        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            uguiRoot.transform,
            "Wrist " + panelName + " Panel",
            new Vector2(1050f, 800f));
        CreateUGUIText(panel.transform, panelName, 50, TextAnchor.MiddleCenter);
        return panel;
    }

    private UnityEngine.UI.Button CreateUGUIButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        return VisionOSSampleControlsUI.CreateButton(parent, label, onClick, 950f, 100f, 35);
    }

    private UnityEngine.UI.Text CreateUGUIText(Transform parent, string text, int fontSize, TextAnchor alignment)
    {
        return VisionOSSampleControlsUI.CreateText(parent, text, fontSize, alignment, VisionOSSampleControlsUI.TextColor);
    }

    private static Font GetBuiltinFont()
    {
        return VisionOSSampleControlsUI.GetBuiltinFont();
    }

    private void AddUGUIBackground(GameObject target, Color color)
    {
        VisionOSSampleControlsUI.AddImage(target, color, raycastTarget: true);
    }

    private void PopulateUGUIEditShapePanel(GameObject selectedObject, string meshName)
    {
        ClearUGUIEditSliderPanel();

        if (meshName.Contains("Cube"))
        {
            CreateUGUIScaleRow("Cube", "X");
            CreateUGUIScaleRow("Cube", "Y");
            CreateUGUIScaleRow("Cube", "Z");
        }
        else if (meshName.Contains("Sphere"))
        {
            CreateUGUIScaleRow("Sphere", "Radius");
        }
        else if (meshName.Contains("Cylinder"))
        {
            CreateUGUIScaleRow("Cylinder", "Height");
            CreateUGUIScaleRow("Cylinder", "Radius");
        }
        else if (meshName.Contains("Mesh"))
        {
            CreateUGUIScaleRow("Mesh", "Scale");
        }
    }

    private void CreateUGUIScaleRow(string shape, string label)
    {
        if (uguiEditSliderPanel == null)
            return;

        var row = new GameObject("Scale " + label);
        row.transform.SetParent(uguiEditSliderPanel, false);
        var rowRect = row.AddComponent<RectTransform>();
        rowRect.sizeDelta = new Vector2(950f, 100f);

        var rowLayout = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = false;
        rowLayout.childForceExpandWidth = false;
        rowLayout.spacing = 25f;

        var toggleObject = new GameObject("Scale " + label + " Toggle");
        toggleObject.transform.SetParent(row.transform, false);
        var toggleRect = toggleObject.AddComponent<RectTransform>();
        toggleRect.sizeDelta = new Vector2(220f, 80f);
        var toggleBackground = toggleObject.AddComponent<UnityEngine.UI.Image>();
        toggleBackground.color = Color.white;
        var toggle = toggleObject.AddComponent<UnityEngine.UI.Toggle>();
        toggle.targetGraphic = toggleBackground;
        toggle.isOn = true;
        toggle.onValueChanged.AddListener(value => SetScaleAxisEnabled(shape, label, value));
        CreateUGUIText(toggleObject.transform, label, 35, TextAnchor.MiddleCenter).raycastTarget = false;

        var sliderObject = new GameObject("Scale " + label + " Slider");
        sliderObject.transform.SetParent(row.transform, false);
        var sliderRect = sliderObject.AddComponent<RectTransform>();
        sliderRect.sizeDelta = new Vector2(680f, 80f);
        var layoutElement = sliderObject.AddComponent<UnityEngine.UI.LayoutElement>();
        layoutElement.preferredWidth = 680f;
        layoutElement.minHeight = 80f;

        var slider = sliderObject.AddComponent<UnityEngine.UI.Slider>();
        slider.minValue = 0f;
        slider.maxValue = 200f;
        slider.value = 100f;

        var background = new GameObject("Background");
        background.transform.SetParent(sliderObject.transform, false);
        var backgroundRect = background.AddComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0f, 0.4f);
        backgroundRect.anchorMax = new Vector2(1f, 0.6f);
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        background.AddComponent<UnityEngine.UI.Image>().color = new Color(0.78f, 0.78f, 0.78f, 1f);

        var fill = new GameObject("Fill");
        fill.transform.SetParent(background.transform, false);
        var fillRect = fill.AddComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        fill.AddComponent<UnityEngine.UI.Image>().color = new Color(0.2f, 0.62f, 0.82f, 1f);

        var handle = new GameObject("Handle");
        handle.transform.SetParent(sliderObject.transform, false);
        var handleRect = handle.AddComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(40f, 70f);
        handle.AddComponent<UnityEngine.UI.Image>().color = VisionOSSampleControlsUI.TextColor;

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.targetGraphic = handle.GetComponent<UnityEngine.UI.Image>();

        float previousValue = 100f;
        slider.onValueChanged.AddListener(value =>
        {
            float delta = (value - previousValue) / 100f;
            previousValue = value;
            if (!Mathf.Approximately(delta, 0f))
                ApplyScaleDelta(selectionManager?.SelectedObject, shape, label, delta);
        });
    }

    private void SetScaleAxisEnabled(string shape, string label, bool enabled)
    {
        var selected = selectionManager?.SelectedObject;
        if (selected == null)
            return;

        var axisLock = selected.GetComponent<XRGrabTransformerScaleAxisLock>();
        if (axisLock == null)
            return;

        if (shape.Contains("Cube"))
        {
            if (label == "X") axisLock.freezeXScale = !enabled;
            else if (label == "Y") axisLock.freezeYScale = !enabled;
            else if (label == "Z") axisLock.freezeZScale = !enabled;
        }
        else
        {
            axisLock.freezeXScale = !enabled;
            axisLock.freezeYScale = !enabled;
            axisLock.freezeZScale = !enabled;
        }
    }

    private void ApplyScaleDelta(GameObject selected, string shape, string label, float delta)
    {
        if (selected == null)
            return;

        Vector3 d = MakeScaleDelta(shape, label, delta);

        var axisLock = selected.GetComponent<XRGrabTransformerScaleAxisLock>();
        if (axisLock != null)
        {
            if (axisLock.freezeXScale) d.x = 0f;
            if (axisLock.freezeYScale) d.y = 0f;
            if (axisLock.freezeZScale) d.z = 0f;
        }

        var grab = selected.GetComponent<XRGrabInteractable>();
        var uiScaleTransformer = selected.GetComponent<XRUIScaleTransformer>();
        if (grab != null && grab.isSelected && uiScaleTransformer != null)
        {
            uiScaleTransformer.queuedDelta += d;
            return;
        }

        const float minScale = 0.01f;
        Vector3 scale = selected.transform.localScale + d;
        scale.x = Mathf.Max(minScale, scale.x);
        scale.y = Mathf.Max(minScale, scale.y);
        scale.z = Mathf.Max(minScale, scale.z);
        selected.transform.localScale = scale;
    }

    private Vector3 MakeScaleDelta(string shapeName, string axisLabel, float delta)
    {
        if (shapeName.Contains("Cube"))
        {
            if (axisLabel == "X") return new Vector3(delta, 0f, 0f);
            if (axisLabel == "Y") return new Vector3(0f, delta, 0f);
            if (axisLabel == "Z") return new Vector3(0f, 0f, delta);
            return Vector3.zero;
        }

        if (shapeName.Contains("Cylinder"))
        {
            if (axisLabel == "Height") return new Vector3(0f, delta, 0f);
            if (axisLabel == "Radius") return new Vector3(delta, 0f, delta);
        }

        return new Vector3(delta, delta, delta);
    }

    private void ClearUGUIEditSliderPanel()
    {
        if (uguiEditSliderPanel == null)
            return;

        for (int i = uguiEditSliderPanel.childCount - 1; i >= 0; i--)
            Destroy(uguiEditSliderPanel.GetChild(i).gameObject);
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

    private void EnsurePalmMenuGesture()
    {
        if (!enablePalmMenuGesture)
            return;

        var gesture = GetComponent<WristPalmMenuGesture>();
        if (gesture == null)
            gesture = gameObject.AddComponent<WristPalmMenuGesture>();

        gesture.SetDebugLogs(palmMenuDebugLogs);
    }
    
    public void ToggleMenu()
    {
        SetMenuVisibility(!isMenuVisible);
    }
    
    public void SetMenuVisibility(bool visible)
    {
        isMenuVisible = visible;

        if (uguiRoot != null)
            uguiRoot.SetActive(visible);
        if (uguiGrabHandle != null)
            uguiGrabHandle.SetActive(visible);

        if (root != null)
        {
            if (wristMenuMainPanel != null)
                wristMenuMainPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        }

        Collider collider = GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = visible;
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

        if (uguiOptionsPanel != null)
            uguiOptionsPanel.SetActive(true);

        if (wristMenuAddShapePanel != null)
        {
            wristMenuAddShapePanel.style.display = DisplayStyle.None;
            wristMenuAddShapePanel.SetEnabled(false);
        }

        if (uguiAddShapePanel != null)
            uguiAddShapePanel.SetActive(false);
        
        if (wristMenuEditShapePanel != null)
        {
            wristMenuEditShapePanel.style.display = DisplayStyle.None;
            wristMenuEditShapePanel.SetEnabled(false);
        }

        if (uguiEditShapePanel != null)
            uguiEditShapePanel.SetActive(false);
    }
    
    private void ShowAddShapePanel()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.None;
            wristMenuOptionsPanel.SetEnabled(false);
        }

        if (uguiOptionsPanel != null)
            uguiOptionsPanel.SetActive(false);

        if (wristMenuAddShapePanel != null)
        {
            wristMenuAddShapePanel.style.display = DisplayStyle.Flex;
            wristMenuAddShapePanel.SetEnabled(true);
        }

        if (uguiAddShapePanel != null)
            uguiAddShapePanel.SetActive(true);
    }

    private void ShowEditShapePanel()
    {
        if (wristMenuOptionsPanel != null)
        {
            wristMenuOptionsPanel.style.display = DisplayStyle.None;
            wristMenuOptionsPanel.SetEnabled(false);
        }

        if (uguiOptionsPanel != null)
            uguiOptionsPanel.SetActive(false);

        if (wristMenuEditShapePanel != null)
        {
            wristMenuEditShapePanel.style.display = DisplayStyle.Flex;
            wristMenuEditShapePanel.SetEnabled(true);
        }

        if (uguiEditShapePanel != null)
            uguiEditShapePanel.SetActive(true);
    }

    // Event Handlers
    private void OnAddShapeClicked()
    {
        ShowAddShapePanel();
        Debug.Log("WristMenuController: Add Shape panel opened");
    }

    private void PopulateEditShapePanel(GameObject selectedObject)
    {
        if (wristMenuEditSliderPanel == null && uguiEditSliderPanel == null)
        {
            Debug.LogWarning("WristMenuController: Edit slider panel not found.");
            return;
        }

        // Clear existing elements
        if (wristMenuEditSliderPanel != null)
            wristMenuEditSliderPanel.Clear();
        ClearUGUIEditSliderPanel();

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
            if (wristMenuEditSliderPanel != null)
            {
                wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "X"));
                wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "Y"));
                wristMenuEditSliderPanel.Add(CreateToggleStack("Cube", "Z"));
            }
            if (uguiEditSliderPanel != null)
                PopulateUGUIEditShapePanel(selectedObject, meshName);
        }
        else if (meshName.Contains("Sphere"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Sphere");
            if (wristMenuEditSliderPanel != null)
                wristMenuEditSliderPanel.Add(CreateToggleStack("Sphere", "Radius"));
            if (uguiEditSliderPanel != null)
                PopulateUGUIEditShapePanel(selectedObject, meshName);
        }
        else if (meshName.Contains("Cylinder"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Cylinder");
            if (wristMenuEditSliderPanel != null)
            {
                wristMenuEditSliderPanel.Add(CreateToggleStack("Cylinder", "Height"));
                wristMenuEditSliderPanel.Add(CreateToggleStack("Cylinder", "Radius"));
            }
            if (uguiEditSliderPanel != null)
                PopulateUGUIEditShapePanel(selectedObject, meshName);
        }
        else if (meshName.Contains("Mesh"))
        {
            Debug.Log("WristMenuController: Populating edit panel for Mesh");
            if (wristMenuEditSliderPanel != null)
                wristMenuEditSliderPanel.Add(CreateToggleStack("Mesh", "Scale"));
            if (uguiEditSliderPanel != null)
                PopulateUGUIEditShapePanel(selectedObject, meshName);
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
        if (wristMenuEditSliderPanel != null)
            wristMenuEditSliderPanel.Clear();
        ClearUGUIEditSliderPanel();
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
        // Keep the object where it's grabbed instead of snapping it to the controller
        gi.useDynamicAttach = true;
        // Don't match the ray hit point's position for the attach anchor — keep it at the
        // object's own pivot so joystick rotation spins the object about its own center
        // instead of orbiting around wherever the ray happened to hit its surface.
        gi.matchAttachPosition = false;
        // Don't apply release velocity, object should stop moving as soon as it's let go
        gi.throwOnDetach = false;

        // Controls grabbing based on SelectionManager selection state
        shape.AddComponent<SelectableGrabController>();

        shape.transform.localScale = Vector3.one * 0.5f;
        shape.tag = "Selectable";

        shape.AddComponent<XRGrabTransformerScaleAxisLock>();
        shape.AddComponent<XRGrabTransformerLockPose>();
        // Don't freeze rotation by default — joystick manipulation should be able to spin
        // the object about its own center. SnapSelectedToSurface() still re-syncs this via
        // SyncInitialRotation() in case freezePose is turned back on elsewhere.
        shape.GetComponent<XRGrabTransformerLockPose>().freezePose = false;

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
            if (uguiRecordButtonText != null)
            {
                uguiRecordButtonText.text = "Recording...";
                uguiRecordButtonText.color = Color.yellow;
            }
            if (recordStatusLabel != null)
                recordStatusLabel.text = "Waiting for grab...";
            if (uguiRecordStatusLabel != null)
                uguiRecordStatusLabel.text = "Waiting for grab...";
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
        if (uguiRecordStatusLabel != null)
            uguiRecordStatusLabel.text = $"Sent: {objectId}";
        ResetRecordUI(resetLabel: false);
    }

    private void ResetRecordUI(bool resetLabel = true)
    {
        if (recordPickPlaceButton != null)
        {
            recordPickPlaceButton.text = "Record Pick & Place";
            recordPickPlaceButton.style.color = new UnityEngine.UIElements.StyleColor(StyleKeyword.Null);
        }
        if (uguiRecordButtonText != null)
        {
            uguiRecordButtonText.text = "Record Pick & Place";
            uguiRecordButtonText.color = Color.white;
        }
        if (resetLabel && recordStatusLabel != null)
            recordStatusLabel.text = "Idle";
        if (resetLabel && uguiRecordStatusLabel != null)
            uguiRecordStatusLabel.text = "Idle";
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
