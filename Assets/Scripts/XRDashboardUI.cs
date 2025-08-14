using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public class XRDashboardUI : MonoBehaviour
{
	[Header("UI Toolkit")]
	[SerializeField] private UIDocument uiDocument;
	[SerializeField] private bool applyBackgroundStyling = true;
	[SerializeField] private bool startHidden = true;
	[SerializeField] private StyleSheet cardsStyles; // assign Cards.uss here

	[Header("Templates & Layout")]
	[SerializeField] private VisualTreeAsset activeCardTemplate;   // e.g., PluginCard.uxml or CardItem.uxml
	[SerializeField] private VisualTreeAsset addPluginCardTemplate; // AddPluginCard.uxml
	[SerializeField] private float activeCardRowHeight = 80f;
	[SerializeField] private float addPluginCardRowHeight = 72f;
	[SerializeField] private int initialVisibleCount = 3;

	[Header("Input Actions (optional)")]
	[SerializeField] private InputActionAsset inputActions;
	[SerializeField] private string actionMapName = "XRI Left Interaction";
	[SerializeField] private string toggleActionName = "Menu";

	private InputAction toggleAction;
	private VisualElement root;
	private bool isVisible;

	// Cached elements
	private Button addCardButton;
	private Button addPluginBackButton;
	private Button pluginConfigBackButton;
	private ListView activeListView;
	private ListView addPluginListView;
	private Label pluginConfigLabel;
	private ScrollView pluginConfigContent;

	// Data models
	[Serializable] public class CardData { public string Name; public bool Enabled; public CardData(string name, bool enabled){ Name = name; Enabled = enabled; } }
	[Serializable] public class PluginInfo { public string Id; public string DisplayName; }

	[SerializeField] private List<CardData> allActiveCards = new();
	private readonly List<CardData> visibleActiveCards = new();
	[SerializeField] private List<PluginInfo> availablePlugins = new();

	// Panel map
	private readonly Dictionary<string, VisualElement> panelNameToElement = new Dictionary<string, VisualElement>(StringComparer.OrdinalIgnoreCase);

	private void OnEnable()
	{
		if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
		root = uiDocument != null ? uiDocument.rootVisualElement : null;
		if (root == null) { Debug.LogWarning("XRDashboardUI: No UIDocument/rootVisualElement found."); return; }

		if (cardsStyles != null && !root.styleSheets.Contains(cardsStyles)) root.styleSheets.Add(cardsStyles);
		if (applyBackgroundStyling) ApplySemiTransparentBackground(root);

		DiscoverNamedPanels(root);
		SetupInputActions();

		// Buttons
		addCardButton = root.Q<Button>("addPluginButton");
		if (addCardButton != null) addCardButton.clicked += ShowAddPluginView;
		addPluginBackButton = root.Q<Button>("addPluginBackButton");
		if (addPluginBackButton != null) addPluginBackButton.clicked += () => ShowPanel("PluginDashboard");
		pluginConfigBackButton = root.Q<Button>("pluginConfigBackButton");
		if (pluginConfigBackButton != null) pluginConfigBackButton.clicked += () => ShowPanel("PluginDashboard");

		// Lists and config panel
		activeListView = root.Q<ListView>("activePluginCardsList");
		addPluginListView = root.Q<ListView>("addPluginCardsList");
		pluginConfigLabel = root.Q<Label>("pluginConfigLabel");
		pluginConfigContent = root.Q<ScrollView>("pluginConfigContent");

		SetupActiveCardsDataIfEmpty();
		WireActiveListView();
		WireAddPluginListView();

		if (startHidden) Hide(); else Show();
	}

	private void SetupActiveCardsDataIfEmpty()
	{
		// Provide some defaults if list is empty
		if (allActiveCards == null || allActiveCards.Count == 0)
		{
			allActiveCards = new List<CardData>
			{
				new CardData("Planner",  true),
				new CardData("Mapper",   false),
				new CardData("Localize", true),
				new CardData("A", true), new CardData("B", true), new CardData("C", true),
				new CardData("D", true), new CardData("E", true), new CardData("F", true),
				new CardData("G", true), new CardData("H", true),
			};
		}

		visibleActiveCards.Clear();
		var count = Mathf.Clamp(initialVisibleCount, 0, allActiveCards.Count);
		for (int i = 0; i < count; i++) visibleActiveCards.Add(allActiveCards[i]);
	}

	private void WireActiveListView()
	{
		if (activeListView == null || activeCardTemplate == null) return;
		activeListView.fixedItemHeight = activeCardRowHeight;
		activeListView.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
		activeListView.makeItem = () => activeCardTemplate.Instantiate();
		activeListView.bindItem = (e, i) =>
		{
			if (i < 0 || i >= visibleActiveCards.Count) return;
			var data = visibleActiveCards[i];
			var title = e.Q<Label>("title") ?? e.Q<Label>("pluginName");
			if (title != null){ title.text = data.Name; title.style.color = Color.white; }
			var toggle = e.Q<Toggle>("enable");
			if (toggle != null)
			{
				toggle.SetValueWithoutNotify(data.Enabled);
				// Prevent toggle from affecting selection
				toggle.RegisterCallback<PointerDownEvent>(evt => evt.StopImmediatePropagation());
				toggle.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
				
				if (toggle.userData is EventCallback<ChangeEvent<bool>> oldCb) toggle.UnregisterValueChangedCallback(oldCb);
				EventCallback<ChangeEvent<bool>> cb = evt => data.Enabled = evt.newValue;
				toggle.userData = cb; toggle.RegisterValueChangedCallback(cb);
			}
		};
		activeListView.unbindItem = (e, i) =>
		{
			var toggle = e.Q<Toggle>("enable");
			if (toggle?.userData is EventCallback<ChangeEvent<bool>> oldCb) toggle.UnregisterValueChangedCallback(oldCb);
		};
		activeListView.itemsSource = visibleActiveCards;
		activeListView.selectionType = SelectionType.Single;
		
		// Handle card selection to open config panel
		activeListView.onSelectionChange += objects =>
		{
			if (objects != null && objects.Any())
			{
				var selectedIndex = activeListView.selectedIndex;
				if (selectedIndex >= 0 && selectedIndex < visibleActiveCards.Count)
				{
					var selectedCard = visibleActiveCards[selectedIndex];
					ShowPluginConfigPanel(selectedCard.Name);
				}
			}
		};
		
		activeListView.RefreshItems();
		root.schedule.Execute(() => activeListView.RefreshItems()).ExecuteLater(0);
	}

	private void ShowPluginConfigPanel(string pluginName)
	{
		if (pluginConfigLabel != null)
		{
			pluginConfigLabel.text = $"{pluginName} Configuration";
		}
		
		// Clear previous config content and add plugin-specific options
		if (pluginConfigContent != null)
		{
			pluginConfigContent.Clear();
			
			// Add some example configuration elements
			var enabledToggle = new Toggle() { text = $"Enable {pluginName}", value = true };
			pluginConfigContent.Add(enabledToggle);
			
			var prioritySlider = new Slider() { label = "Priority", lowValue = 0, highValue = 10, value = 5, showInputField = true };
			pluginConfigContent.Add(prioritySlider);
			
			var topicField = new TextField() { label = "Topic Name", value = $"/{pluginName.ToLower()}" };
			pluginConfigContent.Add(topicField);
			
			var saveButton = new Button() { text = "Save Configuration" };
			saveButton.clicked += () => {
				Debug.Log($"Saving configuration for {pluginName}");
				ShowPanel("PluginDashboard");
			};
			pluginConfigContent.Add(saveButton);
		}
		
		ShowPanel("PluginConfig");
	}

	private void WireAddPluginListView()
	{
		if (addPluginListView == null || addPluginCardTemplate == null) return;
		addPluginListView.fixedItemHeight = addPluginCardRowHeight;
		addPluginListView.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
		addPluginListView.makeItem = () => addPluginCardTemplate.Instantiate();
		addPluginListView.bindItem = (e, i) =>
		{
			if (i < 0 || i >= availablePlugins.Count) return;
			var info = availablePlugins[i];
			var nameLabel = e.Q<Label>("pluginName");
			if (nameLabel != null){ nameLabel.text = info.DisplayName; nameLabel.style.color = Color.white; }
			var addBtn = e.Q<Button>("addPluginButton");
			if (addBtn != null)
			{
				addBtn.clicked -= addBtn.userData as Action;
				Action click = () => AddPluginFromAvailable(i);
				addBtn.userData = click; addBtn.clicked += click;
			}
		};
		addPluginListView.unbindItem = (e, i) =>
		{
			var addBtn = e.Q<Button>("addPluginButton");
			if (addBtn != null && addBtn.userData is Action click) addBtn.clicked -= click;
		};
		addPluginListView.itemsSource = availablePlugins;
		addPluginListView.selectionType = SelectionType.None;
		addPluginListView.RefreshItems();
		root.schedule.Execute(() => addPluginListView.RefreshItems()).ExecuteLater(0);
	}

	private void AddPluginFromAvailable(int index)
	{
		if (index < 0 || index >= availablePlugins.Count) return;
		var info = availablePlugins[index];
		// Add to active list (visible + all)
		var newCard = new CardData(info.DisplayName, true);
		allActiveCards.Add(newCard);
		visibleActiveCards.Add(newCard);
		activeListView.RefreshItems();
		// Optionally remove from available list
		availablePlugins.RemoveAt(index);
		addPluginListView.itemsSource = availablePlugins;
		addPluginListView.RefreshItems();
		// Return to dashboard
		ShowPanel("PluginDashboard");
	}

	public void ShowAddPluginView()
	{
		ShowPanel("AddPlugin");
	}

	private void OnDisable()
	{
		if (toggleAction != null) toggleAction.performed -= OnToggleActionPerformed;
		if (addCardButton != null) addCardButton.clicked -= ShowAddPluginView;
		// Note: Lambda expressions can't be unsubscribed this way, but Unity handles cleanup automatically
	}

	private void OnDestroy()
	{
		if (toggleAction != null) toggleAction.performed -= OnToggleActionPerformed;
	}

	public void Show(){ if (root == null) return; root.style.display = DisplayStyle.Flex; isVisible = true; }
	public void Hide(){ if (root == null) return; root.style.display = DisplayStyle.None; isVisible = false; }
	public void Toggle(){ if (isVisible) Hide(); else Show(); }

	public void ShowPanel(string panelName)
	{
		if (string.IsNullOrWhiteSpace(panelName) || root == null) return;
		foreach (var child in root.Children()) child.style.display = DisplayStyle.None;
		var panel = root.Q<VisualElement>(panelName);
		if (panel != null) { panel.style.display = DisplayStyle.Flex; panelNameToElement[panelName] = panel; }
		else Debug.LogWarning($"XRDashboardUI: Panel '{panelName}' not found.");
	}

	private void SetupInputActions()
	{
		if (inputActions == null) return;
		var map = inputActions.FindActionMap(actionMapName);
		if (map == null) { Debug.LogWarning($"XRDashboardUI: Action map '{actionMapName}' not found."); return; }
		toggleAction = map.FindAction(toggleActionName);
		if (toggleAction == null) { Debug.LogWarning($"XRDashboardUI: Toggle action '{toggleActionName}' not found in map '{actionMapName}'."); return; }
		toggleAction.performed += OnToggleActionPerformed;
	}
	private void OnToggleActionPerformed(InputAction.CallbackContext _) => Toggle();

	private void DiscoverNamedPanels(VisualElement rootElement)
	{
		panelNameToElement.Clear();
		foreach (var child in rootElement.Children()) if (!string.IsNullOrEmpty(child.name)) panelNameToElement[child.name] = child;
	}

	private void ApplySemiTransparentBackground(VisualElement rootElement)
	{
		rootElement.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
		rootElement.style.borderTopLeftRadius = 8f;
		rootElement.style.borderTopRightRadius = 8f;
		rootElement.style.borderBottomLeftRadius = 8f;
		rootElement.style.borderBottomRightRadius = 8f;
		rootElement.style.borderLeftWidth = 1f;
		rootElement.style.borderRightWidth = 1f;
		rootElement.style.borderTopWidth = 1f;
		rootElement.style.borderBottomWidth = 1f;
		rootElement.style.borderLeftColor = new Color(1f, 1f, 1f, 0.15f);
		rootElement.style.borderRightColor = new Color(1f, 1f, 1f, 0.15f);
		rootElement.style.borderTopColor = new Color(1f, 1f, 1f, 0.15f);
		rootElement.style.borderBottomColor = new Color(1f, 1f, 1f, 0.15f);
	}
}
