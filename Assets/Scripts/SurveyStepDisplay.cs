using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

// Steps the participant through the post-task NASA-TLX survey shown in the shared Survey
// scene, mirroring TutorialStepDisplay's B-button paging. The participant answers verbally
// to the study administrator; nothing is recorded here beyond the scene timing StudyLogger
// already captures. The final press hands control back to StudyController.FinishSurvey().
public class SurveyStepDisplay : MonoBehaviour
{
    private struct Step
    {
        public string Heading;
        public string Body;

        public Step(string heading, string body)
        {
            Heading = heading;
            Body = body;
        }
    }

    // Content is fixed for the one Survey scene (the same questions follow every task
    // block), so it's hardcoded here like TutorialStepDisplay's steps.
    private static readonly Step[] Steps =
    {
        new Step(
            "Survey",
            "You have finished all the scenes for this task. Now, you will be shown a series of questions. Please tell the study administrator that you are about to begin the survey and then verbally tell them your answer to the following questions.\n\nPress the 'B' button to advance to the first question."),
        new Step(
            "Mental Demand",
            "How much mental and perceptual activity was required (e.g. thinking, deciding, calculating, remembering, looking, searching, etc.)? Was the task easy or demanding, simple or complex, exacting or forgiving?\n\nPlease rate on a scale from 0 (Low) to 100 (High).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to advance to the next question."),
        new Step(
            "Physical Demand",
            "How much physical activity was required (e.g., pushing, pulling, turning, controlling, activating, etc.)? Was the task easy or demanding, slow or brisk, slack or strenuous, restful or laborious?\n\nPlease rate on a scale from 0 (Low) to 100 (High).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to advance to the next question."),
        new Step(
            "Temporal Demand",
            "How much time pressure did you feel due to the rate or pace at which the tasks or task elements occurred? Was the pace slow and leisurely or rapid and frantic?\n\nPlease rate on a scale from 0 (Low) to 100 (High).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to advance to the next question."),
        new Step(
            "Performance",
            "How successful do you think you were in accomplishing the goals of the task set by the experimenter (or yourself)? How satisfied were you with your performance in accomplishing these goals?\n\nPlease rate on a scale from 0 (Good) to 100 (Poor).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to advance to the next question."),
        new Step(
            "Effort",
            "How hard did you have to work (mentally and physically) to accomplish your level of performance?\n\nPlease rate on a scale from 0 (Low) to 100 (High).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to advance to the next question."),
        new Step(
            "Frustration Level",
            "How insecure, discouraged, irritated, stressed and annoyed versus secure, gratified, content, relaxed and complacent did you feel during the task?\n\nPlease rate on a scale from 0 (Low) to 100 (High).\n\nPlease verbally tell the study administrator your answer. Then, press 'B' to continue the study.")
    };

    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private InputActionReference advanceAction;

    private Label titleLabel;
    private Label stepCounterLabel;
    private Label bodyLabel;
    private int stepIndex;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument?.rootVisualElement;
        titleLabel = root?.Q<Label>("instructionsTitleLabel");
        stepCounterLabel = root?.Q<Label>("stepCounterLabel");
        bodyLabel = root?.Q<Label>("instructionsLabel");

        // The Survey scene reuses Tutorial.uxml, whose title defaults to "Tutorial".
        if (titleLabel != null)
            titleLabel.text = "Survey";

        stepIndex = 0;
        ShowCurrentStep();

        if (advanceAction != null)
        {
            advanceAction.action.performed += OnAdvancePressed;
            advanceAction.action.Enable();
        }
        else
        {
            Debug.LogError("SurveyStepDisplay: Advance action is not assigned.");
        }
    }

    private void OnAdvancePressed(InputAction.CallbackContext context)
    {
        stepIndex++;
        if (stepIndex < Steps.Length)
        {
            ShowCurrentStep();
            return;
        }

        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }

        if (StudyController.Instance != null)
        {
            StudyController.Instance.FinishSurvey();
        }
        else
        {
            Debug.LogError("SurveyStepDisplay: No StudyController found to finish the survey.");
        }
    }

    private void ShowCurrentStep()
    {
        Step step = Steps[stepIndex];
        if (stepCounterLabel != null)
        {
            // Step 0 is the intro; the remaining steps are the 6 survey questions.
            stepCounterLabel.text = stepIndex == 0
                ? ""
                : $"Question {stepIndex} of {Steps.Length - 1} — {step.Heading}";
        }
        if (bodyLabel != null)
            bodyLabel.text = step.Body;
    }

    private void OnDisable()
    {
        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }
    }
}
