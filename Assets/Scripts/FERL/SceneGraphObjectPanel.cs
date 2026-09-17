using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

// Shows the selected scene-graph object's type, editability, attributes and carried state,
// and writes edits back through SceneGraphObject so the publisher and env-trace recorder
// see them exactly like a grab.
public class SceneGraphObjectPanel : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    private Label idLabel;
    private DropdownField typeDropdown;
    private Toggle editableToggle;
    private readonly Toggle[] attributeToggles = new Toggle[4];
    private Toggle carriedToggle;

    private SceneGraphObject current;
    private SelectionManager subscribedManager;
    private bool refreshing;

    private static readonly string[] AttributeToggleNames =
    {
        "objectPanelOpenToggle", "objectPanelDamageToggle", "objectPanelFragileToggle", "objectPanelHotToggle"
    };

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();
        VisualElement root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("SceneGraphObjectPanel: No UIDocument/rootVisualElement found.");
            return;
        }

        idLabel = root.Q<Label>("objectPanelIdLabel");
        typeDropdown = root.Q<DropdownField>("objectPanelTypeDropdown");
        editableToggle = root.Q<Toggle>("objectPanelEditableToggle");
        carriedToggle = root.Q<Toggle>("objectPanelCarriedToggle");
        for (int i = 0; i < AttributeToggleNames.Length; i++)
            attributeToggles[i] = root.Q<Toggle>(AttributeToggleNames[i]);

        if (typeDropdown != null)
        {
            typeDropdown.choices = new List<string>(FerlObjectTypes.Names);
            typeDropdown.RegisterValueChangedCallback(OnTypeChanged);
        }
        editableToggle?.RegisterValueChangedCallback(OnEditableChanged);
        carriedToggle?.RegisterValueChangedCallback(OnCarriedChanged);
        for (int i = 0; i < attributeToggles.Length; i++)
        {
            int index = i;
            attributeToggles[i]?.RegisterValueChangedCallback(evt => OnAttributeChanged(index, evt.newValue));
        }

        TrySubscribe();
        Refresh();
    }

    private void Start()
    {
        // SelectionManager.Instance is assigned in its Awake; OnEnable may run before it.
        TrySubscribe();
        Refresh();
    }

    private void OnDisable()
    {
        if (subscribedManager != null)
        {
            subscribedManager.OnObjectSelected -= OnObjectSelected;
            subscribedManager.OnSelectionCleared -= OnSelectionCleared;
            subscribedManager = null;
        }
        typeDropdown?.UnregisterValueChangedCallback(OnTypeChanged);
        editableToggle?.UnregisterValueChangedCallback(OnEditableChanged);
        carriedToggle?.UnregisterValueChangedCallback(OnCarriedChanged);
        SetCurrent(null);
    }

    private void TrySubscribe()
    {
        if (subscribedManager != null || SelectionManager.Instance == null)
            return;
        subscribedManager = SelectionManager.Instance;
        subscribedManager.OnObjectSelected += OnObjectSelected;
        subscribedManager.OnSelectionCleared += OnSelectionCleared;
        if (subscribedManager.SelectedObject != null)
            OnObjectSelected(subscribedManager.SelectedObject);
    }

    private void OnObjectSelected(GameObject selected)
    {
        SetCurrent(selected != null ? selected.GetComponent<SceneGraphObject>() : null);
    }

    private void OnSelectionCleared() => SetCurrent(null);

    private void SetCurrent(SceneGraphObject obj)
    {
        if (current != null)
            current.Changed -= OnCurrentChanged;
        current = obj;
        if (current != null)
            current.Changed += OnCurrentChanged;
        Refresh();
    }

    private void OnCurrentChanged(SceneGraphObject obj) => Refresh();

    // A carried object has no colliders, so it cannot be re-selected by ray or mouse; when
    // nothing is selected the panel shows it instead so "Carried by robot" can be unticked.
    private SceneGraphObject Target()
    {
        if (current != null)
            return current;
        return SceneGraphPublisher.Instance != null ? SceneGraphPublisher.Instance.CarriedObject() : null;
    }

    private void Refresh()
    {
        refreshing = true;
        SceneGraphObject target = Target();
        bool has = target != null;
        if (idLabel != null)
            idLabel.text = has ? (current != null ? target.ObjectId : $"{target.ObjectId} (carried)") : "(select an object)";
        typeDropdown?.SetValueWithoutNotify(has ? FerlObjectTypes.ToName(target.type) : "");
        typeDropdown?.SetEnabled(has);
        editableToggle?.SetValueWithoutNotify(has && target.editable);
        editableToggle?.SetEnabled(has);
        for (int i = 0; i < attributeToggles.Length; i++)
        {
            attributeToggles[i]?.SetValueWithoutNotify(has && target.GetAttribute(i));
            attributeToggles[i]?.SetEnabled(has);
        }
        bool carried = has && SceneGraphPublisher.Instance != null && SceneGraphPublisher.Instance.IsCarried(target);
        carriedToggle?.SetValueWithoutNotify(carried);
        carriedToggle?.SetEnabled(has);
        refreshing = false;
    }

    private void OnTypeChanged(ChangeEvent<string> evt)
    {
        SceneGraphObject target = Target();
        if (refreshing || target == null)
            return;
        target.SetType(FerlObjectTypes.FromName(evt.newValue));
        FERLObjectFactory factory = FindFirstObjectByType<FERLObjectFactory>();
        if (factory != null)
            FERLObjectFactory.Tint(target.gameObject, factory.ColorFor(target.type));
    }

    private void OnEditableChanged(ChangeEvent<bool> evt)
    {
        SceneGraphObject target = Target();
        if (refreshing || target == null)
            return;
        target.SetEditable(evt.newValue);
    }

    private void OnAttributeChanged(int index, bool value)
    {
        SceneGraphObject target = Target();
        if (refreshing || target == null)
            return;
        target.SetAttribute(index, value);
    }

    private void OnCarriedChanged(ChangeEvent<bool> evt)
    {
        SceneGraphObject target = Target();
        if (refreshing || target == null || SceneGraphPublisher.Instance == null)
            return;
        SceneGraphPublisher.Instance.SetCarried(target, evt.newValue);
        Refresh();
    }
}
