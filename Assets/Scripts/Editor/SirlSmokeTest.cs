#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless smoke test for the SIRL scene:
//   Unity -batchmode -executeMethod SirlSmokeTest.Run   (no -quit; exits itself)
// Enters play mode in SIRL.unity and drives the full mock query flow for both
// modes, then exits with code 0 (pass) or 1 (fail).
public static class SirlSmokeTest
{
    private const string Flag = "sirl_smoke_active";
    private const string ScenePath = "Assets/Scenes/SIRL.unity";

    private enum Phase
    {
        FindManager, RequestSimilarity, AwaitSimilaritySelection, PublishSimilarity,
        SwitchToPreference, AwaitPreferenceSelection, PublishPreference
    }

    private static Phase phase;
    private static double phaseStart;
    private static SirlQueryManager manager;

    public static void Run()
    {
        SessionState.SetBool(Flag, true);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    private static void Resume()
    {
        if (!SessionState.GetBool(Flag, false)) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode) return;

        phase = Phase.FindManager;
        phaseStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
        Debug.Log("[SirlSmokeTest] Resumed in play mode; driving mock query flow.");
    }

    private static void Tick()
    {
        double elapsed = EditorApplication.timeSinceStartup - phaseStart;
        if (elapsed > 120) { Fail($"Timeout in phase {phase} (state={manager?.State})."); return; }

        switch (phase)
        {
            case Phase.FindManager:
                manager = Object.FindFirstObjectByType<SirlQueryManager>();
                if (manager == null || Time.frameCount < 5) return;
                Next(Phase.RequestSimilarity);
                return;

            case Phase.RequestSimilarity:
                if (elapsed < 1) return;  // let Start()s run
                Expect(manager.Mode == SirlMode.Similarity, "mode should start as Similarity");
                manager.RequestTrajectories();
                Next(Phase.AwaitSimilaritySelection);
                return;

            case Phase.AwaitSimilaritySelection:
                if (manager.State != SirlState.AwaitingSelection) return;
                Expect(manager.Trajectories.Length == 3, $"expected 3 trajectories, got {manager.Trajectories.Length}");
                CheckGhosts(3);
                manager.ToggleSelect(0);
                manager.ToggleSelect(1);
                Expect(manager.SelectionValid, "similarity selection of {0,1} should be valid");
                Next(Phase.PublishSimilarity);
                return;

            case Phase.PublishSimilarity:
                manager.ConfirmAndPublish();
                Expect(manager.State == SirlState.Published, $"expected Published, got {manager.State}");
                Debug.Log("[SirlSmokeTest] Similarity flow OK.");
                Next(Phase.SwitchToPreference);
                return;

            case Phase.SwitchToPreference:
                manager.SetMode(SirlMode.Preference);
                Expect(manager.TrajectoryCount == 2, "preference mode should use 2 trajectories");
                manager.RequestTrajectories();
                Next(Phase.AwaitPreferenceSelection);
                return;

            case Phase.AwaitPreferenceSelection:
                if (manager.State != SirlState.AwaitingSelection) return;
                Expect(manager.Trajectories.Length == 2, $"expected 2 trajectories, got {manager.Trajectories.Length}");
                CheckGhosts(2);
                manager.ToggleSelect(1);
                Expect(manager.SelectionValid, "preference selection of {1} should be valid");
                Next(Phase.PublishPreference);
                return;

            case Phase.PublishPreference:
                manager.ConfirmAndPublish();
                Expect(manager.State == SirlState.Published, $"expected Published, got {manager.State}");
                Debug.Log("[SirlSmokeTest] Preference flow OK.");
                Pass();
                return;
        }
    }

    private static void CheckGhosts(int expected)
    {
        var spawner = Object.FindFirstObjectByType<SirlGhostSpawner>();
        Expect(spawner.Ghosts.Count == expected, $"expected {expected} ghosts, got {spawner.Ghosts.Count}");
        foreach (var ghost in spawner.Ghosts)
        {
            int jointCount = ghost.Ik.GetJointStateNames().Length;
            Expect(jointCount == 6, $"ghost '{ghost.Root.name}' joint chain has {jointCount} joints, expected 6 (UR5e)");
            Expect(ghost.Root.GetComponent<TranslucentOverride>() != null, "ghost missing TranslucentOverride");
        }
    }

    private static void Next(Phase next)
    {
        phase = next;
        phaseStart = EditorApplication.timeSinceStartup;
        Debug.Log($"[SirlSmokeTest] Phase -> {next}");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) Fail(message);
    }

    private static void Pass()
    {
        Debug.Log("[SirlSmokeTest] PASS — full similarity + preference mock flow succeeded.");
        EditorApplication.update -= Tick;
        EditorApplication.Exit(0);
    }

    private static void Fail(string message)
    {
        Debug.LogError($"[SirlSmokeTest] FAIL — {message}");
        EditorApplication.update -= Tick;
        EditorApplication.Exit(1);
    }
}
#endif
