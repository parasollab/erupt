using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;

namespace Erupt.Ui
{
    /// <summary>
    /// The always-visible bar. Guidelines Part 2 tier 1: four controls, no more.
    /// </summary>
    /// <remarks>
    /// Deliberately small and fixed-width. Part 6 turns tier 1 into a visionOS ornament,
    /// which is a tight space, so the cap is a design constraint rather than an
    /// inconvenience — there is nowhere for a fifth control to go.
    ///
    /// Building through UiTierRegistry means a fifth control throws at registration
    /// rather than quietly appearing.
    /// </remarks>
    public class TierOneBar : MonoBehaviour
    {
        [SerializeField] private ModeManager modes;
        [SerializeField] private Vector2 buttonSize = new(160f, 72f);

        private readonly UndoStack undoStack = new();

        public UiTierRegistry Registry { get; } = new();
        public UndoStack UndoStack => undoStack;
        public Canvas Canvas { get; private set; }

        public ModeIndicatorControl Mode { get; private set; }
        public UndoControl Undo { get; private set; }
        public RedoControl Redo { get; private set; }
        public VoiceControlPlaceholder Voice { get; private set; }

        private void Awake() => Build();

        public void Build()
        {
            if (Canvas != null) return;
            if (modes == null) modes = FindFirstObjectByType<ModeManager>();

            Canvas = UiBuilder.CreateWorldCanvas("Tier1", transform, new Vector2(560f, 96f));

            var row = UiBuilder.CreateRow("Controls", Canvas.transform);
            UiBuilder.Stretch(row.GetComponent<RectTransform>());

            Button modeButton = UiBuilder.CreateButton("Mode", row.transform, "Build", buttonSize);
            Button undoButton = UiBuilder.CreateButton("Undo", row.transform, "Undo", buttonSize);
            Button redoButton = UiBuilder.CreateButton("Redo", row.transform, "Redo", buttonSize);

            Mode = gameObject.AddComponent<ModeIndicatorControl>();
            Undo = gameObject.AddComponent<UndoControl>();
            Redo = gameObject.AddComponent<RedoControl>();
            Voice = gameObject.AddComponent<VoiceControlPlaceholder>();

            // Registration enforces the cap. Voice occupies the fourth slot now so that
            // adding anything else fails today rather than in Phase 3.
            Registry.RegisterTierOne(Mode);
            Registry.RegisterTierOne(Undo);
            Registry.RegisterTierOne(Redo);
            Registry.RegisterTierOne(Voice);

            Mode.Initialise(modes, modeButton);
            Undo.Initialise(undoStack, undoButton);
            Redo.Initialise(undoStack, redoButton);
        }
    }
}
