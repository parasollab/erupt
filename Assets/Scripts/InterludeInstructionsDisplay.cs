using UnityEngine;
using UnityEngine.UIElements;

public class InterludeInstructionsDisplay : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    [TextArea(3, 10)]
    [SerializeField] private string instructionsText = "Instructions go here.";

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

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
}
