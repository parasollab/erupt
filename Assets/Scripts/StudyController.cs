using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using RosMessageTypes.StudyInterfaces;

public class StudyController : MonoBehaviour
{
    public static StudyController Instance { get; private set; }

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
    [Tooltip("Optional pause before auto-advancing from StartScene into Task1's interlude.")]
    [SerializeField] private float _startSceneAutoAdvanceDelay = 0f;

    [Header("ROS Study Plan")]
    [Tooltip("How long to wait for a /study/plan message before falling back to local random generation (e.g. when running standalone without ROS).")]
    [SerializeField] private float _rosPlanTimeoutSeconds = 3f;

    [Header("Task Configuration")]
    [SerializeField] private TaskConfig _task1 = new TaskConfig();
    [SerializeField] private TaskConfig _task2 = new TaskConfig();
    [SerializeField] private TaskConfig _task3 = new TaskConfig();

    // Runtime state. We track progress with explicit indices rather than
    // inferring it from the active scene name, since these indices live on
    // this DontDestroyOnLoad object and are trivially authoritative across
    // scene loads.
    private List<TaskConfig> _tasks;
    private List<List<string>> _shuffledSequences;
    private int _taskIndex = -1;       // -1 = still in StartScene, before Task1's interlude
    private int _sceneIndexInTask = -1; // -1 = currently on the current task's interlude

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _tasks = new List<TaskConfig> { _task1, _task2, _task3 };

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
        if (_shuffledSequences == null || _taskIndex < 0)
        {
            // Still settling in StartScene (auto-advance hasn't fired yet) - ignore.
            return;
        }

        // On the current task's interlude: move into its first task scene.
        if (_sceneIndexInTask == -1)
        {
            _sceneIndexInTask = 0;
            LoadCurrentTaskScene();
            return;
        }

        // On a task scene: advance to the next one, or roll over to the next task.
        _sceneIndexInTask++;
        if (_sceneIndexInTask < _shuffledSequences[_taskIndex].Count)
        {
            LoadCurrentTaskScene();
            return;
        }

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
        Debug.Log("StudyController: Study complete.");
        if (_advanceAction != null)
        {
            _advanceAction.action.performed -= OnAdvancePressed;
        }
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
