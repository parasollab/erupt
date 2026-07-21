using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// World-space menu for SIRL queries: request trajectories, replay/select
// individual ghosts, and publish the similarity/preference result.
public class SirlQueryPanel : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private SirlQueryManager manager;

    private VisualElement root;
    private Button modeButton;
    private Button requestButton;
    private VisualElement trajectoryList;
    private Button playAllButton;
    private Button stopButton;
    private Button confirmButton;
    private Label statusLabel;
    private Label queryCountLabel;

    private int nowPlayingIndex = -1;

    private static readonly string[] TrajectoryLetters = { "A", "B", "C", "D" };

    private void OnEnable()
    {
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        root = uiDocument?.rootVisualElement;
        if (root == null) { Debug.LogError("[SirlQueryPanel] No root visual element."); return; }

        modeButton = root.Q<Button>("sirlModeButton");
        requestButton = root.Q<Button>("sirlRequestButton");
        trajectoryList = root.Q<VisualElement>("sirlTrajectoryList");
        playAllButton = root.Q<Button>("sirlPlayAllButton");
        stopButton = root.Q<Button>("sirlStopButton");
        confirmButton = root.Q<Button>("sirlConfirmButton");
        statusLabel = root.Q<Label>("sirlStatusLabel");
        queryCountLabel = root.Q<Label>("sirlQueryCountLabel");

        modeButton.clicked += OnModeClicked;
        requestButton.clicked += () => manager?.RequestTrajectories();
        playAllButton.clicked += () => manager?.ReplayAll();
        stopButton.clicked += () => manager?.StopReplays();
        confirmButton.clicked += () => manager?.ConfirmAndPublish();

        if (manager != null)
        {
            manager.StateChanged += Render;
            manager.NowPlaying += OnNowPlaying;
        }
        Render();
    }

    private void OnDisable()
    {
        if (manager != null)
        {
            manager.StateChanged -= Render;
            manager.NowPlaying -= OnNowPlaying;
        }
    }

    private void OnModeClicked()
    {
        if (manager == null) return;
        manager.SetMode(manager.Mode == SirlMode.Similarity ? SirlMode.Preference : SirlMode.Similarity);
    }

    private void OnNowPlaying(int index)
    {
        nowPlayingIndex = index;
        Render();
    }

    private void Render()
    {
        if (root == null || manager == null) return;

        modeButton.text = $"Mode: {manager.Mode}";
        modeButton.SetEnabled(manager.CanRequest);
        confirmButton.SetEnabled(manager.SelectionValid && manager.State == SirlState.AwaitingSelection);
        queryCountLabel.text = $"Completed — Similarity: {manager.SimilarityCompletedCount}  Preference: {manager.PreferenceCompletedCount}";

        bool haveTrajectories = manager.Trajectories.Length > 0;
        playAllButton.SetEnabled(haveTrajectories);
        stopButton.SetEnabled(haveTrajectories);
        requestButton.SetEnabled(manager.CanRequest);

        RebuildTrajectoryRows();
        statusLabel.text = StatusText();
    }

    private void RebuildTrajectoryRows()
    {
        trajectoryList.Clear();

        for (int i = 0; i < manager.Trajectories.Length; i++)
        {
            int index = i;
            bool isSelected = manager.Selection.Contains(index);
            bool isPlaying = index == nowPlayingIndex || manager.State == SirlState.Playing;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));
            row.style.backgroundColor = new StyleColor(isSelected
                ? new Color(0.18f, 0.32f, 0.18f)
                : new Color(0.08f, 0.08f, 0.08f));

            var swatch = new VisualElement();
            swatch.style.width = 14;
            swatch.style.height = 14;
            swatch.style.marginRight = 6;
            Color swatchColor = index < manager.Colors.Count ? manager.Colors[index] : Color.white;
            swatchColor.a = 1f;
            swatch.style.backgroundColor = new StyleColor(swatchColor);

            string letter = index < TrajectoryLetters.Length ? TrajectoryLetters[index] : index.ToString();
            var nameLabel = new Label($"Trajectory {letter}{(isPlaying ? "  ▶" : "")}");
            nameLabel.style.flexGrow = 1;
            nameLabel.style.color = new StyleColor(Color.white);
            nameLabel.style.fontSize = 12;

            var replayButton = new Button(() => manager.ReplayOne(index)) { text = "Replay" };
            replayButton.style.fontSize = 11;

            var selectButton = new Button(() => manager.ToggleSelect(index))
            {
                text = isSelected ? "Selected" : "Select"
            };
            selectButton.style.fontSize = 11;
            if (isSelected)
                selectButton.style.color = new StyleColor(new Color(0.4f, 1f, 0.4f));
            selectButton.SetEnabled(manager.State == SirlState.AwaitingSelection || manager.State == SirlState.Published);

            row.Add(swatch);
            row.Add(nameLabel);
            row.Add(replayButton);
            row.Add(selectButton);
            trajectoryList.Add(row);
        }
    }

    private string StatusText()
    {
        string instruction = manager.Mode == SirlMode.Similarity
            ? "Select the TWO most similar trajectories."
            : "Select the ONE trajectory you prefer.";

        switch (manager.State)
        {
            case SirlState.Idle:
                return $"Idle. Request {manager.TrajectoryCount} trajectories to begin.";
            case SirlState.Requesting:
                return "Requesting trajectories...";
            case SirlState.Playing:
                return "Playing all trajectories...";
            case SirlState.AwaitingSelection:
                return $"{instruction}\nSelected: {manager.Selection.Count}/{manager.RequiredSelectionCount}";
            case SirlState.Published:
                return "Result published. Request again for a new query.";
            default:
                return "";
        }
    }
}
