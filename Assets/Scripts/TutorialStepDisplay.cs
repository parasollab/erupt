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
            "Press the small Menu button on your left controller (just below the thumbstick) to open your wrist menu. Choose Add Shape using the trigger on the right controller, then Cube, to spawn a cube in front of you.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "Objects",
            "Point your right controller at the cube and pull the trigger to select it. Then squeeze the grip button — the button under your middle finger, on the side of the controller — on either controller to grab it. Move the controller to move the cube, and release the grip to let go.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "Wrist Menu (again)",
            "Press the small Menu button on your left controller to open your wrist menu again if it isn't already open. Choose Add Shape, then Cylinder, to spawn a practice cylinder in front of you. Point the right controller at the cylinder and pull the trigger to select it. Press Edit Shape on the wrist menu and edit the scale of each dimension.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "Wrist Menu (one more time)",
            "Point the controller at the cube and press the trigger to select it. Open the wrist menu if needed (go back to the main menu if not already there) and press Delete Shape to delete the cube. Repeat with the Cylinder.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "Robot",
            "Point your right controller at the blue sphere near the robot's wrist and hold the grip button (the one under your middle finger) to drag it — the robot will follow. You can also point at one of the robot's joints and pull the trigger to select it, then push the right thumbstick up or down to rotate that joint directly.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "Planning Request Menu",
            "Grab the floating panel by the indicator bar along its bottom edgeto bring it closer, or just point and pull the trigger on its buttons from where you stand. Choose Set Start State to record the robot's current pose, move the robot to a new pose, then choose Set Goal State. Choose Send Request to plan a path between them.\n\nPress 'B' on your right controller to advance to the next step."),
        new Step(
            "End of Tutorial", 
            "You have completed the tutorial. Feel free to practice and explore the controls until you are ready to begin the study. Press 'B' on your right controller to begin the study.")
    };

    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private InputActionReference advanceAction;

    private Label stepCounterLabel;
    private Label bodyLabel;
    private UnityEngine.UI.Text uguiStepCounterLabel;
    private UnityEngine.UI.Text uguiBodyLabel;
    private int stepIndex;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        if (VisionOSSampleControlsUI.ShouldUseRuntimeUGUI() || uiDocument == null)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            EnsureUGUITutorialPanel();
        }
        else
        {
            VisualElement root = uiDocument?.rootVisualElement;
            stepCounterLabel = root?.Q<Label>("stepCounterLabel");
            bodyLabel = root?.Q<Label>("instructionsLabel");
        }

        stepIndex = 0;
        ShowCurrentStep();

        if (advanceAction != null)
        {
            advanceAction.action.performed += OnAdvancePressed;
            advanceAction.action.Enable();
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

        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }

        if (StudyController.Instance != null)
        {
            StudyController.Instance.FinishTutorial();
        }
        else
        {
            Debug.LogError("TutorialStepDisplay: No StudyController found to finish the tutorial.");
        }
    }

    private void ShowCurrentStep()
    {
        Step step = Steps[stepIndex];
        string counterText = $"Step {stepIndex + 1} of {Steps.Length} — {step.Heading}";
        if (stepCounterLabel != null)
            stepCounterLabel.text = counterText;
        if (bodyLabel != null)
            bodyLabel.text = step.Body;
        if (uguiStepCounterLabel != null)
            uguiStepCounterLabel.text = counterText;
        if (uguiBodyLabel != null)
            uguiBodyLabel.text = step.Body;
    }

    private void OnDisable()
    {
        if (advanceAction != null)
        {
            advanceAction.action.performed -= OnAdvancePressed;
        }
    }

    private void EnsureUGUITutorialPanel()
    {
        Canvas canvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS Tutorial Instructions Canvas",
            new Vector2(1050f, 800f),
            new Vector2(1.05f, 0.8f),
            new Vector3(0f, -0.1f, 0f),
            sortingOrder: 120);

        VisionOSSampleControlsUI.ClearChildren(canvas.transform);
        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            canvas.transform,
            "Tutorial Instructions Panel",
            new Vector2(1050f, 800f));

        uguiStepCounterLabel = VisionOSSampleControlsUI.CreateText(
            panel.transform,
            "",
            45,
            TextAnchor.MiddleLeft,
            VisionOSSampleControlsUI.TextColor,
            100f);

        uguiBodyLabel = VisionOSSampleControlsUI.CreateText(
            panel.transform,
            "",
            32,
            TextAnchor.UpperLeft,
            VisionOSSampleControlsUI.TextColor,
            570f);
    }
}
