using UnityEngine;
using UnityEngine.UIElements;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Trajectory;
using RosMessageTypes.Sensor;
using System.Collections.Generic;
using System.Linq;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MoveItPlanningRequestMenuUI : MonoBehaviour
{
    // Synthetic object_id tying together set_start_state/set_goal_state/send_planning_request/
    // planning_request_result events in the ObjectEvent log -- there's no spawned GameObject
    // behind these, just logical actions on this menu, same convention as CertifyPathMenuController's
    // "path_certification".
    private const string PlanningRequestObjectId = "planning_request";

    [Header("Robot")]
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private string jointStateTopic = "/joint_states";
    [SerializeField] private string executeTrajectoryTopic = "/joint_trajectory_controller/joint_trajectory";
    // Successful plans are republished here for the ROS-side planned_path_logger; a service
    // response is only visible to this client, so nothing else could record the path.
    [SerializeField] private string plannedPathTopic = "/study/planned_paths";

    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    [Header("VisionOS Runtime Menu Layout")]
    [SerializeField] private Vector2 visionOSPlanningMenuSizeMeters = new Vector2(1.05f, 1.25f);
    [SerializeField] private Vector3 visionOSPlanningMenuOffsetMeters = new Vector3(0f, -0.22f, 0f);
    [SerializeField] private Vector3 visionOSGrabHandleSizeMeters = new Vector3(0.9f, 0.07f, 0.025f);
    [SerializeField] private Vector3 visionOSGrabHandleOffsetMeters = new Vector3(0f, 0.45f, -0.02f);
    [SerializeField] private Color visionOSGrabHandleColor = new Color(0.18f, 0.62f, 0.82f, 0.95f);
    
    [Header("MoveIt2 Configuration")]
    [SerializeField] private string planningGroupName = "ur_manipulator";
    [SerializeField] private string planningPipelineId = "ompl";
    [SerializeField] private string defaultPlannerId = "ur_manipulator";
    [SerializeField] private int defaultNumPlanningAttempts = 10;
    [SerializeField] private float defaultAllowedPlanningTime = 5.0f;
    [SerializeField] private double goalTolerance = 0.01;
    
    [Header("ROS 2 Topics")]
    [SerializeField] private string motionPlanServiceName = "/plan_kinematic_path";
    [SerializeField] private string displayTrajectoryTopic = "";

    [Header("Planner Query Service")]
    [SerializeField] private bool autoQueryPlanners = true;
    [SerializeField] private string plannerQueryServiceName = "/query_planner_interface";

    [Header("Ghost Robots")]
    [SerializeField] private SpawnGhosts ghostSpawner;
    [SerializeField] private TrajectoryReplay trajectoryReplayer;

    [Header("Joint Name Remapping")]
    [Tooltip("Prefix in the incoming ROS joint_states topic (e.g. 'panda_')")]
    [SerializeField] private string rosJointNamePrefix = "panda_";
    [Tooltip("Prefix used by the Unity robot prefab joints (e.g. 'fr3_')")]
    [SerializeField] private string unityJointNamePrefix = "fr3_";

    // Robot
    private DirectArticulationIKController robotController;
    
    // UI Elements
    private VisualElement root;
    private Button setStartStateButton;
    private Button setGoalStateButton;
    private DropdownField plannerPipelineDropdown;
    private DropdownField plannerDropdown;
    private IntegerField numPlanningAttemptsField;
    private FloatField allowedPlanningTimeField;
    private Label planningResultLabel;
    private Button planningRequestButton;
    private Button stopReplayButton;
    private Button executeTrajectoryButton;
    private Button mirrorButton;
    private bool isMirroring = false;
    private bool isReplaying = false;

    // Runtime uGUI fallback for PolySpatial/visionOS. World-space Canvas is rendered in
    // RealityKit immersion; UIDocument/UI Toolkit panels are not a reliable visible surface there.
    private Canvas uguiCanvas;
    private GameObject uguiRoot;
    private UnityEngine.UI.Text uguiPlanningResultLabel;
    private UnityEngine.UI.Text uguiSetStartStateButtonText;
    private UnityEngine.UI.Text uguiSetGoalStateButtonText;
    private UnityEngine.UI.Text uguiStopReplayButtonText;
    private UnityEngine.UI.Text uguiMirrorButtonText;
    private UnityEngine.UI.Button uguiStopReplayButton;
    private UnityEngine.UI.Button uguiExecuteTrajectoryButton;
    private UnityEngine.UI.Text uguiPipelineValueLabel;
    private UnityEngine.UI.Text uguiPlannerValueLabel;
    private UnityEngine.UI.Text uguiNumPlanningAttemptsLabel;
    private UnityEngine.UI.Text uguiAllowedPlanningTimeLabel;
    private readonly List<string> uguiPipelineChoices = new List<string>();
    private readonly List<string> uguiPlannerChoices = new List<string>();
    private string uguiSelectedPlanner = "";
    private int uguiNumPlanningAttempts;
    private float uguiAllowedPlanningTime;
    private readonly Queue<Action> pendingUIActions = new Queue<Action>();
    private bool useUGUIRuntimeMenu;

    // ROS Connection
    private ROSConnection ros;
    private bool isConnected = false;

    // Planning state
    private bool startSet = false;
    private bool goalSet = false;
    private RobotStateMsg currentStartState;
    private RobotStateMsg currentGoalState;
    private bool hasStartState = false;
    private bool hasGoalState = false;
    private JointTrajectoryMsg lastPlannedTrajectory;
    public JointTrajectoryMsg LastPlannedTrajectory => lastPlannedTrajectory != null ? BuildLocalTrajectory(lastPlannedTrajectory) : null;

    // Planner querying
    private bool isQueryingPlanners = false;
    private Dictionary<string, string[]> pipelineToPlanners = new Dictionary<string, string[]>();
    
    // Parsed results for planner dropdowns
    [Serializable]
    public class PlannerListing
    {
        public string pipelineId;
        public string[] plannerIds;
    }
    
    public List<PlannerListing> PlannerResults = new List<PlannerListing>();
    
    // Hardcoded joint names for the UR5e - ideally this would be dynamic
    public readonly string[] jointNames = new string[]
    { "shoulder_pan_joint", "shoulder_lift_joint", "elbow_joint", "wrist_1_joint", "wrist_2_joint", "wrist_3_joint" };

    private Dictionary<string, int> jointNameToIndex;

    public readonly Tuple<float, float>[] jointLimits = new Tuple<float, float>[]
    {
        new Tuple<float, float>(-351f, 351f),  // shoulder_pan_joint
        new Tuple<float, float>(-351f, 351f),  // shoulder_lift_joint
        new Tuple<float, float>(-171f, 171f),  // elbow_joint
        new Tuple<float, float>(-351f, 351f),  // wrist_1_joint
        new Tuple<float, float>(-351f, 351f),  // wrist_2_joint
        new Tuple<float, float>(-351f, 351f)   // wrist_3_joint
    };

    private void Awake()
    {
        if (ikController == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: ikController not assigned — drag the Robot IK Manager's DirectArticulationIKController here.");
            return;
        }

        jointNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < jointNames.Length; i++)
        {
            jointNameToIndex[jointNames[i]] = i;
        }
    }

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        useUGUIRuntimeMenu = ShouldUseUGUIRuntimeMenu() || uiDocument == null;

        if (useUGUIRuntimeMenu)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            EnsureUGUIPlanningMenu();
        }
        else
        {
            root = uiDocument?.rootVisualElement;
            if (root == null)
            {
                Debug.LogError("MoveItPlanningRequestMenuUI: No UIDocument/rootVisualElement found.");
                return;
            }

            InitializeUIElements();
            SetupEventHandlers();
        }

        robotController = ikController;

        InitializeROSConnection();

        // Start planner querying immediately
        StartPlannerQuerying();
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

    private void StartPlannerQuerying()
    { 
        // Register the service and start querying
        if (ros != null)
        {
            ros.RegisterRosService<GetMotionPlanRequest, GetMotionPlanResponse>(motionPlanServiceName);

            if (!autoQueryPlanners) return;
            ros.RegisterRosService<QueryPlannerInterfacesRequest, QueryPlannerInterfacesResponse>(plannerQueryServiceName);

            InvokeRepeating(nameof(TryQueryPlanners), 0.5f, 1.0f); // retry until it succeeds
        }
    }

    private void InitializeUIElements()
    {
        // Get UI elements by name
        planningResultLabel = root.Q<Label>("planningResultLabel");
        setStartStateButton = root.Q<Button>("planningRequestSetStartButton");
        setGoalStateButton = root.Q<Button>("planningRequestSetGoalStateButton");
        plannerPipelineDropdown = root.Q<DropdownField>("planningRequestPlannerPipelineIDDropDown");
        plannerDropdown = root.Q<DropdownField>("planningRequestPlannerIDDropDown");
        numPlanningAttemptsField = root.Q<IntegerField>("planningRequestNumPlanningAttemptsInput");
        allowedPlanningTimeField = root.Q<FloatField>("planningRequestAllowedPlanningTimeInput");
        planningRequestButton = root.Q<Button>("planningRequestSendButton");
        stopReplayButton = root.Q<Button>("planningRequestStopReplayButton");
        executeTrajectoryButton = root.Q<Button>("planningRequestExecuteTrajectoryButton");
        mirrorButton = root.Q<Button>("mirrorJointStateButton");

        // Validate UI elements
        if (setStartStateButton == null || setGoalStateButton == null ||
            plannerPipelineDropdown == null || plannerDropdown == null ||
            numPlanningAttemptsField == null || allowedPlanningTimeField == null ||
            planningRequestButton == null || stopReplayButton == null ||
            mirrorButton == null || executeTrajectoryButton == null ||
            planningResultLabel == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: One or more UI elements not found in UXML.");
            return;
        }
        
        // Initialize dropdowns with empty lists - will be populated by ROS
        plannerPipelineDropdown.choices = new List<string>();
        plannerPipelineDropdown.value = "";
        plannerDropdown.choices = new List<string>();
        plannerDropdown.value = "";
        
        // Set default values
        numPlanningAttemptsField.value = defaultNumPlanningAttempts;
        allowedPlanningTimeField.value = defaultAllowedPlanningTime;

        stopReplayButton.SetEnabled(false);
        executeTrajectoryButton.SetEnabled(false);

        // Update button states
        UpdateButtonStates();
    }

    private void SetupEventHandlers()
    {
        setStartStateButton.clicked += OnSetStartStateClicked;
        setGoalStateButton.clicked += OnSetGoalStateClicked;

        // Setup dropdown event handlers
        plannerPipelineDropdown.RegisterValueChangedCallback(OnPipelineSelectionChanged);

        // Add planning request button
        planningRequestButton.clicked += SendPlanningRequest;

        stopReplayButton.clicked += () =>
        {
            if (isReplaying)
            {
                StopPreview();
            }
            else if (!isReplaying)
            {
                PreviewTrajectory(lastPlannedTrajectory);

            }
        };

        mirrorButton.clicked += ToggleMirroring;
        executeTrajectoryButton.clicked += ExectuteTrajectory;
    }

    private void EnsureUGUIPlanningMenu()
    {
        if (uguiRoot != null)
            return;

        uguiCanvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS Planning Request Canvas",
            new Vector2(1050f, 1250f),
            visionOSPlanningMenuSizeMeters,
            visionOSPlanningMenuOffsetMeters,
            sortingOrder: 140);
        EnsureVisionOSGrabHandle();

        if (uguiCanvas.worldCamera == null && Camera.main != null)
            uguiCanvas.worldCamera = Camera.main;

        uguiRoot = uguiCanvas.gameObject;
        VisionOSSampleControlsUI.ClearChildren(uguiRoot.transform);

        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            uguiRoot.transform,
            "Planning Request Panel",
            new Vector2(1050f, 1250f));

        CreateUGUIText(panel.transform, "Planning Request", 50, TextAnchor.MiddleCenter);

        var startButton = CreateUGUIButton(panel.transform, "Set Start State", OnSetStartStateClicked);
        uguiSetStartStateButtonText = startButton.GetComponentInChildren<UnityEngine.UI.Text>();

        var goalButton = CreateUGUIButton(panel.transform, "Set Goal State", OnSetGoalStateClicked);
        uguiSetGoalStateButtonText = goalButton.GetComponentInChildren<UnityEngine.UI.Text>();

        var mirrorButtonUGUI = CreateUGUIButton(panel.transform, "Mirror Joint States", ToggleMirroring);
        uguiMirrorButtonText = mirrorButtonUGUI.GetComponentInChildren<UnityEngine.UI.Text>();

        uguiNumPlanningAttempts = defaultNumPlanningAttempts;
        uguiAllowedPlanningTime = defaultAllowedPlanningTime;
        CreateUGUICycleRow(panel.transform, "Pipeline", () => CyclePipeline(-1), () => CyclePipeline(1), out uguiPipelineValueLabel);
        CreateUGUICycleRow(panel.transform, "Planner", () => CyclePlanner(-1), () => CyclePlanner(1), out uguiPlannerValueLabel);
        CreateUGUIStepperRow(panel.transform, "Attempts", () => StepPlanningAttempts(-1), () => StepPlanningAttempts(1), out uguiNumPlanningAttemptsLabel);
        CreateUGUIStepperRow(panel.transform, "Allowed Time", () => StepAllowedPlanningTime(-0.5f), () => StepAllowedPlanningTime(0.5f), out uguiAllowedPlanningTimeLabel);
        UpdateUGUIPlanningOptionLabels();

        CreateUGUIButton(panel.transform, "Send Planning Request", SendPlanningRequest);

        uguiStopReplayButton = CreateUGUIButton(panel.transform, "Start Replay", () =>
        {
            if (isReplaying)
                StopPreview();
            else
                PreviewTrajectory(lastPlannedTrajectory);
        });
        uguiStopReplayButtonText = uguiStopReplayButton.GetComponentInChildren<UnityEngine.UI.Text>();

        uguiExecuteTrajectoryButton = CreateUGUIButton(panel.transform, "Execute Trajectory", ExectuteTrajectory);

        uguiPlanningResultLabel = CreateUGUIText(panel.transform, "", 35, TextAnchor.MiddleCenter);
        SetButtonInteractable(uguiStopReplayButton, false);
        SetButtonInteractable(uguiExecuteTrajectoryButton, false);

        UpdateButtonStates();
        Debug.Log("MoveItPlanningRequestMenuUI: Created visionOS uGUI planning request menu.");
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
        float parentUniformScale = GetParentUniformScale();
        return meters / parentUniformScale;
    }

    private float MetersToLocalScale(float meters)
    {
        return meters / GetParentUniformScale();
    }

    private float GetParentUniformScale()
    {
        return VisionOSSampleControlsUI.GetUniformScale(transform.lossyScale);
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

    private UnityEngine.UI.Button CreateUGUIButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        return VisionOSSampleControlsUI.CreateButton(parent, label, onClick, 950f, 100f, 35);
    }

    private void CreateUGUICycleRow(
        Transform parent,
        string label,
        UnityEngine.Events.UnityAction previous,
        UnityEngine.Events.UnityAction next,
        out UnityEngine.UI.Text valueText)
    {
        var row = CreateUGUIRow(parent, label);
        VisionOSSampleControlsUI.CreateButton(row.transform, "<", previous, 90f, 80f, 35);
        valueText = CreateUGUIText(row.transform, "", 31, TextAnchor.MiddleCenter);
        var valueLayout = valueText.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
        valueLayout.preferredWidth = 360f;
        valueLayout.minHeight = 80f;
        VisionOSSampleControlsUI.CreateButton(row.transform, ">", next, 90f, 80f, 35);
    }

    private void CreateUGUIStepperRow(
        Transform parent,
        string label,
        UnityEngine.Events.UnityAction decrease,
        UnityEngine.Events.UnityAction increase,
        out UnityEngine.UI.Text valueText)
    {
        var row = CreateUGUIRow(parent, label);
        VisionOSSampleControlsUI.CreateButton(row.transform, "-", decrease, 90f, 80f, 35);
        valueText = CreateUGUIText(row.transform, "", 31, TextAnchor.MiddleCenter);
        var valueLayout = valueText.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
        valueLayout.preferredWidth = 360f;
        valueLayout.minHeight = 80f;
        VisionOSSampleControlsUI.CreateButton(row.transform, "+", increase, 90f, 80f, 35);
    }

    private GameObject CreateUGUIRow(Transform parent, string label)
    {
        var row = new GameObject(label + " Row");
        row.transform.SetParent(parent, false);
        var rect = row.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(950f, 100f);

        var layout = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandWidth = false;
        layout.spacing = 12f;

        var labelText = CreateUGUIText(row.transform, label, 35, TextAnchor.MiddleLeft);
        var labelLayout = labelText.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
        labelLayout.preferredWidth = 300f;
        labelLayout.minHeight = 100f;

        return row;
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

    private void SetPipelineChoices(List<string> choices, string selected)
    {
        if (plannerPipelineDropdown != null)
        {
            plannerPipelineDropdown.choices = choices;
            plannerPipelineDropdown.SetValueWithoutNotify(selected);
        }

        uguiPipelineChoices.Clear();
        uguiPipelineChoices.AddRange(choices);
        if (!string.IsNullOrEmpty(selected))
            planningPipelineId = selected;
        UpdateUGUIPlanningOptionLabels();
    }

    private void SetPlannerChoices(List<string> choices, string selected)
    {
        if (plannerDropdown != null)
        {
            plannerDropdown.choices = choices;
            plannerDropdown.SetValueWithoutNotify(selected);
        }

        uguiPlannerChoices.Clear();
        uguiPlannerChoices.AddRange(choices);
        uguiSelectedPlanner = selected ?? "";
        UpdateUGUIPlanningOptionLabels();
    }

    private void CyclePipeline(int direction)
    {
        if (uguiPipelineChoices.Count == 0)
            return;

        int index = uguiPipelineChoices.IndexOf(planningPipelineId);
        if (index < 0)
            index = 0;
        index = (index + direction + uguiPipelineChoices.Count) % uguiPipelineChoices.Count;
        HandlePipelineSelection(uguiPipelineChoices[index]);
    }

    private void CyclePlanner(int direction)
    {
        if (uguiPlannerChoices.Count == 0)
            return;

        int index = uguiPlannerChoices.IndexOf(uguiSelectedPlanner);
        if (index < 0)
            index = 0;
        index = (index + direction + uguiPlannerChoices.Count) % uguiPlannerChoices.Count;
        uguiSelectedPlanner = uguiPlannerChoices[index];
        UpdateUGUIPlanningOptionLabels();
    }

    private void StepPlanningAttempts(int delta)
    {
        uguiNumPlanningAttempts = Mathf.Clamp(uguiNumPlanningAttempts + delta, 1, 100);
        UpdateUGUIPlanningOptionLabels();
    }

    private void StepAllowedPlanningTime(float delta)
    {
        uguiAllowedPlanningTime = Mathf.Clamp(uguiAllowedPlanningTime + delta, 0.1f, 120f);
        UpdateUGUIPlanningOptionLabels();
    }

    private void UpdateUGUIPlanningOptionLabels()
    {
        if (uguiPipelineValueLabel != null)
            uguiPipelineValueLabel.text = string.IsNullOrEmpty(planningPipelineId) ? "No pipeline" : planningPipelineId;
        if (uguiPlannerValueLabel != null)
            uguiPlannerValueLabel.text = string.IsNullOrEmpty(uguiSelectedPlanner) ? "No planner" : uguiSelectedPlanner;
        if (uguiNumPlanningAttemptsLabel != null)
            uguiNumPlanningAttemptsLabel.text = uguiNumPlanningAttempts.ToString();
        if (uguiAllowedPlanningTimeLabel != null)
            uguiAllowedPlanningTimeLabel.text = $"{uguiAllowedPlanningTime:0.0}s";
    }


    private string GetSelectedPlanner()
    {
        if (plannerDropdown != null)
            return plannerDropdown.value;

        return uguiSelectedPlanner;
    }

    private int GetNumPlanningAttempts()
    {
        if (numPlanningAttemptsField != null)
            return numPlanningAttemptsField.value;

        if (uguiNumPlanningAttemptsLabel != null)
            return Mathf.Max(1, uguiNumPlanningAttempts);

        return defaultNumPlanningAttempts;
    }

    private float GetAllowedPlanningTime()
    {
        if (allowedPlanningTimeField != null)
            return allowedPlanningTimeField.value;

        if (uguiAllowedPlanningTimeLabel != null)
            return Mathf.Max(0.1f, uguiAllowedPlanningTime);

        return defaultAllowedPlanningTime;
    }

    private void SetPlanningResultText(string text)
    {
        if (planningResultLabel != null)
            planningResultLabel.text = text;
        if (uguiPlanningResultLabel != null)
            uguiPlanningResultLabel.text = text;
    }

    private void SetButtonInteractable(UnityEngine.UI.Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    private void SetStopReplayEnabled(bool enabled)
    {
        if (stopReplayButton != null)
            stopReplayButton.SetEnabled(enabled);
        SetButtonInteractable(uguiStopReplayButton, enabled);
    }

    private void SetExecuteTrajectoryEnabled(bool enabled)
    {
        if (executeTrajectoryButton != null)
            executeTrajectoryButton.SetEnabled(enabled);
        SetButtonInteractable(uguiExecuteTrajectoryButton, enabled);
    }

    private void SetStopReplayText(string text)
    {
        if (stopReplayButton != null)
            stopReplayButton.text = text;
        if (uguiStopReplayButtonText != null)
            uguiStopReplayButtonText.text = text;
    }

    private void OnPipelineSelectionChanged(ChangeEvent<string> evt)
    {
        HandlePipelineSelection(evt.newValue);
    }

    private void HandlePipelineSelection(string selectedPipeline)
    {
        planningPipelineId = selectedPipeline;

        if (pipelineToPlanners.TryGetValue(selectedPipeline, out var planners))
        {
            var pick = planners.Contains(defaultPlannerId) ? defaultPlannerId :
                       planners.Length > 0 ? planners[0] : "";
            SetPlannerChoices(planners.ToList(), pick);
        }
        else
        {
            SetPlannerChoices(new List<string>(), "");
        }
    }

    private void TryQueryPlanners()
    {
        if (isQueryingPlanners) return;
        if (ros == null || !ros.HasConnectionThread)
        {
            Debug.LogWarning("[MoveIt] Waiting for ROS-TCP connection...");
            return;
        }

        isQueryingPlanners = true;
        var req = new QueryPlannerInterfacesRequest();
        try
        {
            ros.SendServiceMessage<QueryPlannerInterfacesResponse>(
                plannerQueryServiceName, req,
                OnPlannerQueryResponse
            );
        }
        catch (Exception e)
        {
            isQueryingPlanners = false;
            Debug.LogError($"[MoveIt] Service call failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // Call this whenever you update UI from a callback/thread.
    private void Update()
    {
        while (true)
        {
            Action action = null;
            lock (pendingUIActions)
            {
                if (pendingUIActions.Count > 0)
                    action = pendingUIActions.Dequeue();
            }

            if (action == null)
                break;

            action();
        }
    }

    void UI(Action a)
    {
        // Ensure we run on the UI panel's schedule (main thread, next frame)
        if (root != null)
        {
            root.schedule.Execute(() => a()).ExecuteLater(0);
        }
        else
        {
            lock (pendingUIActions)
                pendingUIActions.Enqueue(a);
        }
    }

    private void OnPlannerQueryResponse(QueryPlannerInterfacesResponse resp)
    {
        isQueryingPlanners = false;
        CancelInvoke(nameof(TryQueryPlanners));

        if (resp == null || resp.planner_interfaces == null)
        {
            Debug.LogWarning("[MoveIt] Empty response from /query_planner_interface");
            return;
        }

        PlannerResults.Clear();
        pipelineToPlanners.Clear();

        foreach (var desc in resp.planner_interfaces)
        {
            var pipeline = desc.pipeline_id ?? string.Empty;
            var planners = desc.planner_ids ?? Array.Empty<string>();

            PlannerResults.Add(new PlannerListing
            {
                pipelineId = pipeline,
                plannerIds = planners
            });

            pipelineToPlanners[pipeline] = planners;
        }

        UI(() =>
        {
            // 1) Update pipeline dropdown
            var discoveredPipelines = pipelineToPlanners.Keys.ToList();

            // Pick a valid pipeline
            string chosenPipeline = planningPipelineId;
            if (!discoveredPipelines.Contains(chosenPipeline))
                chosenPipeline = discoveredPipelines.Count > 0 ? discoveredPipelines[0] : "";

            // IMPORTANT: Set without notify, then manually update planners
            SetPipelineChoices(discoveredPipelines, chosenPipeline);
            planningPipelineId = chosenPipeline;

            // 2) Update planner dropdown for the selected pipeline
            if (!string.IsNullOrEmpty(chosenPipeline) && pipelineToPlanners.TryGetValue(chosenPipeline, out var planners))
            {
                // If your default isn't in the list, pick the first one
                var chosenPlanner = planners.Contains(defaultPlannerId) ? defaultPlannerId :
                                    planners.Length > 0 ? planners[0] : "";
                SetPlannerChoices(planners.ToList(), chosenPlanner);
            }
            else
            {
                SetPlannerChoices(new List<string>(), "");
            }
        });
    }

    private void InitializeROSConnection()
    {
        isConnected = false;

        ros = ROSConnection.GetOrCreateInstance();

        if (ros == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: Failed to create ROS connection.");
            return;
        }

        isConnected = true;

        ros.RegisterPublisher<JointTrajectoryMsg>(executeTrajectoryTopic);
        ros.RegisterPublisher<JointTrajectoryMsg>(plannedPathTopic);

        ros.Subscribe<JointStateMsg>(jointStateTopic, MirrorJointStates);

        if (!string.IsNullOrEmpty(displayTrajectoryTopic))
        {
            ros.Subscribe<DisplayTrajectoryMsg>(displayTrajectoryTopic, DisplayTrajectory);
        }
    }

    private void DisplayTrajectory(DisplayTrajectoryMsg trajectory)
    {
        if (isReplaying && trajectoryReplayer.HasFinishedOneLoop())
            StopPreview();
        else if (isReplaying)
            return;
        
        lastPlannedTrajectory = trajectory.trajectory.Length > 0 ? trajectory.trajectory[0].joint_trajectory : null;
        if (lastPlannedTrajectory != null)
        {
            PreviewTrajectory(lastPlannedTrajectory);
        }
    }

    private void MirrorJointStates(JointStateMsg jointState)
    {
        if (!isMirroring) return;

        if (robotController == null)
        {
            Debug.Log("MoveItPlanningRequestMenuUI: DirectArticulationIKController not assigned.");
            return;
        }

        var sb = new System.Text.StringBuilder("MoveItPlanningRequestMenuUI: Mirror joints —");
        for (int i = 0; i < jointState.name.Length; i++)
            sb.Append($"\n  {jointState.name[i]}: {(i < jointState.position.Length ? jointState.position[i] : double.NaN):F4} rad");
        Debug.Log(sb.ToString());

        robotController.ApplyJointState(RemapJointNames(jointState.name), jointState.position);

        string[] unityNames = robotController.GetJointStateNames();
        float[] unityPositions = robotController.GetJointStatePositions();
        var sb2 = new System.Text.StringBuilder("MoveItPlanningRequestMenuUI: Unity joint state after apply —");
        for (int i = 0; i < unityNames.Length; i++)
            sb2.Append($"\n  {unityNames[i]}: {(i < unityPositions.Length ? unityPositions[i] : float.NaN):F4} rad");
        Debug.Log(sb2.ToString());
    }

    private string[] RemapJointNames(string[] rosNames)
    {
        if (string.IsNullOrEmpty(rosJointNamePrefix) || rosJointNamePrefix == unityJointNamePrefix)
            return rosNames;

        string[] remapped = new string[rosNames.Length];
        for (int i = 0; i < rosNames.Length; i++)
        {
            remapped[i] = rosNames[i].StartsWith(rosJointNamePrefix)
                ? unityJointNamePrefix + rosNames[i].Substring(rosJointNamePrefix.Length)
                : rosNames[i];
        }
        return remapped;
    }

    private string[] RemapJointNamesToRos(string[] unityNames)
    {
        if (string.IsNullOrEmpty(unityJointNamePrefix) || rosJointNamePrefix == unityJointNamePrefix)
            return unityNames;

        string[] remapped = new string[unityNames.Length];
        for (int i = 0; i < unityNames.Length; i++)
        {
            remapped[i] = unityNames[i].StartsWith(unityJointNamePrefix)
                ? rosJointNamePrefix + unityNames[i].Substring(unityJointNamePrefix.Length)
                : unityNames[i];
        }
        return remapped;
    }

    private void ToggleMirroring()
    {
        isMirroring = !isMirroring;
        string text = isMirroring ? "Stop Mirroring" : "Mirror Joint States";
        if (mirrorButton != null)
            mirrorButton.text = text;
        if (uguiMirrorButtonText != null)
            uguiMirrorButtonText.text = text;
        Debug.Log($"MoveItPlanningRequestMenuUI: Mirroring {(isMirroring ? "enabled" : "disabled")}.");
        if (isMirroring && robotController != null)
            robotController.LogJointDriveLimits();
    }

    private void OnSetStartStateClicked()
    {
        ObjectMetricsLogger.Instance?.LogEvent("set_start_state", PlanningRequestObjectId);

        if (!startSet)
        {
            ghostSpawner.SpawnStartGhost();
            startSet = true;
        }
        else
        {
            ghostSpawner.UpdateStartGhost();
        }

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }

        // Get current robot state (this would typically come from the robot or simulation)
        currentStartState = GetCurrentRobotState();
        hasStartState = true;

        UpdateButtonStates();
    }

    private void OnSetGoalStateClicked()
    {
        ObjectMetricsLogger.Instance?.LogEvent("set_goal_state", PlanningRequestObjectId);

        if (!goalSet)
        {
            ghostSpawner.SpawnGoalGhost();
            goalSet = true;
        }
        else
        {
            ghostSpawner.UpdateGoalGhost();
        }

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS connection not available.");
            return;
        }
        
        // Get goal state (this could be from user interaction, predefined poses, etc.)
        currentGoalState = GetGoalRobotState();
        hasGoalState = true;
        
        UpdateButtonStates();
    }

    public void SendPlanningRequest()
    {
        ObjectMetricsLogger.Instance?.LogEvent("send_planning_request", PlanningRequestObjectId);

        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS 2 connection not available.");
            return;
        }
        
        if (!hasStartState || !hasGoalState)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: Both start and goal states must be set before planning.");
            return;
        }
        
        // Get the selected planner from the dropdown
        string selectedPlanner = GetSelectedPlanner();
        if (autoQueryPlanners && string.IsNullOrEmpty(selectedPlanner))
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: No planner selected.");
            return;
        }

        // Disable replay button until we have a new trajectory
        StopPreview();
        if (trajectoryReplayer != null)
            trajectoryReplayer.StopReplay();
        SetStopReplayEnabled(false);
        SetExecuteTrajectoryEnabled(false);
        
        var planningRequest = CreateMotionPlanRequest();
        GetMotionPlanRequest motionPlanRequest = new GetMotionPlanRequest(planningRequest);
        ros.SendServiceMessage<GetMotionPlanResponse>(
            motionPlanServiceName, motionPlanRequest,
            OnMotionPlanResponse
        );

        Debug.Log($"MoveItPlanningRequestMenuUI: ROS 2 planning request sent with planner: {selectedPlanner}");
    }

    private MotionPlanRequestMsg CreateMotionPlanRequest()
    {
        var request = new MotionPlanRequestMsg
        {
            workspace_parameters = new WorkspaceParametersMsg(),
            start_state = currentStartState,
            goal_constraints = CreateGoalConstraints(),
            path_constraints = new ConstraintsMsg(),
            trajectory_constraints = new TrajectoryConstraintsMsg(),
            reference_trajectories = new GenericTrajectoryMsg[0],
            pipeline_id = planningPipelineId,
            planner_id = GetSelectedPlanner(),
            group_name = planningGroupName,
            num_planning_attempts = GetNumPlanningAttempts(),
            allowed_planning_time = GetAllowedPlanningTime(),
            max_velocity_scaling_factor = 1.0,
            max_acceleration_scaling_factor = 1.0,
            cartesian_speed_limited_link = "",
            max_cartesian_speed = 0.0
        };
        
        return request;
    }

    private ConstraintsMsg[] CreateGoalConstraints()
    {
        // Create goal constraints based on the goal state
        // This is a simplified version - you might want to create more specific constraints
        var constraints = new ConstraintsMsg
        {
            name = "goal_constraints",
            joint_constraints = CreateJointConstraints(currentGoalState),
            position_constraints = new PositionConstraintMsg[0],
            orientation_constraints = new OrientationConstraintMsg[0],
            visibility_constraints = new VisibilityConstraintMsg[0]
        };
        
        return new ConstraintsMsg[] { constraints };
    }

    private JointConstraintMsg[] CreateJointConstraints(RobotStateMsg robotState)
    {
        if (robotState?.joint_state?.name == null || robotState.joint_state.position == null)
            return new JointConstraintMsg[0];
        
        var constraints = new List<JointConstraintMsg>();
        
        for (int i = 0; i < robotState.joint_state.name.Length; i++)
        {
            if (i < robotState.joint_state.position.Length)
            {
                var constraint = new JointConstraintMsg
                {
                    joint_name = robotState.joint_state.name[i],
                    position = robotState.joint_state.position[i],
                    tolerance_above = goalTolerance,
                    tolerance_below = goalTolerance,
                    weight = 1.0
                };
                constraints.Add(constraint);
            }
        }
        
        return constraints.ToArray();
    }

    private RobotStateMsg GetRobotStateMsgFromController()
    {
        if (robotController == null) return new RobotStateMsg();

        string[] names = RemapJointNamesToRos(robotController.GetJointStateNames());
        float[] positionsF = robotController.GetJointStatePositions();
        double[] positions = Array.ConvertAll(positionsF, p => (double)p);
        double[] zeros = new double[names.Length];

        var sb = new System.Text.StringBuilder("[MoveIt] Robot state being sent:\n");
        for (int i = 0; i < names.Length; i++)
            sb.AppendLine($"  {names[i]}: {positions[i]:F4} rad");
        Debug.Log(sb.ToString());

        return new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    frame_id = "base_link",
                    stamp = RosMessageCompatibility.CreateTime(Time.time)
                },
                name = names,
                position = positions,
                velocity = zeros,
                effort = zeros
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
    }

    private RobotStateMsg GetCurrentRobotState() => GetRobotStateMsgFromController();

    private RobotStateMsg GetGoalRobotState() => GetRobotStateMsgFromController();

    // Populates start/goal state and ghosts from a pre-baked trajectory's first/last waypoint,
    // instead of the live robot pose — used when a scene auto-plays a trajectory at Start().
    public void SetStartAndGoalFromTrajectory(TrajectoryData trajectoryData)
    {
        if (trajectoryData == null || trajectoryData.waypoints == null || trajectoryData.waypoints.Length == 0)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: TrajectoryData has no waypoints.");
            return;
        }

        if (ghostSpawner == null || robotController == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ghostSpawner or robotController not assigned.");
            return;
        }

        string[] names = trajectoryData.jointNames;
        TrajectoryData.Waypoint first = trajectoryData.waypoints[0];
        TrajectoryData.Waypoint last = trajectoryData.waypoints[trajectoryData.waypoints.Length - 1];

        if (!startSet)
        {
            ghostSpawner.SpawnStartGhostFromPose(robotController, names, first.positions);
            startSet = true;
        }
        else
        {
            ghostSpawner.UpdateStartGhostFromPose(robotController, names, first.positions);
        }
        currentStartState = BuildRobotStateMsgFromNamedPositions(names, first.positions);
        hasStartState = true;

        if (!goalSet)
        {
            ghostSpawner.SpawnGoalGhostFromPose(robotController, names, last.positions);
            goalSet = true;
        }
        else
        {
            ghostSpawner.UpdateGoalGhostFromPose(robotController, names, last.positions);
        }
        currentGoalState = BuildRobotStateMsgFromNamedPositions(names, last.positions);
        hasGoalState = true;

        UpdateButtonStates();
    }

    private RobotStateMsg BuildRobotStateMsgFromNamedPositions(string[] unityNames, double[] positions)
    {
        string[] rosNames = RemapJointNamesToRos(unityNames);
        double[] zeros = new double[rosNames.Length];

        return new RobotStateMsg
        {
            joint_state = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    frame_id = "base_link",
                    stamp = RosMessageCompatibility.CreateTime(Time.time)
                },
                name = rosNames,
                position = positions,
                velocity = zeros,
                effort = zeros
            },
            multi_dof_joint_state = new MultiDOFJointStateMsg()
        };
    }

    private void OnMotionPlanResponse(GetMotionPlanResponse response)
    {
        var motionPlanResponse = response.motion_plan_response;
        if (motionPlanResponse.error_code.val == 1) // SUCCESS
        {
            Debug.Log($"MoveItPlanningRequestMenuUI: Planning successful! Planning time: {motionPlanResponse.planning_time}s");
            ObjectMetricsLogger.Instance?.LogEvent("planning_request_result", PlanningRequestObjectId, details: "success");

            // Handle the planned trajectory
            if (motionPlanResponse.trajectory?.joint_trajectory != null)
            {
                lastPlannedTrajectory = motionPlanResponse.trajectory.joint_trajectory;
                Debug.Log($"MoveItPlanningRequestMenuUI: Trajectory has {lastPlannedTrajectory.points.Length} waypoints");

                // Raw trajectory (original ROS joint names), not the remapped local copy.
                if (lastPlannedTrajectory.points.Length > 0)
                {
                    ros.Publish(plannedPathTopic, lastPlannedTrajectory);
                }

                SetPlanningResultText($"Planning successful! Time: {motionPlanResponse.planning_time}s, Waypoints: {lastPlannedTrajectory.points.Length}");

                // You can execute the trajectory here or store it for later execution
                PreviewTrajectory(lastPlannedTrajectory);
                SetExecuteTrajectoryEnabled(true);
            }
        }
        else
        {
            SetPlanningResultText($"Planning failed with error: {motionPlanResponse.error_code.val} {motionPlanResponse.error_code.message}");
            lastPlannedTrajectory = null;
            SetStopReplayEnabled(false);
            SetExecuteTrajectoryEnabled(false);

            Debug.LogError($"MoveItPlanningRequestMenuUI: Planning failed with error code: {motionPlanResponse.error_code.val} - {motionPlanResponse.error_code.message}");
            ObjectMetricsLogger.Instance?.LogEvent("planning_request_result", PlanningRequestObjectId,
                details: $"failure:{motionPlanResponse.error_code.val}:{motionPlanResponse.error_code.message}");
        }
    }

    private void PreviewTrajectory(JointTrajectoryMsg trajectory)
    {
        if (trajectoryReplayer != null)
        {
            SetStopReplayEnabled(true);
            isReplaying = true;
            SetStopReplayText("Stop Replay");
            // Remap joint names from ROS convention (panda_) to Unity convention (fr3_)
            // so ApplyJointState can find joints in the dictionary.
            trajectoryReplayer.StartReplay(BuildLocalTrajectory(trajectory));
        }
        else
        {
            Debug.Log("MoveItPlanningRequestMenuUI: No TrajectoryReplay component assigned.");
        }
    }

    private JointTrajectoryMsg BuildLocalTrajectory(JointTrajectoryMsg traj)
    {
        return new JointTrajectoryMsg
        {
            header = traj.header,
            joint_names = RemapJointNames(traj.joint_names),
            points = traj.points
        };
    }
    
    private void StopPreview()
    {
        if (trajectoryReplayer != null && isReplaying)
        {
            trajectoryReplayer.StopReplay();
            isReplaying = false;
            SetStopReplayText("Start Replay");
        }
    }

    private void ExectuteTrajectory()
    {
        if (!isConnected)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: ROS 2 connection not available.");
            return;
        }

        if (lastPlannedTrajectory == null)
        {
            Debug.LogWarning("MoveItPlanningRequestMenuUI: No planned trajectory to execute.");
            return;
        }

        // Stop any ongoing preview and start mirroring if not already
        StopPreview();
        if (!isMirroring)
            ToggleMirroring();

        ros.Publish(executeTrajectoryTopic, lastPlannedTrajectory);
        Debug.Log("MoveItPlanningRequestMenuUI: Published trajectory for execution.");
    }

    private void UpdateButtonStates()
    {
        // Update button visual states based on current planning state
        string startText = hasStartState ? "Start State ✓" : "Set Start State";
        string goalText = hasGoalState ? "Goal State ✓" : "Set Goal State";
        if (setStartStateButton != null)
            setStartStateButton.text = startText;
        if (setGoalStateButton != null)
            setGoalStateButton.text = goalText;
        if (uguiSetStartStateButtonText != null)
            uguiSetStartStateButtonText.text = startText;
        if (uguiSetGoalStateButtonText != null)
            uguiSetGoalStateButtonText.text = goalText;

        // You could also change button colors or enable/disable them
        if (setStartStateButton != null)
            setStartStateButton.SetEnabled(true);
        if (setGoalStateButton != null)
            setGoalStateButton.SetEnabled(true);
    }

    public void ResetPlanningState()
    {
        hasStartState = false;
        hasGoalState = false;
        currentStartState = null;
        currentGoalState = null;
        UpdateButtonStates();
    }

    public void SetPlanningGroup(string groupName)
    {
        planningGroupName = groupName;
    }

    public void SetPlanningPipeline(string pipelineId)
    {
        planningPipelineId = pipelineId;
        UpdateUGUIPlanningOptionLabels();
    }

    private void OnDisable()
    {
    }

    private void OnDestroy()
    {    
        // Cancel any pending invokes
        CancelInvoke(nameof(TryQueryPlanners));
    }
}
