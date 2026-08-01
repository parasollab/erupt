using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
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

    // Resolved from _advanceAction's action map by name rather than serialized, so no scene
    // rewiring is needed. Pressing it (the A button / backspace) steps the study backwards
    // to recover from an accidental advance; a revisited scene reloads from scratch.
    private InputAction _goBackAction;

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

            _goBackAction = _advanceAction.action.actionMap?.FindAction("GoBackStudy");
            if (_goBackAction != null)
            {
                _goBackAction.performed += OnGoBackPressed;
                _goBackAction.Enable();
            }
            else
            {
                Debug.LogError("StudyController: No 'GoBackStudy' action found alongside the advance action.");
            }
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
            // On StudyComplete - only the go-back action does anything here.
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
            SceneManager.LoadScene(_surveySceneName);
            return;
        }

        AdvanceToNextTask();
    }

    // Steps the study backwards one stop, to recover from an accidental advance. Scene
    // contents are not preserved: a revisited task scene reloads from scratch, and a
    // revisited survey restarts at its intro page.
    private void OnGoBackPressed(InputAction.CallbackContext context)
    {
        if (_taskIndex == -2)
        {
            // On the tutorial scene - TutorialStepDisplay owns this button press locally.
            return;
        }

        if (_inSurvey)
        {
            // On the survey scene - SurveyStepDisplay owns this button press locally and
            // calls BackOutOfSurvey() itself from the intro page.
            return;
        }

        if (_shuffledSequences == null || _taskIndex < 0)
        {
            // Still settling in StartScene - ignore.
            return;
        }

        // On StudyComplete: return to the last task's survey.
        if (_taskIndex >= _tasks.Count)
        {
            if (string.IsNullOrEmpty(_surveySceneName))
            {
                return;
            }
            _taskIndex = _tasks.Count - 1;
            _sceneIndexInTask = _shuffledSequences[_taskIndex].Count;
            _inSurvey = true;
            SceneManager.LoadScene(_surveySceneName);
            return;
        }

        // On the current task's interlude: return to the previous task's survey (or the
        // tutorial when this is the first task).
        if (_sceneIndexInTask == -1)
        {
            if (_taskIndex == 0)
            {
                if (!string.IsNullOrEmpty(_tutorialSceneName))
                {
                    _taskIndex = -2;
                    SceneManager.LoadScene(_tutorialSceneName);
                }
                return;
            }

            _taskIndex--;
            if (!string.IsNullOrEmpty(_surveySceneName))
            {
                _sceneIndexInTask = _shuffledSequences[_taskIndex].Count;
                _inSurvey = true;
                SceneManager.LoadScene(_surveySceneName);
            }
            else
            {
                _sceneIndexInTask = _shuffledSequences[_taskIndex].Count - 1;
                LoadCurrentTaskScene();
            }
            return;
        }

        // On a task's first scene: back out to its interlude.
        if (_sceneIndexInTask == 0)
        {
            _sceneIndexInTask = -1;
            LoadCurrentInterlude();
            return;
        }

        // On any later task scene: reload the previous scene (fresh - no state is kept).
        _sceneIndexInTask--;
        LoadCurrentTaskScene();
    }

    // Called by the survey scene once the participant has stepped through every question,
    // to move on to the next task's interlude (or the completion scene after the last task).
    public void FinishSurvey()
    {
        _inSurvey = false;
        AdvanceToNextTask();
    }

    // Called by the survey scene when the participant backs out of the survey's intro page,
    // to return to the current task's last scene (reloaded from scratch).
    public void BackOutOfSurvey()
    {
        if (!_inSurvey)
        {
            return;
        }
        _inSurvey = false;
        _sceneIndexInTask = _shuffledSequences[_taskIndex].Count - 1;
        LoadCurrentTaskScene();
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
        // Both actions stay subscribed: OnAdvancePressed ignores presses while complete,
        // and OnGoBackPressed can still return to the last task's survey in case the
        // participant skipped there by accident.
        Debug.Log("StudyController: Study complete.");
        SceneManager.LoadScene("StudyComplete");
    }

    private void OnDestroy()
    {
        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
        if (_goBackAction != null)
        {
            _goBackAction.performed -= OnGoBackPressed;
        }
    }
}
