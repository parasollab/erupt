using UnityEngine;
using UnityEngine.UIElements;

// World-space palette: one button per scene-graph object type, plus delete. Mirrors the
// IndicatorMenuController pattern (buttons resolved by UXML name in OnEnable).
public class FERLObjectPaletteController : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private FERLObjectFactory factory;
    [SerializeField] private float spawnDistance = 0.75f;
    [Tooltip("Spawned objects are dropped this far below eye level so they land near table height.")]
    [SerializeField] private float spawnDrop = 0.35f;

    private readonly Button[] addButtons = new Button[FerlObjectTypes.Names.Length];
    private Button deleteButton;

    private static readonly string[] ButtonNames =
    {
        "paletteAddLaptop", "paletteAddCup", "paletteAddTable", "paletteAddHuman", "paletteAddMarker", "paletteAddOther"
    };

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();
        VisualElement root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("FERLObjectPaletteController: No UIDocument/rootVisualElement found.");
            return;
        }

        for (int i = 0; i < ButtonNames.Length; i++)
        {
            int index = i;
            addButtons[i] = root.Q<Button>(ButtonNames[i]);
            if (addButtons[i] != null)
                addButtons[i].clicked += () => Spawn((FerlObjectType)index);
            else
                Debug.LogError($"FERLObjectPaletteController: '{ButtonNames[i]}' not found in UXML.");
        }
        deleteButton = root.Q<Button>("paletteDeleteSelected");
        if (deleteButton != null)
            deleteButton.clicked += DeleteSelected;
    }

    private void OnDisable()
    {
        for (int i = 0; i < addButtons.Length; i++)
            addButtons[i] = null;
        if (deleteButton != null)
            deleteButton.clicked -= DeleteSelected;
        deleteButton = null;
    }

    public SceneGraphObject Spawn(FerlObjectType type)
    {
        if (factory == null)
        {
            Debug.LogError("FERLObjectPaletteController: factory is not assigned.");
            return null;
        }
        Camera camera = Camera.main;
        Vector3 position = camera != null
            ? camera.transform.position + camera.transform.forward * spawnDistance + Vector3.down * spawnDrop
            : Vector3.forward * spawnDistance;
        SceneGraphObject spawned = factory.Spawn(type, position);
        if (SelectionManager.Instance != null)
            SelectionManager.Instance.SetSelectedObject(spawned.gameObject);
        SceneGraphPublisher.Instance?.MarkDirty();
        return spawned;
    }

    public void DeleteSelected()
    {
        SelectionManager manager = SelectionManager.Instance;
        GameObject selected = manager != null ? manager.SelectedObject : null;
        if (selected == null || selected.GetComponent<SceneGraphObject>() == null)
        {
            Debug.Log("FERLObjectPaletteController: nothing selected to delete (select a scene-graph object first).");
            return;
        }
        manager.ClearSelection();
        Destroy(selected);
        SceneGraphPublisher.Instance?.MarkDirty();
    }
}
