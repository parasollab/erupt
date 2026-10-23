using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    /// <summary>
    /// The instruction card: a world-space UI Toolkit panel (GuideCard.uxml on the planning-menu
    /// prefab recipe, kept in view by <see cref="GuideHeadFollow"/>) showing the current step's
    /// number, title, one-sentence instruction, which controller button to use, the extra hint
    /// once the user has been on the step for a while, and the last notice. It only reads from
    /// <see cref="DemoGuide"/>.
    /// </summary>
    public class GuideInstructionCard : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private DemoGuide guide;
        [SerializeField, Tooltip("Shown on the hint line after the second escalation.")]
        private string helpLine = "Stuck? Ask the person next to you.";

        private VisualElement root;
        private Label stepLabel, statusLabel, titleLabel, instructionLabel, controllerLabel, hintLabel, noticeLabel;
        private Coroutine bindWhenReady;

        public DemoGuide Guide => guide;
        public string InstructionText => instructionLabel != null ? instructionLabel.text : null;
        public string TitleText => titleLabel != null ? titleLabel.text : null;
        public bool HintVisible => hintLabel != null && hintLabel.style.display == DisplayStyle.Flex;

        private void OnEnable()
        {
            if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
            if (uiDocument == null) uiDocument = GetComponentInChildren<UIDocument>(true);
            root = uiDocument != null ? uiDocument.rootVisualElement : null;
            if (root == null)
            {
                bindWhenReady = StartCoroutine(BindWhenRootReady());
                return;
            }
            Bind();
        }

        private void OnDisable()
        {
            if (bindWhenReady != null) { StopCoroutine(bindWhenReady); bindWhenReady = null; }
            if (guide != null) guide.Changed -= Refresh;
        }

        private IEnumerator BindWhenRootReady()
        {
            while (uiDocument == null || uiDocument.rootVisualElement == null) yield return null;
            root = uiDocument.rootVisualElement;
            bindWhenReady = null;
            Bind();
        }

        private void Bind()
        {
            stepLabel = root.Q<Label>("guideStepLabel");
            statusLabel = root.Q<Label>("guideStatusLabel");
            titleLabel = root.Q<Label>("guideTitleLabel");
            instructionLabel = root.Q<Label>("guideInstructionLabel");
            controllerLabel = root.Q<Label>("guideControllerLabel");
            hintLabel = root.Q<Label>("guideHintLabel");
            noticeLabel = root.Q<Label>("guideNoticeLabel");
            if (guide != null)
            {
                guide.Changed -= Refresh;
                guide.Changed += Refresh;
            }
            Refresh();
        }

        public void Refresh()
        {
            if (root == null) return;
            if (guide == null)
            {
                Set(titleLabel, "No guide assigned");
                return;
            }
            GuideStep step = guide.Step;
            GuideText text = guide.Text;
            int number = DemoGuideSteps.NumberOf(step);
            Set(stepLabel, number > 0 ? $"STEP {number} OF {DemoGuideSteps.StepCount}" : "DONE");
            Set(statusLabel, guide.RobotOnline ? "" : "ROBOT OFFLINE");
            Set(titleLabel, text.title);
            Set(instructionLabel, text.instruction);
            Set(controllerLabel, text.controller);

            string hint = "";
            if (guide.Escalation >= 1 && !string.IsNullOrEmpty(text.hint)) hint = text.hint;
            if (guide.Escalation >= 2) hint = string.IsNullOrEmpty(hint) ? helpLine : hint + "\n" + helpLine;
            Set(hintLabel, hint);
            Set(noticeLabel, guide.Notice ?? "");
        }

        private static void Set(Label label, string text)
        {
            if (label == null) return;
            label.text = text ?? "";
            label.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
