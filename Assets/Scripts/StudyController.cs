using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using RosMessageTypes.StudyInterfaces;

public class StudyController : MonoBehaviour
{
    public static StudyController Instance { get; private set; }

    // Set from the received /study/plan message; "unknown" when running standalone with a
    // locally-generated fallback plan (no ROS participant identity to attach to it).
    public string ParticipantId { get; private set; } = "unknown";

    public int TaskIndex => _taskIndex;
    public int SceneIndexInTask => _sceneIndexInTask;

    [System.Serializable]
    public class TaskConfig
    {
        public string interludeSceneName;
        public List<string> scenePool = new List<string>();
        public int numScenesToPlay;
    }

    [Header("Input")]
    [SerializeField] private InputActionReference _advanceAction;

    [Header("Advance Confirmation")]
    [Tooltip("XRI world-space Grab UI prefab instantiated as the Confirm/Cancel dialog shown before any advancement.")]
    [SerializeField] private GameObject _confirmDialogTemplate;
    [Tooltip("UXML rendered inside the confirmation dialog (must contain 'advanceConfirmButton' and 'advanceCancelButton').")]
    [SerializeField] private VisualTreeAsset _confirmDialogUxml;
    [Tooltip("World-space panel settings for the confirmation dialog.")]
    [SerializeField] private PanelSettings _confirmDialogPanelSettings;

    private GameObject _activeConfirmDialog;

    // Panel canvas size in UI Toolkit pixels. At WristUISettings' 100 px/unit and the Grab UI
    // template's 0.2 root scale, this comes out to a 0.68 m x 0.36 m panel in the world.
    private const float kConfirmPanelWidthPx = 340f;
    private const float kConfirmPanelHeightPx = 180f;
    // Taller variant for the "Cannot Advance Yet" notice, whose two-bullet messages need room.
    private const float kBlockedPanelHeightPx = 220f;
    // How far in front of (and below) the participant's eyes the dialog spawns. If scene
    // geometry (workbench, counter, robot) is closer than the preferred distance, the dialog
    // is pulled in front of it, but never nearer than the minimum.
    private const float kConfirmDialogDistance = 0.9f;
    private const float kConfirmDialogMinDistance = 0.4f;
    private const float kConfirmDialogClearance = 0.15f;
    private const float kConfirmDialogDropBelowEyes = 0.1f;

    [Header("Start Scene")]
    [Tooltip("Optional pause before auto-advancing from StartScene into the tutorial (or the first task's interlude, if no tutorial scene is set).")]
    [SerializeField] private float _startSceneAutoAdvanceDelay = 0f;

    [Header("Tutorial")]
    [Tooltip("Scene shown once, before the first task's interlude, to walk participants through the controls. Leave blank to skip straight into the study.")]
    [SerializeField] private string _tutorialSceneName = "Tutorial";

    [Header("Survey")]
    [Tooltip("Scene shown after the last scene of every task, stepping the participant through the post-task survey questions. Leave blank to skip straight to the next task's interlude.")]
    [SerializeField] private string _surveySceneName = "Survey";

    [Header("ROS Study Plan")]
    [Tooltip("How long to wait for a /study/plan message before falling back to local random generation (e.g. when running standalone without ROS).")]
    [SerializeField] private float _rosPlanTimeoutSeconds = 3f;

    [Header("Task Configuration")]
    [SerializeField] private TaskConfig _task1 = new TaskConfig();
    [SerializeField] private TaskConfig _task2 = new TaskConfig();
    [SerializeField] private TaskConfig _task3 = new TaskConfig();
    [SerializeField] private TaskConfig _task4 = new TaskConfig();

    // Runtime state. We track progress with explicit indices rather than
    // inferring it from the active scene name, since these indices live on
    // this DontDestroyOnLoad object and are trivially authoritative across
    // scene loads.
    private List<TaskConfig> _tasks;
    private List<List<string>> _shuffledSequences;
    private int _taskIndex = -1;       // -2 = on the tutorial scene; -1 = still in StartScene; 0+ = index into _tasks
    private int _sceneIndexInTask = -1; // -1 = currently on the current task's interlude
    private bool _inSurvey;            // on the Survey scene after the current task's last scene

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _tasks = new List<TaskConfig> { _task1, _task2, _task3, _task4 };

        if (_advanceAction != null)
        {
            _advanceAction.action.performed += OnAdvancePressed;
            _advanceAction.action.Enable();
        }
        else
        {
            Debug.LogError("StudyController: Advance action is not assigned.");
        }

        StudyPlanReceiver.WaitForPlanOrTimeout(this, _rosPlanTimeoutSeconds);
    }

    // Builds _shuffledSequences from a StudyPlan received over ROS (/study/plan), so the
    // VR and RViz2 sessions for a given participant see the same task/scene order. Reorders
    // _tasks to match the plan's task order and skips ShuffleWithoutReplacement entirely,
    // since the ROS-supplied scene order is already final.
    public void ApplyExternalPlan(StudyPlanMsg plan)
    {
        List<TaskConfig> orderedTasks = new List<TaskConfig>();
        List<List<string>> sequences = new List<List<string>>();

        foreach (TaskPlanMsg taskPlan in plan.tasks)
        {
            TaskConfig matching = _tasks.FirstOrDefault(t => t.interludeSceneName == taskPlan.interlude_scene);
            if (matching == null)
            {
                Debug.LogError($"StudyController: received plan references unknown interlude scene '{taskPlan.interlude_scene}'; ignoring this task entry.");
                continue;
            }
            orderedTasks.Add(matching);
            sequences.Add(taskPlan.scene_names.ToList());
        }

        _tasks = orderedTasks;
        _shuffledSequences = sequences;
        ParticipantId = plan.participant_id;
        Debug.Log($"StudyController: applied external study plan for participant '{plan.participant_id}' ({_tasks.Count} tasks).");

        FinishInitialization();
    }

    // Local random generation, unchanged from the original behavior -- used as a fallback
    // when no /study/plan message arrives in time (e.g. running standalone without ROS).
    public void ApplyLocalFallbackPlan()
    {
        ShuffleInPlace(_tasks);
        _shuffledSequences = new List<List<string>>();
        foreach (TaskConfig task in _tasks)
        {
            _shuffledSequences.Add(ShuffleWithoutReplacement(task.scenePool, task.numScenesToPlay));
        }
        Debug.Log("StudyController: applied locally-generated fallback study plan.");

        FinishInitialization();
    }

    private void FinishInitialization()
    {
        Invoke(nameof(BeginStudy), _startSceneAutoAdvanceDelay);
    }

    // Leaves StartScene automatically once settled, instead of waiting for a button press.
    private void BeginStudy()
    {
        if (!string.IsNullOrEmpty(_tutorialSceneName))
        {
            _taskIndex = -2;
            SceneManager.LoadScene(_tutorialSceneName);
            return;
        }

        _taskIndex = 0;
        _sceneIndexInTask = -1;
        LoadCurrentInterlude();
    }

    // Called by the tutorial scene once the participant has stepped through every control,
    // to hand off into the study proper. Mirrors what BeginStudy() does when no tutorial is configured.
    public void FinishTutorial()
    {
        _taskIndex = 0;
        _sceneIndexInTask = -1;
        LoadCurrentInterlude();
    }

    private static void ShuffleInPlace<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static List<string> ShuffleWithoutReplacement(List<string> pool, int n)
    {
        List<string> shuffled = new List<string>(pool);
        ShuffleInPlace(shuffled);

        if (n > shuffled.Count)
        {
            Debug.LogError($"StudyController: requested {n} scenes but pool only has {shuffled.Count}. Clamping.");
            n = shuffled.Count;
        }

        return shuffled.GetRange(0, n);
    }

    private void OnAdvancePressed(InputAction.CallbackContext context)
    {
        if (_taskIndex == -2)
        {
            // On the tutorial scene - TutorialStepDisplay owns this button press locally
            // and calls FinishTutorial() itself once the participant is done.
            return;
        }

        if (_inSurvey)
        {
            // On the survey scene - SurveyStepDisplay owns this button press locally
            // and calls FinishSurvey() itself once the participant is done.
            return;
        }

        if (_shuffledSequences == null || _taskIndex < 0)
        {
            // Still settling in StartScene (auto-advance hasn't fired yet) - ignore.
            return;
        }

        if (_taskIndex >= _tasks.Count)
        {
            // On StudyComplete - there is nothing left to advance to.
            return;
        }

        if (TryGetAdvanceBlockReason(out string blockedMessage, out string blockedDetails))
        {
            ShowAdvanceBlockedDialog(blockedMessage, blockedDetails);
            return;
        }

        RequestAdvanceConfirmation(PerformAdvance);
    }

    // The actual advancement, run only once the participant confirms via the dialog.
    private void PerformAdvance()
    {
        // On the current task's interlude: move into its first task scene.
        if (_sceneIndexInTask == -1)
        {
            _sceneIndexInTask = 0;
            LoadCurrentTaskScene();
            return;
        }

        // On a task scene: advance to the next one, or roll over into the post-task survey.
        _sceneIndexInTask++;
        if (_sceneIndexInTask < _shuffledSequences[_taskIndex].Count)
        {
            LoadCurrentTaskScene();
            return;
        }

        if (!string.IsNullOrEmpty(_surveySceneName))
        {
            _inSurvey = true;
            SceneManager.LoadScene(_surveySceneName);
            return;
        }

        AdvanceToNextTask();
    }

    // Shows the Confirm/Cancel dialog and runs onConfirm only if the participant confirms.
    // TutorialStepDisplay and SurveyStepDisplay route their page turns through here too, so
    // every advance press in the study goes through the same gate. Falls through to onConfirm
    // when no StudyController exists (e.g. a scene played directly in the editor).
    public static void ConfirmAdvance(System.Action onConfirm)
    {
        if (Instance != null)
        {
            Instance.RequestAdvanceConfirmation(onConfirm);
        }
        else
        {
            Debug.LogWarning("StudyController.ConfirmAdvance: no StudyController instance; advancing without confirmation.");
            onConfirm();
        }
    }

    public void RequestAdvanceConfirmation(System.Action onConfirm)
    {
        if (_activeConfirmDialog != null)
        {
            // A dialog is already up - repeat advance presses are ignored until it is resolved.
            return;
        }

        if (_confirmDialogTemplate == null || _confirmDialogUxml == null)
        {
            // Fail open rather than soft-locking the study on a broken reference.
            Debug.LogWarning("StudyController: confirmation dialog is not configured; advancing without confirmation.");
            onConfirm();
            return;
        }

        AdvanceConfirmDialogController dialog = CreateAdvanceDialog(kConfirmPanelWidthPx, kConfirmPanelHeightPx);
        if (dialog == null)
        {
            // Fail open: a broken template must not block the study.
            onConfirm();
            return;
        }

        dialog.Confirmed += () =>
        {
            ObjectMetricsLogger.Instance?.LogEvent("advance_confirmed", "study_advance");
            CloseConfirmDialog();
            onConfirm();
        };
        dialog.Cancelled += () =>
        {
            ObjectMetricsLogger.Instance?.LogEvent("advance_cancelled", "study_advance");
            CloseConfirmDialog();
        };

        ObjectMetricsLogger.Instance?.LogEvent("advance_confirm_shown", "study_advance");
    }

    // Instantiates and configures the world-space dialog template at the given canvas size;
    // returns its controller, or null (dialog cleaned up) if the template is unusable.
    private AdvanceConfirmDialogController CreateAdvanceDialog(float widthPx, float heightPx)
    {
        // Deliberately not DontDestroyOnLoad: any scene change destroys the dialog, which is
        // the correct outcome if something else (e.g. /study/go_back) moves the study along.
        _activeConfirmDialog = Instantiate(_confirmDialogTemplate);
        UIDocument document = _activeConfirmDialog.GetComponentInChildren<UIDocument>(true);
        if (document == null)
        {
            Debug.LogError("StudyController: confirmation dialog template has no UIDocument.");
            CloseConfirmDialog();
            return null;
        }

        document.visualTreeAsset = _confirmDialogUxml;
        if (_confirmDialogPanelSettings != null)
        {
            document.panelSettings = _confirmDialogPanelSettings;
        }

        // Shrink the template's 300x400 canvas to fit the dialog content (no dead space), and
        // re-center the panel on the root: the template offsets the panel quad up and to the
        // side to float above its grab handle, and its pivot is the panel's top-left corner --
        // so centering means offsetting by half the size in local units (1 unit = 100 px).
        document.worldSpaceSize = new Vector2(widthPx, heightPx);
        document.transform.localPosition = new Vector3(-widthPx / 200f, heightPx / 200f, 0f);

        DisableGrabHandle(_activeConfirmDialog);
        PositionDialogInFrontOfCamera(_activeConfirmDialog.transform);

        return document.gameObject.AddComponent<AdvanceConfirmDialogController>();
    }

    // Shows a dismissable "Cannot Advance Yet" notice listing what the participant still has
    // to do in this scene. Always blocks: even when the dialog can't be built, the advance
    // does not proceed.
    private void ShowAdvanceBlockedDialog(string message, string logDetails)
    {
        if (_activeConfirmDialog != null)
        {
            // A dialog is already up - repeat presses are ignored (and not re-logged).
            return;
        }

        ObjectMetricsLogger.Instance?.LogEvent("advance_blocked", "study_advance", details: logDetails);

        if (_confirmDialogTemplate == null || _confirmDialogUxml == null)
        {
            Debug.LogWarning($"StudyController: advance blocked ({logDetails}) but the dialog is not configured: {message}");
            return;
        }

        AdvanceConfirmDialogController dialog = CreateAdvanceDialog(kConfirmPanelWidthPx, kBlockedPanelHeightPx);
        if (dialog == null)
        {
            Debug.LogWarning($"StudyController: advance blocked ({logDetails}): {message}");
            return;
        }

        dialog.SetBlockedMode("Cannot Advance Yet", message);
        // Both events dismiss: OK button (Cancelled) in the headset, Enter/Esc in the editor.
        dialog.Confirmed += CloseConfirmDialog;
        dialog.Cancelled += CloseConfirmDialog;
    }

    private void CloseConfirmDialog()
    {
        if (_activeConfirmDialog != null)
        {
            Destroy(_activeConfirmDialog);
            _activeConfirmDialog = null;
        }
    }

    // Wrist-menu shapes get ids like "unity_cube_638...". Scene-baked publishers
    // ("unity_Kettle_Prefab") and the indicator sphere ("unity_indicator_sphere_...") don't
    // match, so this counts exactly the participant-created objects. The same task gates
    // exist in the RViz panel (study_rviz_panel) -- keep the two in sync.
    private static readonly Regex kUserShapeIdPattern =
        new Regex(@"^unity_(cube|sphere|cylinder|capsule|plane|mesh)_\d+$", RegexOptions.Compiled);

    // Blocks leaving a task scene until that task's work is done in the current scene:
    // Task1 = a user-created object exists; Task2 = that plus a successful plan; Task3 = the
    // indicator sphere was spawned; Task4 = a successful plan plus certification. Fails open
    // (not blocked) on interludes, unknown scenes, or missing scene components -- a
    // misconfigured scene must never soft-lock the study.
    private bool TryGetAdvanceBlockReason(out string message, out string logDetails)
    {
        message = null;
        logDetails = null;

        if (_sceneIndexInTask < 0)
        {
            // On the interlude - nothing to gate.
            return false;
        }

        // The plan shuffles task order, so _taskIndex is the presentation index; the logical
        // task comes from the scene name.
        string sceneName = SceneManager.GetActiveScene().name;
        List<string> missingCodes = new List<string>();
        List<string> missingItems = new List<string>();

        if (sceneName.StartsWith("Task1_"))
        {
            if (CountUserCreatedObjects() == 0)
            {
                missingCodes.Add("user_object");
                missingItems.Add("You must create at least one shape before advancing. Open the wrist menu and use Add Shape to place an object.");
            }
        }
        else if (sceneName.StartsWith("Task2_"))
        {
            if (CountUserCreatedObjects() == 0)
            {
                missingCodes.Add("user_object");
                missingItems.Add("Create at least one shape using the wrist menu's Add Shape button.");
            }
            if (!HasSuccessfulPlanInScene())
            {
                missingCodes.Add("plan_success");
                missingItems.Add("Request at least one successful motion plan using the planning menu's Send Request button.");
            }
        }
        else if (sceneName.StartsWith("Task3_"))
        {
            if (FindFirstObjectByType<IndicatorSphereController>() == null)
            {
                missingCodes.Add("indicator_sphere");
                missingItems.Add("You must place the indicator sphere before advancing. Press 'Get Indicator Sphere' and place the sphere at the collision location.");
            }
        }
        else if (sceneName.StartsWith("Task4_"))
        {
            if (!HasSuccessfulPlanInScene())
            {
                missingCodes.Add("plan_success");
                missingItems.Add("Request at least one successful motion plan using the planning menu's Send Request button.");
            }
            CertifyPathMenuController certifyMenu =
                FindFirstObjectByType<CertifyPathMenuController>(FindObjectsInactive.Include);
            if (certifyMenu == null)
            {
                Debug.LogWarning("StudyController: no CertifyPathMenuController in this Task4 scene; skipping the certification check.");
            }
            else if (!certifyMenu.HasCertified)
            {
                missingCodes.Add("certification");
                missingItems.Add("Press 'Certify Path Collision-Free' once you believe the path is safe.");
            }
        }

        if (missingCodes.Count == 0)
        {
            return false;
        }

        logDetails = "missing=" + string.Join(",", missingCodes);
        message = missingItems.Count == 1
            ? missingItems[0]
            : "Before advancing:\n• " + string.Join("\n• ", missingItems);
        return true;
    }

    private static bool HasSuccessfulPlanInScene()
    {
        MoveItPlanningRequestMenuUI planningMenu =
            FindFirstObjectByType<MoveItPlanningRequestMenuUI>(FindObjectsInactive.Include);
        if (planningMenu == null)
        {
            Debug.LogWarning("StudyController: no MoveItPlanningRequestMenuUI in this scene; skipping the plan-success check.");
            return true;
        }
        return planningMenu.HasPlannedSuccessfully;
    }

    private static int CountUserCreatedObjects()
    {
        int count = 0;
        foreach (CollisionObjectPublisher publisher in
            FindObjectsByType<CollisionObjectPublisher>(FindObjectsSortMode.None))
        {
            if (publisher.objectId != null && kUserShapeIdPattern.IsMatch(publisher.objectId))
            {
                count++;
            }
        }
        return count;
    }

    // The Grab UI template ships as a grabbable panel; the confirmation dialog should stay
    // put, so turn off the grab handle (its visual, collider, and interactable) the same way
    // the certify/indicator menu instances disable theirs.
    private static void DisableGrabHandle(GameObject dialog)
    {
        foreach (SkinnedMeshRenderer handleVisual in dialog.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            handleVisual.enabled = false;
        }
        XRGrabInteractable grab = dialog.GetComponentInChildren<XRGrabInteractable>(true);
        if (grab != null)
        {
            foreach (Collider grabCollider in grab.GetComponents<Collider>())
            {
                grabCollider.enabled = false;
            }
            grab.enabled = false;
        }
    }

    // Same facing convention as Billboard.cs: panel forward aligned with the camera's
    // (horizontal) forward, placed just below eye height within arm's-reach ray distance.
    private static void PositionDialogInFrontOfCamera(Transform dialog)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        // Task scenes put the participant right up against a workbench/counter with the robot
        // on it; a fixed-distance panel would end up inside that geometry and be depth-culled.
        // Trigger colliders are ignored so world-space UI panels (all triggers) can't block it.
        // The grab handle's non-trigger collider is disabled before this runs, so the dialog
        // can't hit itself either.
        float distance = kConfirmDialogDistance;
        if (Physics.Raycast(
                cam.transform.position, forward, out RaycastHit hit, kConfirmDialogDistance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            distance = Mathf.Max(hit.distance - kConfirmDialogClearance, kConfirmDialogMinDistance);
        }

        dialog.position = cam.transform.position
            + forward * distance
            + Vector3.down * kConfirmDialogDropBelowEyes;
        dialog.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    // Called by the survey scene once the participant has stepped through every question,
    // to move on to the next task's interlude (or the completion scene after the last task).
    public void FinishSurvey()
    {
        _inSurvey = false;
        AdvanceToNextTask();
    }

    private void AdvanceToNextTask()
    {
        _taskIndex++;
        _sceneIndexInTask = -1;
        if (_taskIndex < _tasks.Count)
        {
            LoadCurrentInterlude();
        }
        else
        {
            LoadCompletion();
        }
    }

    private void LoadCurrentInterlude()
    {
        SceneManager.LoadScene(_tasks[_taskIndex].interludeSceneName);
    }

    private void LoadCurrentTaskScene()
    {
        SceneManager.LoadScene(_shuffledSequences[_taskIndex][_sceneIndexInTask]);
    }

    private void LoadCompletion()
    {
        // The advance action stays subscribed: OnAdvancePressed ignores presses while complete.
        Debug.Log("StudyController: Study complete.");
        SceneManager.LoadScene("StudyComplete");
    }

    private void OnDestroy()
    {
        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
    }
}
