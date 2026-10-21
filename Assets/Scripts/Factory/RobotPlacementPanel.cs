using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Factory
{
    // World-space UI Toolkit panel for the robot placement phase: nudge / turn / reset the
    // placement handle, read the reach-marker summary, confirm; afterwards a single button to go
    // back. Same UIDocument/XRI stack as the door menu (planning-menu prefab with
    // RobotPlacementPanel.uxml as the source asset).
    public class RobotPlacementPanel : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private RobotPlacementController controller;

        private VisualElement root;
        private VisualElement editPanel, confirmedPanel;
        private Label statusLabel, warningLabel, repositionHint;
        private Button nudgeLeft, nudgeRight, nudgeToward, nudgeAway, turnLeft, turnRight, resetButton, confirmButton, repositionButton;
        private IVisualElementScheduledItem poll;
        private Coroutine bindWhenReady;

        public RobotPlacementController Controller => controller;

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
            Unbind();
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
            editPanel = root.Q<VisualElement>("placementEditPanel");
            confirmedPanel = root.Q<VisualElement>("placementConfirmedPanel");
            statusLabel = root.Q<Label>("placementStatusLabel");
            warningLabel = root.Q<Label>("placementWarningLabel");
            repositionHint = root.Q<Label>("placementRepositionHint");
            nudgeLeft = root.Q<Button>("placementNudgeLeftButton");
            nudgeRight = root.Q<Button>("placementNudgeRightButton");
            nudgeToward = root.Q<Button>("placementNudgeTowardButton");
            nudgeAway = root.Q<Button>("placementNudgeAwayButton");
            turnLeft = root.Q<Button>("placementTurnLeftButton");
            turnRight = root.Q<Button>("placementTurnRightButton");
            resetButton = root.Q<Button>("placementResetButton");
            confirmButton = root.Q<Button>("placementConfirmButton");
            repositionButton = root.Q<Button>("placementRepositionButton");

            // The visitor stands at the origin facing +z with the machine beyond the robot, so
            // "left" is world -x, "toward the machine" is world +z.
            if (nudgeLeft != null) nudgeLeft.clicked += OnNudgeLeft;
            if (nudgeRight != null) nudgeRight.clicked += OnNudgeRight;
            if (nudgeToward != null) nudgeToward.clicked += OnNudgeToward;
            if (nudgeAway != null) nudgeAway.clicked += OnNudgeAway;
            if (turnLeft != null) turnLeft.clicked += OnTurnLeft;
            if (turnRight != null) turnRight.clicked += OnTurnRight;
            if (resetButton != null) resetButton.clicked += OnReset;
            if (confirmButton != null) confirmButton.clicked += OnConfirm;
            if (repositionButton != null) repositionButton.clicked += OnReposition;
            if (controller != null)
            {
                controller.PhaseChanged += OnPhaseChanged;
                if (controller.PickPlaceClient != null)
                {
                    controller.PickPlaceClient.OnStatus += OnClientStatus;
                    controller.PickPlaceClient.OnTaskReset += Refresh;
                }
            }
            // Marker counts and the grab state change without events; a light poll keeps the text live.
            poll = root.schedule.Execute(Refresh).Every(250);
            Refresh();
        }

        private void Unbind()
        {
            if (nudgeLeft != null) nudgeLeft.clicked -= OnNudgeLeft;
            if (nudgeRight != null) nudgeRight.clicked -= OnNudgeRight;
            if (nudgeToward != null) nudgeToward.clicked -= OnNudgeToward;
            if (nudgeAway != null) nudgeAway.clicked -= OnNudgeAway;
            if (turnLeft != null) turnLeft.clicked -= OnTurnLeft;
            if (turnRight != null) turnRight.clicked -= OnTurnRight;
            if (resetButton != null) resetButton.clicked -= OnReset;
            if (confirmButton != null) confirmButton.clicked -= OnConfirm;
            if (repositionButton != null) repositionButton.clicked -= OnReposition;
            if (controller != null)
            {
                controller.PhaseChanged -= OnPhaseChanged;
                if (controller.PickPlaceClient != null)
                {
                    controller.PickPlaceClient.OnStatus -= OnClientStatus;
                    controller.PickPlaceClient.OnTaskReset -= Refresh;
                }
            }
            poll?.Pause();
            poll = null;
            nudgeLeft = nudgeRight = nudgeToward = nudgeAway = turnLeft = turnRight = resetButton = confirmButton = repositionButton = null;
        }

        private void OnNudgeLeft() => controller?.NudgeStep(-1f, 0f);
        private void OnNudgeRight() => controller?.NudgeStep(1f, 0f);
        private void OnNudgeToward() => controller?.NudgeStep(0f, 1f);
        private void OnNudgeAway() => controller?.NudgeStep(0f, -1f);
        private void OnTurnLeft() => controller?.RotateStep(-1f);
        private void OnTurnRight() => controller?.RotateStep(1f);
        private void OnReset() => controller?.ResetToStart();
        private void OnConfirm() { controller?.Confirm(); Refresh(); }
        private void OnReposition() { controller?.BeginReposition(); Refresh(); }
        private void OnPhaseChanged(RobotPlacementController.PlacementPhase _) => Refresh();
        private void OnClientStatus(string _) => Refresh();

        private void Refresh()
        {
            if (controller == null)
            {
                if (statusLabel != null) statusLabel.text = "No placement controller assigned";
                editPanel?.SetEnabled(false);
                confirmedPanel?.SetEnabled(false);
                return;
            }
            bool placing = controller.Phase == RobotPlacementController.PlacementPhase.Placing;
            if (editPanel != null) editPanel.style.display = placing ? DisplayStyle.Flex : DisplayStyle.None;
            if (confirmedPanel != null) confirmedPanel.style.display = placing ? DisplayStyle.None : DisplayStyle.Flex;

            ReachTargetMarkerSet markers = controller.Markers;
            string reach = markers != null && markers.Count > 0 ? $"{markers.ReachableCount} / {markers.Count} targets reachable" : "";
            if (statusLabel != null)
            {
                string phase = placing ? (controller.HandleGrabbed ? "Moving the robot..." : "Move the robot to where it can reach everything") : "Robot placed";
                statusLabel.text = string.IsNullOrEmpty(reach) ? phase : $"{phase}\n{reach}";
            }
            if (warningLabel != null)
            {
                List<string> warnings = controller.Warnings();
                warningLabel.text = warnings.Count > 0 ? string.Join("\n", warnings) : "All targets reachable top-down";
            }
            bool canEdit = placing && !controller.HandleGrabbed;
            nudgeLeft?.SetEnabled(canEdit);
            nudgeRight?.SetEnabled(canEdit);
            nudgeToward?.SetEnabled(canEdit);
            nudgeAway?.SetEnabled(canEdit);
            turnLeft?.SetEnabled(canEdit);
            turnRight?.SetEnabled(canEdit);
            resetButton?.SetEnabled(canEdit);
            confirmButton?.SetEnabled(controller.CanConfirm);
            repositionButton?.SetEnabled(controller.CanReposition);
            if (repositionHint != null)
                repositionHint.text = controller.CanReposition ? "Moves the robot again; the planning scene is re-sent on confirm."
                                                              : "Available once no plan is recording or executing.";
        }
    }
}
