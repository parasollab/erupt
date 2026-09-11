namespace Erupt.Environment
{
    /// <summary>
    /// A component that keeps the environment mirror in step with an external scene, such
    /// as a planner's world. Core never references a sync; the sync binds itself to the
    /// registry and reacts to its events.
    /// </summary>
    public interface IEnvironmentSync
    {
        /// <summary>Human-readable name for diagnostics ("moveit").</summary>
        string SyncId { get; }

        /// <summary>The registry this sync mirrors, or null before binding.</summary>
        EnvironmentRegistry Registry { get; }
    }
}
