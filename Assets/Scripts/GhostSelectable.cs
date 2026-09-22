using UnityEngine;

// Marker added to every ghost (start/goal/replay) by SpawnGhosts.SpawnGhost so it can be
// raycast-selected despite its original colliders being stripped. Bridges a hit back to the
// shared overhead GhostControlPanel (via Spawner) and, for replay ghosts, to the
// TrajectoryReplay driving it.
public class GhostSelectable : MonoBehaviour
{
    // Owning spawner, set by SpawnGhosts.SetupGhostSelection. All ghosts share a single control
    // panel instance (see SpawnGhosts.ToggleControlPanelForGhost) rather than each owning its own.
    public SpawnGhosts Spawner { get; set; }
    public TrajectoryReplay ReplaySource { get; set; }

    // "StartGhost", "GoalGhost", or "ReplayGhost" — set by SpawnGhosts.SpawnGhost, doubles as the key
    // for remembering this ghost's control-panel placement across respawns (see SpawnGhosts.SavePanelOffset).
    public string GhostKind { get; set; }

    public void OnSelected()
    {
        Spawner?.ToggleControlPanelForGhost(this);
    }

    // Hides visuals only, not the collider, so a hidden ghost can still be clicked to reopen its panel.
    public void SetHidden(bool hidden)
    {
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            renderer.enabled = !hidden;
    }
}
