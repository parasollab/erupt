using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public class TutorialStepDisplay : MonoBehaviour
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

    // Content is fixed for the one Tutorial scene, so it's hardcoded here rather than
    // serialized per-scene like InterludeInstructionsDisplay's per-task instructionsText.
    private static readonly Step[] Steps =
    {
        new Step(
            "Wrist Menu",
            "Press the small Menu button on your left controller (just below the thumbstick) to open your wrist menu. Choose Add Shape using the trigger on the right controller, then Cube, to spawn a cube in front of you.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "Objects",
            "Point your right controller at the cube and pull the trigger to select it. Then squeeze the grip button — the button under your middle finger, on the side of the controller — on either controller to grab it. Move the controller to move the cube, and release the grip to let go.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "Wrist Menu (again)",
            "Press the small Menu button on your left controller to open your wrist menu again if it isn't already open. Choose Add Shape, then Cylinder, to spawn a practice cylinder in front of you. Point the right controller at the cylinder and pull the trigger to select it. Press Edit Shape on the wrist menu and edit the scale of each dimension.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "Wrist Menu (one more time)",
            "Point the controller at the cube and press the trigger to select it. Open the wrist menu if needed (go back to the main menu if not already there) and press Delete Shape to delete the cube. Repeat with the Cylinder.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "Robot",
            "Point your right controller at the blue sphere near the robot's wrist and hold the grip button (the one under your middle finger) to drag it — the robot will follow. You can also point at one of the robot's joints and pull the trigger to select it, then push the right thumbstick up or down to rotate that joint directly.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "Planning Request Menu",
            "Grab the floating panel by the indicator bar along its bottom edgeto bring it closer, or just point and pull the trigger on its buttons from where you stand. Choose Set Start State to record the robot's current pose, move the robot to a new pose, then choose Set Goal State. Choose Send Request to plan a path between them.\n\nPress 'B' on your right controller to advance to the next step, or 'A' to go back a step."),
        new Step(
            "End of Tutorial",
            "You have completed the tutorial. Feel free to practice and explore the controls until you are ready to begin the study. You can press 'Y' on your left controller at any time to return to your starting position.\n\nPress 'B' on your right controller to begin the study, or 'A' to go back a step.")
    };

    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private InputActionReference advanceAction;

    // Resolved from advanceAction's action map by name rather than serialized, so no scene
    // rewiring is needed. Steps back to the previous tutorial step (A button / backspace).
    private InputAction goBackAction;

    private Label stepCounterLabel;
    private Label bodyLabel;
    private int stepIndex;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument?.rootVisualElement;
        stepCounterLabel = root?.Q<Label>("stepCounterLabel");
        bodyLabel = root?.Q<Label>("instructionsLabel");

        stepIndex = 0;
        ShowCurrentStep();

        if (advanceAction != null)
        {
            advanceAction.action.performed += OnAdvancePressed;
            advanceAction.action.Enable();

            goBackAction = advanceAction.action.actionMap?.FindAction("GoBackStudy");
            if (goBackAction != null)
            {
                goBackAction.performed += OnGoBackPressed;
                goBackAction.Enable();
            }
        }
        else
        {
            Debug.LogError("TutorialStepDisplay: Advance action is not assigned.");
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

        if (StudyController.Instance != null)
        {
            if (!StudyController.Instance.FinishTutorial())
            {
                stepIndex = Steps.Length - 1;
                return;
            }
        }
        else
        {
            Debug.LogError("TutorialStepDisplay: No StudyController found to finish the tutorial.");
            stepIndex = Steps.Length - 1;
            return;
        }

        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }
    }

    private void OnGoBackPressed(InputAction.CallbackContext context)
    {
        // The tutorial is the first stop in the study, so there is nowhere to go back to
        // from the first step.
        if (stepIndex == 0)
            return;

        stepIndex--;
        ShowCurrentStep();
    }

    private void ShowCurrentStep()
    {
        Step step = Steps[stepIndex];
        if (stepCounterLabel != null)
            stepCounterLabel.text = $"Step {stepIndex + 1} of {Steps.Length} — {step.Heading}";
        if (bodyLabel != null)
            bodyLabel.text = step.Body;
    }

    private void OnDisable()
    {
        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }
        if (goBackAction != null)
        {
            goBackAction.performed -= OnGoBackPressed;
        }
    }
}
