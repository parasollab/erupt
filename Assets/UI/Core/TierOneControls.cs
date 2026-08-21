using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>Shows the current mode and cycles it. Guidelines Part 4.</summary>
    /// <remarks>
    /// Part 4 requires the mode to be visible and one action from changing. This control
    /// is both halves, which is why it is a single tier 1 slot rather than two.
    /// </remarks>
    public class ModeIndicatorControl : MonoBehaviour, ITierOneControl
    {
        public UiTier Tier => UiTier.Persistent;
        public string Id => "mode";

        private ModeManager modes;
        private Button button;

        public void Initialise(ModeManager modeManager, Button target)
        {
            modes = modeManager;
            button = target;

            button.onClick.AddListener(OnClick);
            if (modes != null) modes.ModeChanged += OnModeChanged;

            Refresh();
        }

        private void OnClick() => modes?.Cycle();

        private void OnModeChanged(AppMode _) => Refresh();

        private void Refresh()
        {
            if (button == null) return;
            UiBuilder.SetButtonText(button, modes != null ? modes.Current.ToString() : "—");
        }

        private void OnDestroy()
        {
            if (modes != null) modes.ModeChanged -= OnModeChanged;
            if (button != null) button.onClick.RemoveListener(OnClick);
        }
    }

    /// <summary>Undo, and its enabled state. Guidelines Part 2 tier 1.</summary>
    public class UndoControl : MonoBehaviour, ITierOneControl
    {
        public UiTier Tier => UiTier.Persistent;
        public string Id => "undo";

        private UndoStack stack;
        private Button button;

        public void Initialise(UndoStack undoStack, Button target)
        {
            stack = undoStack;
            button = target;

            button.onClick.AddListener(OnClick);
            if (stack != null) stack.Changed += Refresh;

            Refresh();
        }

        private void OnClick() => stack?.Undo();

        private void Refresh()
        {
            if (button == null) return;
            UiBuilder.SetInteractable(button, stack != null && stack.CanUndo);
        }

        private void OnDestroy()
        {
            if (stack != null) stack.Changed -= Refresh;
            if (button != null) button.onClick.RemoveListener(OnClick);
        }
    }

    /// <summary>Redo, and its enabled state.</summary>
    public class RedoControl : MonoBehaviour, ITierOneControl
    {
        public UiTier Tier => UiTier.Persistent;
        public string Id => "redo";

        private UndoStack stack;
        private Button button;

        public void Initialise(UndoStack undoStack, Button target)
        {
            stack = undoStack;
            button = target;

            button.onClick.AddListener(OnClick);
            if (stack != null) stack.Changed += Refresh;

            Refresh();
        }

        private void OnClick() => stack?.Redo();

        private void Refresh()
        {
            if (button == null) return;
            UiBuilder.SetInteractable(button, stack != null && stack.CanRedo);
        }

        private void OnDestroy()
        {
            if (stack != null) stack.Changed -= Refresh;
            if (button != null) button.onClick.RemoveListener(OnClick);
        }
    }

    /// <summary>
    /// Placeholder holding tier 1's fourth slot for voice, which arrives in Phase 3.
    /// </summary>
    /// <remarks>
    /// Registered so the cap is enforced against the real intended contents. Without it,
    /// a fifth control could be added now and only fail once voice lands.
    /// </remarks>
    public class VoiceControlPlaceholder : MonoBehaviour, ITierOneControl
    {
        public UiTier Tier => UiTier.Persistent;
        public string Id => "voice";
    }
}
