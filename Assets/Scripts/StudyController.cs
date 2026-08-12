using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using RosMessageTypes.StudyInterfaces;
using Stopwatch = System.Diagnostics.Stopwatch;

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
    private PanelSettings _confirmDialogRuntimePanelSettings;

    // Panel canvas size in UI Toolkit pixels. At WristUISettings' 100 px/unit and the Grab UI
    // template's 0.2 root scale, this comes out to a 0.68 m x 0.36 m panel in the world.
    private const float kConfirmPanelWidthPx = 340f;
    private const float kConfirmPanelHeightPx = 180f;
    // Taller variant for the "Cannot Advance Yet" notice, whose two-bullet messages need room.
    private const float kBlockedPanelHeightPx = 220f;
    // Candidate placement settings for the world-space confirmation panel. Positions are
    // tested in preference order, beginning directly in front of and slightly below eye level.
    private const float kConfirmDialogObstacleClearance = 0.08f;
    private const float kConfirmDialogPanelHalfDepth = 0.04f;
    private const float kConfirmDialogSightlineEndTolerance = 0.04f;
    private const float kConfirmDialogSortingOrder = 32767f;
    private static readonly float[] kConfirmDialogDistances = { 0.9f, 0.7f, 0.5f, 1.1f, 1.3f };
    private static readonly float[] kConfirmDialogVerticalOffsets = { -0.1f, 0.15f, 0.4f };
    private static readonly float[] kConfirmDialogYawOffsets = { 0f, -25f, 25f, -50f, 50f, -75f, 75f };

    [Header("Start Scene")]
    [Tooltip("Optional pause before auto-advancing from StartScene into the tutorial (or the first task's interlude, if no tutorial scene is set).")]
    [SerializeField] private float _startSceneAutoAdvanceDelay = 0f;

    [Header("Tutorial")]
    [Tooltip("Scene shown once, before the first task's interlude, to walk participants through the controls. Leave blank to skip straight into the study.")]
    [SerializeField] private string _tutorialSceneName = "Tutorial";

    [Header("Survey")]
    [Tooltip("Scene shown after the last scene of every task, stepping the participant through the post-task survey questions. Leave blank to skip straight to the next task's interlude.")]
    [SerializeField] private string _surveySceneName = "Survey";

    [Header("Scene Transitions")]
    [Tooltip("Minimum time to keep the current scene active while the next scene loads in the background before activating it.")]
    [SerializeField] private float _minimumSceneTransitionDelaySeconds = 2f;
    [Tooltip("Extra time to wait after a preloaded scene reaches Unity's ready-to-activate point before activating it.")]
    [SerializeField] private float _preloadedSceneReadySettleSeconds = 1f;
    [Tooltip("How long to keep the loading overlay visible after the new scene activates, giving SpawnHuman and XR tracking a frame to settle.")]
    [SerializeField] private float _loadingOverlayPostActivationDelaySeconds = 0.5f;
    [Tooltip("Rendered frames to wait after showing the loading overlay before starting expensive scene load or activation work.")]
    [SerializeField] private int _loadingOverlayWarmupFrames = 3;
    [Tooltip("If enabled, starts loading the next scene while the participant is still in the current scene. Background integration is capped by Application.backgroundLoadingPriority.")]
    [SerializeField] private bool _preloadScenesDuringCurrentScene = true;
    [Tooltip("Maximum main-thread time spent disabling roots from the outgoing content scene in one frame.")]
    [SerializeField, Min(0.1f)] private float _sceneRetirementFrameBudgetMilliseconds = 1.5f;

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
    private bool _isSceneTransitionInProgress;
    private AsyncOperation _preloadedSceneOperation;
    private string _preloadedSceneName;
    private bool _isPreloadedSceneReady;
    private float _preloadedSceneReadyTime = -1f;
    private Coroutine _preloadCoroutine;
    private ThreadPriority _previousBackgroundLoadingPriority;
    private bool _isBackgroundLoadingPriorityOverridden;
    private Scene _bootstrapScene;
    private Scene _currentContentScene;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _bootstrapScene = SceneManager.GetActiveScene();
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

        // Resume first: /study/resume is latched and only ever published by a crash-recovery
        // (crash:=true) study_controller_node. Subscribing before the plan means the resume
        // message tends to be delivered before FinishInitialization decides what to preload.
        StudyResumeReceiver.Listen(this);
        StudyPlanReceiver.WaitForPlanOrTimeout(this, _rosPlanTimeoutSeconds);
    }

    // Latest crash-recovery stop received on /study/resume; null in normal sessions.
    private StudyStateMsg _pendingResume;

    public void ApplyResumeState(StudyStateMsg msg)
    {
        if (_taskIndex != -1 || _inSurvey)
        {
            // The study is already underway (or resumed); latched re-publishes are expected
            // while the recovery session journals new scenes — ignore them.
            return;
        }

        _pendingResume = msg;
        Debug.Log($"StudyController: crash-recovery resume state received — phase '{msg.phase}', " +
            $"task {msg.task_index}, scene {msg.scene_index} ('{msg.scene_name}').");
    }

    // Scene the resume stop maps to, or null when it should fall back to a normal start.
    private string GetResumeTargetSceneName(StudyStateMsg resume)
    {
        switch (resume.phase)
        {
            case "interlude":
                return (resume.task_index >= 0 && resume.task_index < _tasks.Count)
                    ? _tasks[resume.task_index].interludeSceneName
                    : null;
            case "scene":
                return (_shuffledSequences != null &&
                        resume.task_index >= 0 && resume.task_index < _shuffledSequences.Count &&
                        resume.scene_index >= 0 &&
                        resume.scene_index < _shuffledSequences[resume.task_index].Count)
                    ? _shuffledSequences[resume.task_index][resume.scene_index]
                    : null;
            case "survey":
                return (resume.task_index >= 0 && resume.task_index < _tasks.Count)
                    ? _surveySceneName
                    : null;
            case "complete":
                return "StudyComplete";
            default:
                // "tutorial" (and anything unrecognized) is just a normal start.
                return null;
        }
    }

    private bool TryResumeFrom(StudyStateMsg resume)
    {
        string targetScene = GetResumeTargetSceneName(resume);
        if (string.IsNullOrEmpty(targetScene))
        {
            return false;
        }

        // A held preload for a different scene would stall any other load, so a resume that
        // arrived after the preload decision must fall back to the normal start (which the
        // pending preload matches) rather than deadlock the transition.
        if (_preloadedSceneOperation != null && _preloadedSceneName != targetScene)
        {
            Debug.LogWarning($"StudyController: resume target '{targetScene}' arrived after " +
                $"'{_preloadedSceneName}' began preloading; starting normally instead.");
            return false;
        }

        Debug.Log($"StudyController: resuming study at '{targetScene}' after crash recovery.");
        switch (resume.phase)
        {
            case "interlude":
                _taskIndex = resume.task_index;
                _sceneIndexInTask = -1;
                LoadCurrentInterlude();
                return true;
            case "scene":
                _taskIndex = resume.task_index;
                _sceneIndexInTask = resume.scene_index;
                LoadCurrentTaskScene();
                return true;
            case "survey":
                _taskIndex = resume.task_index;
                _sceneIndexInTask = _shuffledSequences[_taskIndex].Count;
                _inSurvey = true;
                LoadSceneWhenReady(_surveySceneName);
                return true;
            case "complete":
                _taskIndex = _tasks.Count;
                _sceneIndexInTask = -1;
                LoadCompletion();
                return true;
            default:
                return false;
        }
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
        _planFromRos = true;
        Debug.Log($"StudyController: applied external study plan for participant '{plan.participant_id}' ({_tasks.Count} tasks).");

        FinishInitialization();
    }

    // True when the plan came from /study/plan (vs the local standalone fallback) — only
    // then can a crash-recovery /study/resume arrive, so only then is it worth waiting for.
    private bool _planFromRos;

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

    // How long a ROS-driven session waits for a possible /study/resume before committing to
    // a normal start. The crash-recovery node republishes resume at 1 Hz (the endpoint's
    // volatile relay subscription means latching alone never delivers it), so when recovery
    // is active the message lands well inside this window; in a normal session nothing
    // arrives and startup proceeds after the wait.
    private const float kResumeWaitSeconds = 2.5f;

    private void FinishInitialization()
    {
        StartCoroutine(FinishInitializationCoroutine());
    }

    private IEnumerator FinishInitializationCoroutine()
    {
        if (_planFromRos && _pendingResume == null)
        {
            float deadline = Time.unscaledTime + kResumeWaitSeconds;
            while (_pendingResume == null && Time.unscaledTime < deadline)
            {
                yield return null;
            }
        }

        // In a crash-recovery session the resume stop, not the study's first scene, is what
        // should warm up — a held preload for the wrong scene would block the resume load.
        string resumeTarget = _pendingResume != null ? GetResumeTargetSceneName(_pendingResume) : null;
        BeginScenePreloadIfEnabled(resumeTarget ?? GetFirstSceneName());

        if (_startSceneAutoAdvanceDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(_startSceneAutoAdvanceDelay);
        }
        BeginStudy();
    }

    // Leaves StartScene automatically once settled, instead of waiting for a button press.
    private void BeginStudy()
    {
        if (_pendingResume != null && TryResumeFrom(_pendingResume))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_tutorialSceneName))
        {
            _taskIndex = -2;
            LoadSceneWhenReady(_tutorialSceneName);
            return;
        }

        _taskIndex = 0;
        _sceneIndexInTask = -1;
        LoadCurrentInterlude();
    }

    // Called by the tutorial scene once the participant has stepped through every control,
    // to hand off into the study proper. Mirrors what BeginStudy() does when no tutorial is configured.
    public bool FinishTutorial()
    {
        if (!TryCommitSceneTransition(GetTaskInterludeOrCompletion(0)))
        {
            return false;
        }

        _taskIndex = 0;
        _sceneIndexInTask = -1;
        LoadCurrentInterlude();
        return true;
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

        if (_isSceneTransitionInProgress)
        {
            // On StudyComplete - there is nothing left to advance to.
            return;
        }

        if (TryGetAdvanceBlockReason(out string blockedMessage, out string blockedDetails))
        {
            ShowAdvanceBlockedDialog(blockedMessage, blockedDetails);
            return;
        }

        string nextSceneName = GetAdvanceTargetSceneName();
        Debug.Log($"StudyController: advance requested from '{SceneManager.GetActiveScene().name}' to '{nextSceneName}'.");
        if (!ShouldRequestAdvanceConfirmation())
        {
            if (TryCommitSceneTransition(nextSceneName))
            {
                PerformAdvance();
            }
            return;
        }

        RequestAdvanceConfirmation(() =>
        {
            if (TryCommitSceneTransition(nextSceneName))
            {
                PerformAdvance();
            }
        });
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
            LoadSceneWhenReady(_surveySceneName);
            return;
        }

        AdvanceToNextTask();
    }

    // Shows the Confirm/Cancel dialog only on real task scenes. Tutorial, survey, and task
    // interlude page turns advance immediately.
    public static void ConfirmAdvance(System.Action onConfirm)
    {
        if (Instance != null)
        {
            if (Instance.ShouldRequestAdvanceConfirmation())
            {
                Instance.RequestAdvanceConfirmation(onConfirm);
            }
            else
            {
                onConfirm();
            }
        }
        else
        {
            Debug.LogWarning("StudyController.ConfirmAdvance: no StudyController instance; advancing without confirmation.");
            onConfirm();
        }
    }

    private bool ShouldRequestAdvanceConfirmation()
    {
        return _taskIndex >= 0 && _sceneIndexInTask >= 0 && !_inSurvey;
    }

    public void RequestAdvanceConfirmation(System.Action onConfirm)
    {
        if (_activeConfirmDialog != null)
        {
            RepositionActiveConfirmDialog();
            return;
        }

        if (_confirmDialogTemplate == null || _confirmDialogUxml == null)
        {
            Debug.LogError("StudyController: confirmation dialog is not configured; advance cancelled.");
            return;
        }

        AdvanceConfirmDialogController dialog = CreateAdvanceDialog(kConfirmPanelWidthPx, kConfirmPanelHeightPx);
        if (dialog == null)
        {
            Debug.LogError("StudyController: confirmation dialog could not be created; advance cancelled.");
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

        Debug.Log("StudyController: advance confirmation dialog shown.");
        ObjectMetricsLogger.Instance?.LogEvent("advance_confirm_shown", "study_advance");
    }

    // Instantiates and configures the world-space dialog template at the given canvas size;
    // returns its controller, or null (dialog cleaned up) if the template is unusable.
    private AdvanceConfirmDialogController CreateAdvanceDialog(float widthPx, float heightPx)
    {
        // Deliberately not DontDestroyOnLoad: any scene change destroys the dialog, which is
        // the correct outcome if something else (e.g. /study/go_back) moves the study along.
        _activeConfirmDialog = Instantiate(_confirmDialogTemplate);
        _activeConfirmDialog.SetActive(false);

        UIDocument document = _activeConfirmDialog.GetComponentInChildren<UIDocument>(true);
        if (document == null)
        {
            Debug.LogError("StudyController: confirmation dialog template has no UIDocument.");
            CloseConfirmDialog();
            return null;
        }

        document.visualTreeAsset = _confirmDialogUxml;
        PanelSettings sourcePanelSettings = _confirmDialogPanelSettings != null
            ? _confirmDialogPanelSettings
            : document.panelSettings;
        if (_confirmDialogRuntimePanelSettings == null && sourcePanelSettings != null)
        {
            // Use a private settings instance so the confirmation can render last without
            // changing the wrist menu and every other document that shares the source asset.
            // Keep it for later dialogs so subsequent openings do not repeat this setup.
            _confirmDialogRuntimePanelSettings = Instantiate(sourcePanelSettings);
            _confirmDialogRuntimePanelSettings.name = "Confirmation Dialog Panel Settings (Runtime)";
            _confirmDialogRuntimePanelSettings.hideFlags = HideFlags.DontSave;
            _confirmDialogRuntimePanelSettings.sortingOrder = kConfirmDialogSortingOrder;
            _confirmDialogRuntimePanelSettings.clearDepthStencil = true;
        }
        if (_confirmDialogRuntimePanelSettings != null)
            document.panelSettings = _confirmDialogRuntimePanelSettings;
        document.sortingOrder = kConfirmDialogSortingOrder;

        // Shrink the template's 300x400 canvas to fit the dialog content (no dead space), and
        // re-center the panel on the root: the template offsets the panel quad up and to the
        // side to float above its grab handle, and its pivot is the panel's top-left corner --
        // so centering means offsetting by half the size in local units (1 unit = 100 px).
        document.worldSpaceSize = new Vector2(widthPx, heightPx);
        document.transform.localPosition = new Vector3(-widthPx / 200f, heightPx / 200f, 0f);

        DisableGrabHandle(_activeConfirmDialog);
        if (!TryPositionDialogInClearSpace(_activeConfirmDialog.transform, widthPx, heightPx))
        {
            Debug.LogError("StudyController: no clear, visible position was found for the confirmation dialog.");
            CloseConfirmDialog();
            return null;
        }

        AdvanceConfirmDialogController dialog = document.gameObject.AddComponent<AdvanceConfirmDialogController>();
        _activeConfirmDialog.SetActive(true);
        return dialog;
    }

    // Shows a dismissable "Cannot Advance Yet" notice listing what the participant still has
    // to do in this scene. Always blocks: even when the dialog can't be built, the advance
    // does not proceed.
    private void ShowAdvanceBlockedDialog(string message, string logDetails)
    {
        if (_activeConfirmDialog != null)
        {
            RepositionActiveConfirmDialog();
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
            _activeConfirmDialog.SetActive(false);
            Destroy(_activeConfirmDialog);
            _activeConfirmDialog = null;
        }
    }

    // A second advance-button press recenters the existing prompt relative to the user's
    // current view. Temporarily deactivate it so its own panel collider is not mistaken for
    // an obstacle by the same collision-safe placement checks used when it first opens.
    private bool RepositionActiveConfirmDialog()
    {
        if (_activeConfirmDialog == null)
            return false;

        UIDocument document = _activeConfirmDialog.GetComponentInChildren<UIDocument>(true);
        if (document == null)
        {
            Debug.LogWarning("StudyController: cannot reposition the confirmation dialog because it has no UIDocument.");
            return false;
        }

        bool wasActive = _activeConfirmDialog.activeSelf;
        if (wasActive)
            _activeConfirmDialog.SetActive(false);

        Vector2 panelSize = document.worldSpaceSize;
        bool repositioned = TryPositionDialogInClearSpace(
            _activeConfirmDialog.transform,
            panelSize.x,
            panelSize.y);

        if (wasActive)
            _activeConfirmDialog.SetActive(true);

        if (repositioned)
            Debug.Log("StudyController: confirmation dialog moved to the user's current view.");
        else
            Debug.LogWarning("StudyController: confirmation dialog kept its previous position because no clear position was found in the current view.");

        return repositioned;
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
        Debug.Log($"StudyController: advance blocked in '{sceneName}' ({logDetails}).");
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

    // The source prefab includes a grab bar, but this confirmation prompt should stay at the
    // collision-tested position chosen when it opens. Keep only its UI surface interactive.
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

    // Places the old world-space XR dialog at the first candidate that neither overlaps scene
    // geometry nor has an obstacle between the user's eyes and the panel's center/corners.
    // The inactive dialog cannot collide with its own prefab colliders during these checks.
    private static bool TryPositionDialogInClearSpace(Transform dialog, float widthPx, float heightPx)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                {
                    camera = cameras[i];
                    break;
                }
            }
        }

        if (camera == null)
        {
            Debug.LogWarning("StudyController: cannot position the confirmation dialog because no active camera was found.");
            return false;
        }

        Vector3 eyePosition = camera.transform.position;
        Vector3 forward = camera.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        Vector3 scale = dialog.lossyScale;
        Vector3 panelHalfExtents = new Vector3(
            widthPx / 200f * Mathf.Abs(scale.x) + kConfirmDialogObstacleClearance,
            heightPx / 200f * Mathf.Abs(scale.y) + kConfirmDialogObstacleClearance,
            kConfirmDialogPanelHalfDepth + kConfirmDialogObstacleClearance);

        for (int yawIndex = 0; yawIndex < kConfirmDialogYawOffsets.Length; yawIndex++)
        {
            Vector3 candidateDirection =
                Quaternion.AngleAxis(kConfirmDialogYawOffsets[yawIndex], Vector3.up) * forward;
            Quaternion candidateRotation = Quaternion.LookRotation(candidateDirection, Vector3.up);

            for (int verticalIndex = 0; verticalIndex < kConfirmDialogVerticalOffsets.Length; verticalIndex++)
            {
                for (int distanceIndex = 0; distanceIndex < kConfirmDialogDistances.Length; distanceIndex++)
                {
                    Vector3 candidatePosition = eyePosition
                        + candidateDirection * kConfirmDialogDistances[distanceIndex]
                        + Vector3.up * kConfirmDialogVerticalOffsets[verticalIndex];

                    if (!IsDialogPlacementClear(
                            eyePosition,
                            candidatePosition,
                            candidateRotation,
                            panelHalfExtents))
                    {
                        continue;
                    }

                    dialog.SetPositionAndRotation(candidatePosition, candidateRotation);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDialogPlacementClear(
        Vector3 eyePosition,
        Vector3 panelPosition,
        Quaternion panelRotation,
        Vector3 panelHalfExtents)
    {
        if (Physics.CheckBox(
                panelPosition,
                panelHalfExtents,
                panelRotation,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        Vector3 panelRight = panelRotation * Vector3.right;
        Vector3 panelUp = panelRotation * Vector3.up;
        float sightlineHalfWidth = panelHalfExtents.x - kConfirmDialogObstacleClearance;
        float sightlineHalfHeight = panelHalfExtents.y - kConfirmDialogObstacleClearance;
        return !IsDialogSightlineBlocked(eyePosition, panelPosition)
            && !IsDialogSightlineBlocked(
                eyePosition,
                panelPosition + panelRight * sightlineHalfWidth + panelUp * sightlineHalfHeight)
            && !IsDialogSightlineBlocked(
                eyePosition,
                panelPosition + panelRight * sightlineHalfWidth - panelUp * sightlineHalfHeight)
            && !IsDialogSightlineBlocked(
                eyePosition,
                panelPosition - panelRight * sightlineHalfWidth + panelUp * sightlineHalfHeight)
            && !IsDialogSightlineBlocked(
                eyePosition,
                panelPosition - panelRight * sightlineHalfWidth - panelUp * sightlineHalfHeight);
    }

    private static bool IsDialogSightlineBlocked(Vector3 eyePosition, Vector3 target)
    {
        Vector3 toTarget = target - eyePosition;
        float sightlineDistance = toTarget.magnitude - kConfirmDialogSightlineEndTolerance;
        return sightlineDistance > 0f && Physics.Raycast(
            eyePosition,
            toTarget.normalized,
            sightlineDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
    }

    // Called by the survey scene once the participant has stepped through every question,
    // to move on to the next task's interlude (or the completion scene after the last task).
    public bool FinishSurvey()
    {
        _inSurvey = false;
        AdvanceToNextTask();
        return true;
    }

    // Called by the survey scene when the participant backs out of the survey's intro page,
    // to return to the current task's last scene (reloaded from scratch).
    public bool BackOutOfSurvey()
    {
        if (!_inSurvey)
        {
            return false;
        }

        _inSurvey = false;
        AdvanceToNextTask();
        return true;
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
        LoadSceneWhenReady(_tasks[_taskIndex].interludeSceneName);
    }

    private void LoadCurrentTaskScene()
    {
        LoadSceneWhenReady(_shuffledSequences[_taskIndex][_sceneIndexInTask]);
    }

    private void LoadCompletion()
    {
        // The advance action stays subscribed: OnAdvancePressed ignores presses while complete.
        Debug.Log("StudyController: Study complete.");
        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
        LoadSceneWhenReady("StudyComplete");
    }

    private void LoadSceneWhenReady(string sceneName)
    {
        if (_isSceneTransitionInProgress)
        {
            Debug.LogWarning($"StudyController: already loading a scene, ignoring request to load '{sceneName}'.");
            return;
        }

        if (_preloadedSceneOperation != null && _preloadedSceneName == sceneName)
        {
            StartCoroutine(ActivatePreloadedSceneCoroutine(sceneName));
            return;
        }

        StartCoroutine(LoadSceneWithoutPreloadCoroutine(sceneName));
    }

    private IEnumerator ActivatePreloadedSceneCoroutine(string sceneName)
    {
        _isSceneTransitionInProgress = true;
        SceneTransitionOverlay.Show();
        yield return WaitForLoadingOverlayWarmup();
        float activationTime = Time.unscaledTime + Mathf.Max(0f, _minimumSceneTransitionDelaySeconds);

        while (!CanActivatePreloadedScene(sceneName) || Time.unscaledTime < activationTime)
        {
            yield return null;
        }

        AsyncOperation operation = _preloadedSceneOperation;
        _preloadedSceneOperation = null;
        _preloadedSceneName = null;
        _isPreloadedSceneReady = false;
        _preloadedSceneReadyTime = -1f;
        _preloadCoroutine = null;

        operation.allowSceneActivation = true;
        yield return CompleteAdditiveSceneTransition(sceneName, operation);
    }

    private IEnumerator LoadSceneWithoutPreloadCoroutine(string sceneName)
    {
        _isSceneTransitionInProgress = true;
        SceneTransitionOverlay.Show();
        yield return WaitForLoadingOverlayWarmup();
        float transitionStartTime = Time.unscaledTime;
        LowerBackgroundLoadingPriority();

        AsyncOperation loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        if (loadOperation == null)
        {
            Debug.LogError($"StudyController: failed to start async load for scene '{sceneName}'.");
            RestoreBackgroundLoadingPriority();
            SceneTransitionOverlay.Hide();
            _isSceneTransitionInProgress = false;
            yield break;
        }

        loadOperation.allowSceneActivation = false;

        while (loadOperation.progress < 0.9f)
        {
            yield return null;
        }

        float activationTime = transitionStartTime + Mathf.Max(0f, _minimumSceneTransitionDelaySeconds);
        while (Time.unscaledTime < activationTime)
        {
            yield return null;
        }

        yield return null;
        RestoreBackgroundLoadingPriority();
        loadOperation.allowSceneActivation = true;

        yield return CompleteAdditiveSceneTransition(sceneName, loadOperation);
    }

    private void BeginScenePreloadIfEnabled(string sceneName)
    {
        if (_preloadScenesDuringCurrentScene)
        {
            BeginScenePreload(sceneName);
        }
    }

    private void BeginScenePreload(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName) || _isSceneTransitionInProgress)
        {
            return;
        }

        if (_preloadedSceneOperation != null || _preloadCoroutine != null)
        {
            if (_preloadedSceneName != sceneName)
            {
                Debug.LogWarning($"StudyController: '{_preloadedSceneName}' is already preloading; cannot also preload '{sceneName}'.");
            }
            return;
        }

        _preloadCoroutine = StartCoroutine(PreloadSceneCoroutine(sceneName));
    }

    private IEnumerator PreloadSceneCoroutine(string sceneName)
    {
        _preloadedSceneName = sceneName;
        _isPreloadedSceneReady = false;

        LowerBackgroundLoadingPriority();

        AsyncOperation loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        if (loadOperation == null)
        {
            Debug.LogError($"StudyController: failed to start async preload for scene '{sceneName}'.");
            RestoreBackgroundLoadingPriority();
            ClearPreloadState();
            yield break;
        }

        _preloadedSceneOperation = loadOperation;
        loadOperation.allowSceneActivation = false;

        while (loadOperation.progress < 0.9f)
        {
            yield return null;
        }

        RestoreBackgroundLoadingPriority();
        _isPreloadedSceneReady = true;
        _preloadedSceneReadyTime = Time.unscaledTime;
    }

    private IEnumerator CompleteAdditiveSceneTransition(string sceneName, AsyncOperation loadOperation)
    {
        while (!loadOperation.isDone)
        {
            yield return null;
        }

        Scene incomingScene = FindNewestLoadedScene(sceneName);
        if (!incomingScene.IsValid() || !incomingScene.isLoaded)
        {
            UnityEngine.Debug.LogError($"StudyController: additive load completed but scene '{sceneName}' was not found.");
            SceneTransitionOverlay.Hide();
            _isSceneTransitionInProgress = false;
            yield break;
        }

        Scene outgoingScene = _currentContentScene;
        SceneManager.SetActiveScene(incomingScene);
        _currentContentScene = incomingScene;

        // PersistentXRInfrastructure disables the incoming scene's duplicate XR/platform
        // roots from its sceneLoaded callback before the next frame is rendered. Retire the
        // remaining outgoing content incrementally so OnDisable work is spread across frames.
        if (outgoingScene.IsValid() && outgoingScene.isLoaded &&
            outgoingScene != incomingScene && outgoingScene != _bootstrapScene)
        {
            yield return RetireSceneIncrementally(outgoingScene);
        }

        // The retired scene's publishers sent their planning-scene REMOVEs from OnDestroy,
        // mid-transition, where they can be lost (reconnects, races with the incoming ADDs).
        // Now that the unload is complete and live-publisher counts are settled, re-publish
        // REMOVEs for anything still in the planning scene that no live publisher owns.
        CollisionObjectPublisher.PublishRemovalsForOrphanedIds();

        yield return new WaitForSecondsRealtime(Mathf.Max(0f, _loadingOverlayPostActivationDelaySeconds));
        SceneTransitionOverlay.Hide();

        _isSceneTransitionInProgress = false;
        BeginScenePreloadIfEnabled(GetUpcomingSceneName());
    }

    private Scene FindNewestLoadedScene(string sceneName)
    {
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded && scene.name == sceneName && scene != _currentContentScene)
            {
                return scene;
            }
        }

        return default;
    }

    private IEnumerator RetireSceneIncrementally(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        float budgetMilliseconds = Mathf.Max(0.1f, _sceneRetirementFrameBudgetMilliseconds);
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }

            if (stopwatch.Elapsed.TotalMilliseconds >= budgetMilliseconds)
            {
                stopwatch.Restart();
                yield return null;
            }
        }

        AsyncOperation unloadOperation = SceneManager.UnloadSceneAsync(scene);
        if (unloadOperation == null)
        {
            UnityEngine.Debug.LogWarning($"StudyController: failed to begin unloading scene '{scene.name}'.");
            yield break;
        }

        while (!unloadOperation.isDone)
        {
            yield return null;
        }
    }

    private void LowerBackgroundLoadingPriority()
    {
        if (_isBackgroundLoadingPriorityOverridden)
        {
            return;
        }

        _previousBackgroundLoadingPriority = Application.backgroundLoadingPriority;
        Application.backgroundLoadingPriority = ThreadPriority.Low;
        _isBackgroundLoadingPriorityOverridden = true;
    }

    private void RestoreBackgroundLoadingPriority()
    {
        if (!_isBackgroundLoadingPriorityOverridden)
        {
            return;
        }

        Application.backgroundLoadingPriority = _previousBackgroundLoadingPriority;
        _isBackgroundLoadingPriorityOverridden = false;
    }

    private void ClearPreloadState()
    {
        _preloadedSceneOperation = null;
        _preloadedSceneName = null;
        _isPreloadedSceneReady = false;
        _preloadedSceneReadyTime = -1f;
        _preloadCoroutine = null;
    }

    private bool TryCommitSceneTransition(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return false;
        }

        if (_isSceneTransitionInProgress)
        {
            return false;
        }

        if (_preloadedSceneOperation != null && _preloadedSceneName != sceneName)
        {
            Debug.LogWarning($"StudyController: '{_preloadedSceneName}' is already preloading; cannot transition to '{sceneName}' yet.");
            return false;
        }

        SceneTransitionOverlay.Show();
        return true;
    }

    private IEnumerator WaitForLoadingOverlayWarmup()
    {
        int framesToWait = Mathf.Max(5, _loadingOverlayWarmupFrames);
        for (int i = 0; i < framesToWait; i++)
        {
            yield return null;
        }
    }

    private bool CanActivatePreloadedScene(string sceneName)
    {
        if (_preloadedSceneOperation == null || _preloadedSceneName != sceneName || !_isPreloadedSceneReady)
        {
            return false;
        }

        float settledAt = _preloadedSceneReadyTime + Mathf.Max(0f, _preloadedSceneReadySettleSeconds);
        return Time.unscaledTime >= settledAt;
    }

    private string GetAdvanceTargetSceneName()
    {
        if (_shuffledSequences == null || _taskIndex < 0 || _taskIndex >= _shuffledSequences.Count)
        {
            return null;
        }

        if (_sceneIndexInTask == -1)
        {
            return _shuffledSequences[_taskIndex].Count > 0 ? _shuffledSequences[_taskIndex][0] : null;
        }

        int nextSceneIndex = _sceneIndexInTask + 1;
        if (nextSceneIndex < _shuffledSequences[_taskIndex].Count)
        {
            return _shuffledSequences[_taskIndex][nextSceneIndex];
        }

        if (!string.IsNullOrEmpty(_surveySceneName))
        {
            return _surveySceneName;
        }

        return GetTaskInterludeOrCompletion(_taskIndex + 1);
    }

    private string GetFirstSceneName()
    {
        if (!string.IsNullOrEmpty(_tutorialSceneName))
        {
            return _tutorialSceneName;
        }

        if (_tasks != null && _tasks.Count > 0)
        {
            return _tasks[0].interludeSceneName;
        }

        return null;
    }

    private string GetUpcomingSceneName()
    {
        if (_tasks == null || _tasks.Count == 0)
        {
            return null;
        }

        if (_taskIndex == -2)
        {
            return _tasks[0].interludeSceneName;
        }

        if (_inSurvey)
        {
            return GetTaskInterludeOrCompletion(_taskIndex + 1);
        }

        if (_shuffledSequences == null || _taskIndex < 0)
        {
            return GetFirstSceneName();
        }

        if (_taskIndex >= _shuffledSequences.Count)
        {
            return null;
        }

        if (_sceneIndexInTask == -1)
        {
            return _shuffledSequences[_taskIndex].Count > 0 ? _shuffledSequences[_taskIndex][0] : null;
        }

        int nextSceneIndex = _sceneIndexInTask + 1;
        if (nextSceneIndex < _shuffledSequences[_taskIndex].Count)
        {
            return _shuffledSequences[_taskIndex][nextSceneIndex];
        }

        if (!string.IsNullOrEmpty(_surveySceneName))
        {
            return _surveySceneName;
        }

        return GetTaskInterludeOrCompletion(_taskIndex + 1);
    }

    private string GetTaskInterludeOrCompletion(int taskIndex)
    {
        if (taskIndex < _tasks.Count)
        {
            return _tasks[taskIndex].interludeSceneName;
        }

        return "StudyComplete";
    }

    private void OnDestroy()
    {
        RestoreBackgroundLoadingPriority();

        if (_confirmDialogRuntimePanelSettings != null)
        {
            Destroy(_confirmDialogRuntimePanelSettings);
            _confirmDialogRuntimePanelSettings = null;
        }

        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
    }
}
