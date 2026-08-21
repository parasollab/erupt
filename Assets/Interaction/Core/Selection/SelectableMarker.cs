using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>
    /// Marks a GameObject as selectable and declares its kind.
    /// </summary>
    /// <remarks>
    /// Named SelectableMarker rather than Selectable to avoid colliding with
    /// UnityEngine.UI.Selectable, which tier UI code will have in scope.
    /// </remarks>
    public class SelectableMarker : MonoBehaviour, ISelectable
    {
        [SerializeField] private SelectionKind kind = SelectionKind.Obstacle;
        [SerializeField] private string displayName = "";

        public SelectionKind Kind => kind;
        public GameObject GameObject => gameObject;
        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;

        public void SetKind(SelectionKind newKind) => kind = newKind;
    }
}
