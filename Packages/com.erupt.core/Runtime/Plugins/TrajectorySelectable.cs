using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Plugins
{
    /// <summary>
    /// A plan the user can select, so <see cref="SelectionKind.Trajectory"/> verbs
    /// (preview, execute) have something to attach to. Spawned by
    /// <see cref="PlanningPlugin"/> for every <see cref="PlanResult"/>.
    /// </summary>
    public class TrajectorySelectable : MonoBehaviour, ISelectable
    {
        public PlanResult Result { get; private set; }
        public PlanningPlugin Owner { get; private set; }

        public SelectionKind Kind => SelectionKind.Trajectory;
        public GameObject GameObject => gameObject;
        public string DisplayName => Result?.Label ?? name;

        public void Initialize(PlanningPlugin owner, PlanResult result)
        {
            Owner = owner;
            Result = result;
            name = $"Trajectory ({owner.Id})";
        }
    }
}
