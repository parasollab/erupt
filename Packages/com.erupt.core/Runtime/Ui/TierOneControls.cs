using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>Opens and closes the tier 3 panel. Guidelines Part 2 tier 1.</summary>
    /// <remarks>
    /// The same thing the controller's Menu button, the hand menu gesture and the desktop
    /// panel key do through the router's <c>Activate</c> intent; this is the one the user can
    /// see, so nothing has to be known beforehand to find the panel.
    /// </remarks>
    public class MenuControl : MonoBehaviour, ITierOneControl
    {
        public UiTier Tier => UiTier.Persistent;
        public string Id => "menu";

        private Button button;

        /// <summary>The button was pressed. The rig that owns the panel toggles it.</summary>
        public event System.Action Pressed;

        public void Initialise(Button target)
        {
            button = target;
            button.onClick.AddListener(OnClick);
        }

        private void OnClick() => Pressed?.Invoke();

        private void OnDestroy()
        {
            Pressed = null;
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
