using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// The always-visible bar: Menu, Undo, Redo. Guidelines Part 2 tier 1: four controls, no more.
    /// </summary>
    /// <remarks>
    /// Deliberately small. Tier 1 is wrist-anchored (Part 6), which is a tight space, so the
    /// cap is a design constraint rather than an inconvenience: there is nowhere for a fifth
    /// control to go. The canvas is as wide as its buttons (<see cref="SizeFor"/>).
    ///
    /// Building through UiTierRegistry means a fifth control throws at registration
    /// rather than quietly appearing.
    /// </remarks>
    public class TierOneBar : MonoBehaviour
    {
        /// <summary>The texts on the bar's buttons, in order.</summary>
        public const string MenuLabel = "Menu", UndoLabel = "Undo", RedoLabel = "Redo";

        [SerializeField] private Vector2 buttonSize = new(140f, 72f);
        [Tooltip("Gap between the buttons, in canvas units.")]
        [SerializeField, Min(0f)] private float spacing = 8f;
        [Tooltip("Margin round the buttons, in canvas units.")]
        [SerializeField, Min(0f)] private float padding = 12f;

        private UndoStack undoStack;

        public UiTierRegistry Registry { get; } = new();
        public UndoStack UndoStack => undoStack ??= new UndoStack();

        /// <summary>
        /// Hand the bar the app's command history before it builds (the plugin host owns
        /// it; the bar only renders undo/redo). After Build this is a no-op.
        /// </summary>
        public void UseUndoStack(UndoStack stack)
        {
            if (Canvas != null || stack == null) return;
            undoStack = stack;
        }
        public Canvas Canvas { get; private set; }

        public MenuControl Menu { get; private set; }
        public UndoControl Undo { get; private set; }
        public RedoControl Redo { get; private set; }
        public VoiceControlPlaceholder Voice { get; private set; }

        /// <summary>The bar's size in canvas units for a number of buttons: the buttons, the gaps between them and the margin.</summary>
        public static Vector2 SizeFor(int buttons, Vector2 buttonSize, float spacing, float padding) =>
            new(buttons * buttonSize.x + Mathf.Max(0, buttons - 1) * spacing + 2f * padding, buttonSize.y + 2f * padding);

        private void Awake() => Build();

        public void Build()
        {
            if (Canvas != null) return;

            Canvas = UiBuilder.CreateWorldCanvas("Tier1", transform, SizeFor(3, buttonSize, spacing, padding));

            var row = UiBuilder.CreateRow("Controls", Canvas.transform, spacing);
            UiBuilder.Stretch(row.GetComponent<RectTransform>());

            Button menuButton = UiBuilder.CreateButton("Menu", row.transform, MenuLabel, buttonSize);
            Button undoButton = UiBuilder.CreateButton("Undo", row.transform, UndoLabel, buttonSize);
            Button redoButton = UiBuilder.CreateButton("Redo", row.transform, RedoLabel, buttonSize);

            Menu = gameObject.AddComponent<MenuControl>();
            Undo = gameObject.AddComponent<UndoControl>();
            Redo = gameObject.AddComponent<RedoControl>();
            Voice = gameObject.AddComponent<VoiceControlPlaceholder>();

            // Registration enforces the cap. Voice occupies the fourth slot now so that
            // adding anything else fails today rather than when voice arrives.
            Registry.RegisterTierOne(Menu);
            Registry.RegisterTierOne(Undo);
            Registry.RegisterTierOne(Redo);
            Registry.RegisterTierOne(Voice);

            Menu.Initialise(menuButton);
            Undo.Initialise(UndoStack, undoButton);
            Redo.Initialise(UndoStack, redoButton);
        }
    }
}
