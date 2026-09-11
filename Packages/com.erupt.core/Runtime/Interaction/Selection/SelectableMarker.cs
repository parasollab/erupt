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
        public void SetDisplayName(string name) => displayName = name;

        /// <summary>The object's marker, added with the given kind if it has none.</summary>
        public static SelectableMarker Ensure(GameObject go, SelectionKind kind, string displayName = null)
        {
            if (go == null) return null;
            var marker = go.GetComponent<SelectableMarker>();
            if (marker == null)
            {
                marker = go.AddComponent<SelectableMarker>();
                marker.SetKind(kind);
                if (displayName != null) marker.SetDisplayName(displayName);
            }
            return marker;
        }
    }
}
