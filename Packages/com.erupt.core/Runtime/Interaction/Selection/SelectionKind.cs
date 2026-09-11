namespace Erupt.Interaction
{
    /// <summary>
    /// What kind of thing is selected. Tier 2 menus are built from this — Guidelines
    /// Part 2's contextual verb table has one row per kind.
    /// </summary>
    /// <remarks>
    /// Before Phase 2 the codebase had exactly one notion of selection, the "Selectable"
    /// tag, and robot selection lived in a second, unrelated system. Both now report a
    /// kind through <see cref="ISelectable"/>.
    /// </remarks>
    public enum SelectionKind
    {
        None = 0,
        Obstacle,
        Manipulable,
        RobotLink,
        EndEffector,
        Trajectory,
        Waypoint
    }
}
