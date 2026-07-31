using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
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
        BeginScenePreloadIfEnabled(GetFirstSceneName());
        Invoke(nameof(BeginStudy), _startSceneAutoAdvanceDelay);
    }

    // Leaves StartScene automatically once settled, instead of waiting for a button press.
    private void BeginStudy()
    {
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
            return;
        }

        string nextSceneName = GetAdvanceTargetSceneName();
        if (!TryCommitSceneTransition(nextSceneName))
        {
            return;
        }

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

    // Called by the survey scene once the participant has stepped through every question,
    // to move on to the next task's interlude (or the completion scene after the last task).
    public bool FinishSurvey()
    {
        if (!TryCommitSceneTransition(GetTaskInterludeOrCompletion(_taskIndex + 1)))
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

        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
    }
}
