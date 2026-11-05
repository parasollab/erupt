using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    /// <summary>
    /// The instruction card: a world-space UI Toolkit panel (GuideCard.uxml on the planning-menu
    /// prefab recipe, kept in view by <see cref="GuideHeadFollow"/>) showing the current step's
    /// number, title, one-sentence instruction, which controller button to use, the extra hint
    /// once the user has been on the step for a while, and the last notice. On the welcome step it
    /// also shows the renderings of both controllers (trigger, grip and thumbstick ringed) with a
    /// legend. The drawn root is sized to its content every time the layout changes, between
    /// <see cref="minHeightUnits"/> and the document's baked height (<see cref="maxHeightUnits"/>), so
    /// no step's text is cut off (2026-11-05); the document is shifted so that the drawn part stays
    /// centred on the root, and its lower part is empty and invisible (resizing a world-space
    /// document itself at runtime left its renderer clipping stale regions, 2026-11-04). It only
    /// reads from <see cref="DemoGuide"/>.
    /// </summary>
    public class GuideInstructionCard : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private DemoGuide guide;
        [SerializeField, Tooltip("Shown on the hint line after the second escalation.")]
        private string helpLine = "Stuck? Ask the demonstration administrator.";

        [Header("Welcome step: the controllers")]
        [SerializeField, Tooltip("Rendering of the left controller (trigger, grip and thumbstick ringed in the legend's colours); baked by the scene builder.")]
        private Texture2D leftControllerImage;
        [SerializeField, Tooltip("Rendering of the right controller.")]
        private Texture2D rightControllerImage;
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("normalHeightUnits"),
         Tooltip("Smallest height of the drawn card (units of the 300-wide document); also the height the builder centred the document for.")]
        private float minHeightUnits = 320f;
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("welcomeHeightUnits"),
         Tooltip("Largest height of the drawn card: the document's own baked height.")]
        private float maxHeightUnits = 520f;
        [SerializeField, Tooltip("The head-follow on the card's root; its occlusion check gets the drawn card's half extents per step. Found in the parents when empty.")]
        private GuideHeadFollow follow;
        [SerializeField, Tooltip("Metres per document unit (the panel prefab's 0.2 scale x 0.01).")]
        private float metresPerUnit = 0.002f;

        private VisualElement root, cardRoot, controllersBlock, leftControllerView, rightControllerView;
        private Label stepLabel, statusLabel, titleLabel, instructionLabel, controllerLabel, hintLabel, noticeLabel;
        private Coroutine bindWhenReady;
        private Vector3 normalLocalPosition;
        private bool positionCaptured;
        private float appliedHeight = -1f;

        public DemoGuide Guide => guide;
        public UIDocument Document => uiDocument;
        public string InstructionText => instructionLabel != null ? instructionLabel.text : null;
        public string TitleText => titleLabel != null ? titleLabel.text : null;
        public bool HintVisible => hintLabel != null && hintLabel.style.display == DisplayStyle.Flex;
        /// <summary>The controller renderings and their legend are on the card (welcome step).</summary>
        public bool ControllersVisible => controllersBlock != null && controllersBlock.style.display == DisplayStyle.Flex;
        public bool ControllerImagesAssigned => leftControllerImage != null && rightControllerImage != null;
        /// <summary>Smallest height of the drawn card, in document units.</summary>
        public float MinHeightUnits => minHeightUnits;
        /// <summary>Largest height of the drawn card (the baked document), in document units.</summary>
        public float MaxHeightUnits => maxHeightUnits;
        /// <summary>Height the drawn card currently has, in document units.</summary>
        public float RootHeightUnits => cardRoot != null ? cardRoot.resolvedStyle.height : 0f;
        /// <summary>Height the current content needs (last visible row's bottom plus the padding and border), in document units.</summary>
        public float ContentHeightUnits => cardRoot != null ? MeasureContent() : 0f;
        /// <summary>Every visible row lies inside the drawn card.</summary>
        public bool ContentFits => cardRoot != null && ContentHeightUnits <= RootHeightUnits + 0.5f;

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
            cardRoot = root.Q<VisualElement>("guideRoot");
            if (follow == null) follow = GetComponentInParent<GuideHeadFollow>(true);
            controllersBlock = root.Q<VisualElement>("guideControllersBlock");
            leftControllerView = root.Q<VisualElement>("guideControllerLeftImage");
            rightControllerView = root.Q<VisualElement>("guideControllerRightImage");
            if (leftControllerView != null && leftControllerImage != null) leftControllerView.style.backgroundImage = new StyleBackground(leftControllerImage);
            if (rightControllerView != null && rightControllerImage != null) rightControllerView.style.backgroundImage = new StyleBackground(rightControllerImage);
            if (!positionCaptured && uiDocument != null)
            {
                // The builder places the document so that its top minHeightUnits are centred on the root.
                normalLocalPosition = uiDocument.transform.localPosition;
                positionCaptured = true;
            }
            // Any row's layout change (new text, a hint appearing, the controllers block) re-fits the drawn height.
            if (cardRoot != null)
            {
                cardRoot.RegisterCallback<GeometryChangedEvent>(_ => FitHeight());
                foreach (VisualElement child in cardRoot.Children()) child.RegisterCallback<GeometryChangedEvent>(_ => FitHeight());
            }
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

            // The controllers (renderings + legend) sit under the welcome text and stand in for its controller line.
            bool showControllers = step == GuideStep.Welcome && controllersBlock != null && ControllerImagesAssigned;
            if (controllersBlock != null) controllersBlock.style.display = showControllers ? DisplayStyle.Flex : DisplayStyle.None;

            // At most one block sits under the instruction besides the help line: the controller
            // line while the step is fresh, the hint once the user has been on it a while, and a
            // notice (plan failed, part fell) in place of either. The height follows the content.
            bool hasNotice = !string.IsNullOrEmpty(guide.Notice);
            string hint = "";
            if (!hasNotice && guide.Escalation >= 1 && !string.IsNullOrEmpty(text.hint)) hint = text.hint;
            if (guide.Escalation >= 2) hint = string.IsNullOrEmpty(hint) ? helpLine : hint + "\n" + helpLine;
            Set(controllerLabel, hasNotice || guide.Escalation >= 1 || showControllers ? "" : text.controller);
            Set(hintLabel, hint);
            Set(noticeLabel, guide.Notice ?? "");
            FitHeight();
            if (cardRoot != null) cardRoot.schedule.Execute(FitHeight); // once more after the new text has been laid out
        }

        // Bottom of the lowest visible row plus the root's bottom padding and border; NaN before the first layout.
        private float MeasureContent()
        {
            float bottom = 0f;
            foreach (VisualElement child in cardRoot.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None) continue;
                Rect r = child.layout;
                if (float.IsNaN(r.yMax)) return float.NaN;
                bottom = Mathf.Max(bottom, r.yMax + child.resolvedStyle.marginBottom);
            }
            return bottom + cardRoot.resolvedStyle.paddingBottom + cardRoot.resolvedStyle.borderBottomWidth;
        }

        // The drawn root takes what its rows need, between the minimum and the baked document height; the
        // document moves up by half the difference to the minimum (1 unit = 0.01 local) so the drawn part
        // stays centred on the root, and the follow's occlusion check gets the drawn card's half extents.
        private void FitHeight()
        {
            if (cardRoot == null) return;
            float needed = MeasureContent();
            if (float.IsNaN(needed)) return;
            float height = Mathf.Clamp(needed, minHeightUnits, Mathf.Max(minHeightUnits, maxHeightUnits));
            if (Mathf.Abs(height - appliedHeight) < 0.5f) return;
            appliedHeight = height;
            cardRoot.style.flexGrow = 0f;
            cardRoot.style.height = height;
            if (follow != null && uiDocument != null)
                follow.halfExtents = new Vector2(uiDocument.worldSpaceSize.x, height) * (metresPerUnit * 0.5f);
            if (uiDocument != null && positionCaptured)
            {
                Vector3 p = normalLocalPosition;
                p.y += (height - minHeightUnits) / 200f;
                uiDocument.transform.localPosition = p;
            }
        }

        private static void Set(Label label, string text)
        {
            if (label == null) return;
            label.text = text ?? "";
            label.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
