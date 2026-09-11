using UnityEngine;
using Erupt.Interaction;

/// <summary>
/// Material swap on the selected environment object, driven by
/// <see cref="SelectionService.SelectionChanged"/>. This is what remains of the legacy
/// SelectionManager: the selection rule itself now lives in
/// <see cref="SelectionRouterBinding"/> and the state in <see cref="SelectionService"/>.
/// </summary>
/// <remarks>
/// Only <see cref="SelectionKind.Obstacle"/> and <see cref="SelectionKind.Manipulable"/>
/// are highlighted here: robot links keep their own tint in
/// Quest3RobotInteractionController, exactly as before the merge.
/// </remarks>
public class SelectionHighlighter : MonoBehaviour
{
    [Tooltip("Found in the scene if empty.")]
    [SerializeField] private SelectionService selectionService;

    [Header("Highlighting")]
    public Material highlightMaterial;

    private Material originalMaterial;
    private Renderer selectedRenderer;

    public SelectionService Service => selectionService;

    /// <summary>The highlighted object, for callers that still think in GameObjects.</summary>
    public GameObject SelectedObject => selectionService != null ? selectionService.Current?.GameObject : null;

    private void Awake()
    {
        if (selectionService == null) selectionService = FindFirstObjectByType<SelectionService>();
        if (selectionService == null)
        {
            Debug.LogError("SelectionHighlighter: no SelectionService in the scene.", this);
            return;
        }
        selectionService.SelectionChanged += OnSelectionChanged;
        OnSelectionChanged(selectionService.Current);
    }

    private void OnDestroy()
    {
        if (selectionService != null) selectionService.SelectionChanged -= OnSelectionChanged;
        Restore();
    }

    private void OnSelectionChanged(ISelectable current)
    {
        Restore();

        if (current == null || current.GameObject == null) return;
        if (current.Kind != SelectionKind.Obstacle && current.Kind != SelectionKind.Manipulable) return;

        selectedRenderer = current.GameObject.GetComponent<Renderer>();
        if (selectedRenderer == null || highlightMaterial == null) { selectedRenderer = null; return; }

        originalMaterial = selectedRenderer.material;
        selectedRenderer.material = highlightMaterial;
    }

    private void Restore()
    {
        if (selectedRenderer != null && selectedRenderer.gameObject != null && originalMaterial != null)
            selectedRenderer.material = originalMaterial;
        selectedRenderer = null;
        originalMaterial = null;
    }

    /// <summary>Select an environment object by GameObject (legacy callers); it gets an Obstacle marker if it has none.</summary>
    public void Select(GameObject target)
    {
        if (selectionService == null) return;
        if (target == null) { selectionService.ClearSelection(); return; }
        selectionService.Select(SelectionService.Resolve(target) ?? SelectableMarker.Ensure(target, SelectionKind.Obstacle));
    }

    public void ClearSelection() => selectionService?.ClearSelection();

    /// <summary>Destroy the selected object after clearing the highlight and the selection.</summary>
    public void DeleteSelectedObject()
    {
        GameObject target = SelectedObject;
        if (target == null) return;
        selectionService.ClearSelection();     // restores the material first
        Destroy(target);
    }
}
