using UnityEngine;
using UnityEngine.UIElements;

public class InterludeInstructionsDisplay : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    [TextArea(3, 10)]
    [SerializeField] private string instructionsText = "Instructions go here.";

    private UnityEngine.UI.Text uguiInstructionsLabel;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        if (VisionOSSampleControlsUI.ShouldUseRuntimeUGUI() || uiDocument == null)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            EnsureUGUIInstructions();
            return;
        }

        VisualElement root = uiDocument?.rootVisualElement;
        Label label = root?.Q<Label>("instructionsLabel");
        if (label != null)
        {
            label.text = instructionsText;
        }
        else
        {
            Debug.LogError("InterludeInstructionsDisplay: No 'instructionsLabel' found in UIDocument.");
        }
    }

    private void EnsureUGUIInstructions()
    {
        Canvas canvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS Instructions Canvas",
            new Vector2(1050f, 700f),
            new Vector2(1.05f, 0.7f),
            new Vector3(0f, -0.1f, 0f),
            sortingOrder: 120);

        VisionOSSampleControlsUI.ClearChildren(canvas.transform);
        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            canvas.transform,
            "Instructions Panel",
            new Vector2(1050f, 700f));

        uguiInstructionsLabel = VisionOSSampleControlsUI.CreateText(
            panel.transform,
            instructionsText,
            35,
            TextAnchor.UpperLeft,
            VisionOSSampleControlsUI.TextColor,
            560f);
    }
}
