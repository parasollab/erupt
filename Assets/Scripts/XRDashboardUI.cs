using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Unity.Robotics.Visualizations;
#if UNITY_EDITOR
using UnityEditor.UIElements;
#endif

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

	[Header("Input Actions (optional)")]
	[SerializeField] private InputActionAsset inputActions;
	[SerializeField] private string actionMapName = "XRI Left Interaction";
	[SerializeField] private string toggleActionName = "Menu";

	[Header("Unity Robotics Integration")]
	[SerializeField] private GameObject defaultVisualizationSuite;
	[SerializeField] private bool autoFindVisualizationSuite = true;

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
	
	[Serializable]
	public class VisualizerInfo
	{
		public string Name;
		public string MessageType;
		public string Topic;
		public Component Component;
		public List<ConfigParameter> Parameters = new List<ConfigParameter>();
	}
	
	[Serializable]
	public class ConfigParameter
	{
		public string Name;
		public string DisplayName;
		public object Value;
		public Type Type;
		public FieldInfo Field;
		public bool IsSerializeField;
		public string Tooltip;
	}

	[SerializeField] private List<CardData> allActiveCards = new();
	private readonly List<CardData> visibleActiveCards = new();
	[SerializeField] private List<PluginInfo> availablePlugins = new();
	
	// Unity Robotics Integration
	private List<VisualizerInfo> discoveredVisualizers = new List<VisualizerInfo>();
	private VisualizerInfo currentSelectedVisualizer;

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
		DiscoverUnityRoboticsVisualizers();
		WireActiveListView();
		WireAddPluginListView();

		if (startHidden) Hide(); else Show();
	}

	private void SetupActiveCardsDataIfEmpty()
	{
		// Initialize empty list if null, but don't add any default cards
		if (allActiveCards == null)
		{
			allActiveCards = new List<CardData>();
		}

		// Show all active cards (will be empty by default until Unity Robotics discovery runs)
		visibleActiveCards.Clear();
		visibleActiveCards.AddRange(allActiveCards);
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
		
		// Find the corresponding visualizer
		currentSelectedVisualizer = discoveredVisualizers.FirstOrDefault(v => v.Name == pluginName);
		
		// Clear previous config content and add plugin-specific options
		if (pluginConfigContent != null)
		{
			pluginConfigContent.Clear();
			
			if (currentSelectedVisualizer != null)
			{
				// Generate configuration UI from Unity Robotics visualizer parameters
				GenerateVisualizerConfigUI(currentSelectedVisualizer);
			}
			else
			{
				// Fallback to example configuration elements
				var enabledToggle = new Toggle() { text = $"Enable {pluginName}", value = true };
				pluginConfigContent.Add(enabledToggle);
				
				var prioritySlider = new Slider() { label = "Priority", lowValue = 0, highValue = 10, value = 5, showInputField = true };
				pluginConfigContent.Add(prioritySlider);
				
				var topicField = new TextField() { label = "Topic Name", value = $"/{pluginName.ToLower()}" };
				pluginConfigContent.Add(topicField);
			}
			
			var saveButton = new Button() { text = "Save Configuration" };
			saveButton.clicked += () => {
				if (currentSelectedVisualizer != null)
				{
					ApplyConfigurationChanges(currentSelectedVisualizer);
				}
				Debug.Log($"Saving configuration for {pluginName}");
				ShowPanel("PluginDashboard");
			};
			pluginConfigContent.Add(saveButton);
		}
		
		ShowPanel("PluginConfig");
	}

	private void WireAddPluginListView()
	{
		Debug.Log($"WireAddPluginListView: addPluginListView={(addPluginListView == null ? "null" : "exists")}, template={(addPluginCardTemplate == null ? "null" : "exists")}");
		
		if (addPluginListView == null || addPluginCardTemplate == null) 
		{
			Debug.LogWarning("WireAddPluginListView: Missing components, cannot wire add plugin list");
			return;
		}
		
		Debug.Log($"WireAddPluginListView: Setting up with {availablePlugins.Count} available plugins");
		
		addPluginListView.fixedItemHeight = addPluginCardRowHeight;
		addPluginListView.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
		addPluginListView.makeItem = () => addPluginCardTemplate.Instantiate();
		addPluginListView.bindItem = (e, i) =>
		{
			if (i < 0 || i >= availablePlugins.Count) return;
			var info = availablePlugins[i];
			Debug.Log($"Binding plugin item {i}: {info.DisplayName}");
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
		
		Debug.Log("WireAddPluginListView: Setup complete");
	}

	private void AddPluginFromAvailable(int index)
	{
		if (index < 0 || index >= availablePlugins.Count) return;
		var info = availablePlugins[index];
		
		Debug.Log($"AddPluginFromAvailable: Attempting to add {info.DisplayName} (ID: {info.Id})");
		
		// Try to create the visualizer component
		if (CreateVisualizerComponent(info))
		{
			Debug.Log($"CreateVisualizerComponent succeeded for {info.DisplayName}");
			
			// Manually add to active cards list (don't rely on discovery)
			var displayName = info.DisplayName.Split('(')[0].Trim(); // Remove the message type part
			var newCard = new CardData(displayName, true);
			allActiveCards.Add(newCard);
			
			Debug.Log($"Added card to allActiveCards: {displayName}");
			
			// Refresh the discovery to pick up the new component
			DiscoverUnityRoboticsVisualizers();
			
			// Refresh the UI - show all active cards
			visibleActiveCards.Clear();
			visibleActiveCards.AddRange(allActiveCards);
			
			activeListView.RefreshItems();
			addPluginListView.itemsSource = availablePlugins;
			addPluginListView.RefreshItems();
			
			Debug.Log($"UI refreshed. Active cards count: {allActiveCards.Count}, Visible cards count: {visibleActiveCards.Count}");
		}
		else
		{
			Debug.LogError($"Failed to create visualizer: {info.DisplayName}");
		}
		
		// Return to dashboard
		ShowPanel("PluginDashboard");
	}

	private bool CreateVisualizerComponent(PluginInfo pluginInfo)
	{
		Debug.Log($"CreateVisualizerComponent: Creating {pluginInfo.Id} ({pluginInfo.DisplayName})");
		
		if (defaultVisualizationSuite == null)
		{
			Debug.LogError("Cannot create visualizer: DefaultVisualizationSuite not found");
			// For fallback plugins, just add to the list without creating actual components
			if (IsTestPlugin(pluginInfo))
			{
				Debug.Log($"Adding test plugin {pluginInfo.DisplayName} without creating component");
				return true;
			}
			return false;
		}
		
		// Find the type by name
		var type = System.AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic)
			.SelectMany(a => {
				try 
				{ 
					return a.GetTypes(); 
				} 
				catch 
				{ 
					return new Type[0];
				}
			})
			.FirstOrDefault(t => t.Name == pluginInfo.Id);
			
		if (type == null)
		{
			Debug.LogWarning($"Visualizer type not found: {pluginInfo.Id}");
			// For fallback plugins, just add to the list without creating actual components
			if (IsTestPlugin(pluginInfo))
			{
				Debug.Log($"Adding test plugin {pluginInfo.DisplayName} without creating component");
				return true;
			}
			return false;
		}
		
		// Create a new GameObject for this visualizer
		var visualizerName = GetVisualizerDisplayName(type);
		var newGameObject = new GameObject(visualizerName);
		newGameObject.transform.SetParent(defaultVisualizationSuite.transform);
		
		Debug.Log($"Created GameObject: {visualizerName}");
		
		// Add the visualizer component
		try
		{
			var component = newGameObject.AddComponent(type);
			if (component != null)
			{
				Debug.Log($"Successfully created {type.Name} component on {visualizerName}");
				return true;
			}
		}
		catch (System.Exception e)
		{
			Debug.LogError($"Failed to add component {type.Name}: {e.Message}");
			if (newGameObject != null)
			{
				DestroyImmediate(newGameObject);
			}
		}
		
		return false;
	}

	private bool IsTestPlugin(PluginInfo pluginInfo)
	{
		// Check if this is one of our fallback test plugins
		return pluginInfo.Id.Contains("Visualizer") && !pluginInfo.Id.Contains("Default");
	}

	public void ShowAddPluginView()
	{
		Debug.Log($"ShowAddPluginView called. Available plugins count: {availablePlugins.Count}");
		foreach (var plugin in availablePlugins)
		{
			Debug.Log($"Available plugin: {plugin.DisplayName}");
		}
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
		if (string.IsNullOrWhiteSpace(panelName) || root == null) 
		{
			Debug.LogWarning($"ShowPanel failed: panelName='{panelName}', root={(root == null ? "null" : "exists")}");
			return;
		}
		
		Debug.Log($"ShowPanel: Switching to '{panelName}'");
		
		// Clear selection when returning to main dashboard to allow re-selection
		if (panelName == "PluginDashboard" && activeListView != null)
		{
			activeListView.ClearSelection();
			Debug.Log("Cleared ListView selection for re-selection");
		}
		
		foreach (var child in root.Children()) child.style.display = DisplayStyle.None;
		var panel = root.Q<VisualElement>(panelName);
		if (panel != null) 
		{ 
			panel.style.display = DisplayStyle.Flex; 
			panelNameToElement[panelName] = panel;
			Debug.Log($"ShowPanel: Successfully showed '{panelName}'");
		}
		else 
		{
			Debug.LogWarning($"XRDashboardUI: Panel '{panelName}' not found.");
			// List all available panels for debugging
			Debug.Log("Available panels:");
			foreach (var child in root.Children())
			{
				if (!string.IsNullOrEmpty(child.name))
					Debug.Log($"  - {child.name}");
			}
		}
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

	#region Unity Robotics Integration

	private void DiscoverUnityRoboticsVisualizers()
	{
		discoveredVisualizers.Clear();
		
		// Auto-find DefaultVisualizationSuite if not assigned
		if (autoFindVisualizationSuite && defaultVisualizationSuite == null)
		{
			defaultVisualizationSuite = GameObject.Find("DefaultVisualizationSuite");
		}
		
		if (defaultVisualizationSuite == null)
		{
			Debug.LogWarning("XRDashboardUI: No DefaultVisualizationSuite found. Assign manually or ensure it exists in scene.");
			UpdateAvailablePluginsFromVisualizers(); // Still update available plugins even if no suite found
			return;
		}
		
		// Find all visualizer components in the suite
		var allVisualizers = defaultVisualizationSuite.GetComponentsInChildren<MonoBehaviour>();
		
		foreach (var visualizer in allVisualizers)
		{
			if (IsValidVisualizer(visualizer))
			{
				var info = ExtractVisualizerInfo(visualizer);
				if (info != null)
				{
					discoveredVisualizers.Add(info);
				}
			}
		}
		
		// Only update available plugins list - don't auto-add to active cards
		UpdateAvailablePluginsFromVisualizers();
		
		Debug.Log($"XRDashboardUI: Discovered {discoveredVisualizers.Count} Unity Robotics visualizers");
	}

	private bool IsValidVisualizer(MonoBehaviour component)
	{
		if (component == null) return false;
		
		var type = component.GetType();
		
		// Check if it's a DrawingVisualizer or BaseVisualFactory
		return type.Name.Contains("Visualizer") || 
		       type.GetInterfaces().Any(i => i.Name.Contains("IVisualFactory")) ||
		       IsSubclassOfGeneric(type, typeof(DrawingVisualizer<>)) ||
		       IsSubclassOfGeneric(type, typeof(BaseVisualFactory<>));
	}

	private bool IsSubclassOfGeneric(Type type, Type genericBaseType)
	{
		while (type != null && type != typeof(object))
		{
			var currentType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
			if (genericBaseType == currentType)
			{
				return true;
			}
			type = type.BaseType;
		}
		return false;
	}

	private VisualizerInfo ExtractVisualizerInfo(MonoBehaviour visualizer)
	{
		var type = visualizer.GetType();
		var info = new VisualizerInfo
		{
			Name = GetVisualizerDisplayName(type),
			MessageType = GetMessageTypeName(type),
			Component = visualizer,
			Parameters = new List<ConfigParameter>()
		};
		
		// Extract topic field if available
		var topicField = type.GetField("m_Topic", BindingFlags.NonPublic | BindingFlags.Instance) ??
		                 type.GetField("Topic", BindingFlags.Public | BindingFlags.Instance);
		if (topicField != null)
		{
			info.Topic = topicField.GetValue(visualizer)?.ToString() ?? "";
		}
		
		// Extract all configurable fields
		var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
		
		foreach (var field in fields)
		{
			if (IsConfigurableField(field))
			{
				var param = new ConfigParameter
				{
					Name = field.Name,
					DisplayName = GetDisplayName(field),
					Value = field.GetValue(visualizer),
					Type = field.FieldType,
					Field = field,
					IsSerializeField = field.GetCustomAttribute<SerializeField>() != null,
					Tooltip = GetTooltip(field)
				};
				info.Parameters.Add(param);
			}
		}
		
		return info;
	}

	private string GetVisualizerDisplayName(Type type)
	{
		var name = type.Name;
		
		// Remove common suffixes
		if (name.EndsWith("DefaultVisualizer"))
			name = name.Substring(0, name.Length - "DefaultVisualizer".Length);
		else if (name.EndsWith("Visualizer"))
			name = name.Substring(0, name.Length - "Visualizer".Length);
		
		// Add spaces before capital letters
		return System.Text.RegularExpressions.Regex.Replace(name, "(\\B[A-Z])", " $1");
	}

	private string GetMessageTypeName(Type type)
	{
		// Try to extract message type from generic base class
		var baseType = type.BaseType;
		while (baseType != null)
		{
			if (baseType.IsGenericType)
			{
				var genericArgs = baseType.GetGenericArguments();
				if (genericArgs.Length > 0)
				{
					return genericArgs[0].Name.Replace("Msg", "");
				}
			}
			baseType = baseType.BaseType;
		}
		return "Unknown";
	}

	private bool IsConfigurableField(FieldInfo field)
	{
		// Include fields that are:
		// 1. Public
		// 2. Have SerializeField attribute
		// 3. Are of supported types
		// 4. Not Unity internal fields
		
		if (field.Name.StartsWith("m_") && field.Name.Contains("Object")) return false;
		if (field.Name.StartsWith("m_") && field.Name.Contains("Instance")) return false;
		if (field.FieldType == typeof(GameObject)) return false;
		if (field.FieldType == typeof(Transform)) return false;
		
		return (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null) &&
		       IsSupportedType(field.FieldType);
	}

	private bool IsSupportedType(Type type)
	{
		return type == typeof(string) ||
		       type == typeof(int) ||
		       type == typeof(float) ||
		       type == typeof(bool) ||
		       type == typeof(Vector3) ||
		       type == typeof(Color) ||
		       type.IsEnum;
	}

	private string GetDisplayName(FieldInfo field)
	{
		var name = field.Name;
		
		// Remove m_ prefix
		if (name.StartsWith("m_"))
			name = name.Substring(2);
		
		// Add spaces before capital letters
		return System.Text.RegularExpressions.Regex.Replace(name, "(\\B[A-Z])", " $1");
	}

	private string GetTooltip(FieldInfo field)
	{
		var tooltipAttr = field.GetCustomAttribute<TooltipAttribute>();
		return tooltipAttr?.tooltip ?? "";
	}

	private void UpdateActiveCardsFromVisualizers()
	{
		// Only add discovered visualizers that don't already exist in allActiveCards
		// This prevents auto-adding all discovered visualizers when user manually adds one
		if (discoveredVisualizers.Count > 0)
		{
			foreach (var visualizer in discoveredVisualizers)
			{
				// Check if this visualizer is already in the active cards list
				bool alreadyExists = allActiveCards.Any(card => card.Name == visualizer.Name);
				
				if (!alreadyExists)
				{
					// Only add it if the GameObject is active (meaning it was manually added or enabled)
					if (visualizer.Component.gameObject.activeInHierarchy)
					{
						allActiveCards.Add(new CardData(visualizer.Name, true));
						Debug.Log($"Added active card for discovered visualizer: {visualizer.Name}");
					}
				}
			}
		}
		
		// Update available plugins with visualizers that could be added
		UpdateAvailablePluginsFromVisualizers();
	}

	private void UpdateAvailablePluginsFromVisualizers()
	{
		availablePlugins.Clear();
		
		// Get all known Unity Robotics visualizer types
		var knownVisualizerTypes = GetKnownVisualizerTypes();
		
		// Add visualizers that aren't currently in the active cards list
		foreach (var visualizerType in knownVisualizerTypes)
		{
			var displayName = GetVisualizerDisplayName(visualizerType.Key);
			var messageType = visualizerType.Value;
			
			// Check if this visualizer type is already in the active cards list
			bool isInActiveList = allActiveCards.Any(card => card.Name == displayName);
			
			if (!isInActiveList)
			{
				availablePlugins.Add(new PluginInfo 
				{ 
					Id = visualizerType.Key.Name,
					DisplayName = $"{displayName} ({messageType})"
				});
			}
		}
		
		// Fallback: Add some test plugins if no Unity Robotics visualizers found
		if (availablePlugins.Count == 0)
		{
			Debug.LogWarning("No Unity Robotics visualizers found, adding fallback test plugins");
			availablePlugins.AddRange(new[]
			{
				new PluginInfo { Id = "PointCloudVisualizer", DisplayName = "Point Cloud Visualizer (PointCloud2)" },
				new PluginInfo { Id = "LaserScanVisualizer", DisplayName = "Laser Scan Visualizer (LaserScan)" },
				new PluginInfo { Id = "PathVisualizer", DisplayName = "Path Visualizer (Path)" },
				new PluginInfo { Id = "OccupancyGridVisualizer", DisplayName = "Occupancy Grid Visualizer (OccupancyGrid)" },
				new PluginInfo { Id = "PoseVisualizer", DisplayName = "Pose Visualizer (PoseStamped)" }
			});
		}
		
		Debug.Log($"XRDashboardUI: Found {availablePlugins.Count} available visualizer plugins");
	}

	private Dictionary<Type, string> GetKnownVisualizerTypes()
	{
		var knownTypes = new Dictionary<Type, string>();
		
		Debug.Log("Scanning for Unity Robotics Visualizer types...");
		
		try
		{
			// Dynamically find all visualizer types instead of hardcoding
			var visualizerTypes = System.AppDomain.CurrentDomain.GetAssemblies()
				.Where(a => !a.IsDynamic)
				.SelectMany(a => {
					try 
					{ 
						return a.GetTypes(); 
					} 
					catch 
					{ 
						return new Type[0];
					}
				})
				.Where(t => t != null && 
					   (t.Name.Contains("Visualizer") || t.Name.Contains("Visual")) &&
					   !t.IsAbstract && 
					   !t.IsInterface &&
					   typeof(MonoBehaviour).IsAssignableFrom(t))
				.ToList();
			
			Debug.Log($"Found {visualizerTypes.Count} potential visualizer types");
			
			foreach (var type in visualizerTypes)
			{
				var messageType = ExtractMessageTypeFromVisualizerType(type);
				knownTypes[type] = messageType;
				Debug.Log($"Added visualizer: {type.Name} -> {messageType}");
			}
		}
		catch (System.Exception e)
		{
			Debug.LogError($"Error scanning for visualizer types: {e.Message}");
		}
		
		// Fallback to some common types if dynamic scanning fails
		if (knownTypes.Count == 0)
		{
			Debug.LogWarning("Dynamic scanning failed, trying hardcoded common types...");
			
			// Try some common ones that are most likely to exist
			AddVisualizerType(knownTypes, "PointCloud2DefaultVisualizer", "PointCloud2");
			AddVisualizerType(knownTypes, "PoseStampedDefaultVisualizer", "PoseStamped");
			AddVisualizerType(knownTypes, "LaserScanDefaultVisualizer", "LaserScan");
			AddVisualizerType(knownTypes, "PathDefaultVisualizer", "Path");
			AddVisualizerType(knownTypes, "OccupancyGridDefaultVisualizer", "OccupancyGrid");
			AddVisualizerType(knownTypes, "MarkerDefaultVisualizer", "Marker");
		}
		
		Debug.Log($"GetKnownVisualizerTypes: Found {knownTypes.Count} total visualizer types");
		return knownTypes;
	}

	private string ExtractMessageTypeFromVisualizerType(Type type)
	{
		var name = type.Name;
		
		// Remove "DefaultVisualizer" suffix
		if (name.EndsWith("DefaultVisualizer"))
			name = name.Substring(0, name.Length - "DefaultVisualizer".Length);
		else if (name.EndsWith("Visualizer"))
			name = name.Substring(0, name.Length - "Visualizer".Length);
		
		// Try to extract from generic base types
		var baseType = type.BaseType;
		while (baseType != null)
		{
			if (baseType.IsGenericType)
			{
				var genericArgs = baseType.GetGenericArguments();
				if (genericArgs.Length > 0)
				{
					var msgType = genericArgs[0].Name;
					if (msgType.EndsWith("Msg"))
						msgType = msgType.Substring(0, msgType.Length - 3);
					return msgType;
				}
			}
			baseType = baseType.BaseType;
		}
		
		return name;
	}

	private void AddVisualizerType(Dictionary<Type, string> dict, string typeName, string messageType)
	{
		try
		{
			// Try to find the type in all loaded assemblies
			var type = System.AppDomain.CurrentDomain.GetAssemblies()
				.Where(a => !a.IsDynamic) // Skip dynamic assemblies that can cause issues
				.SelectMany(a => {
					try 
					{ 
						return a.GetTypes(); 
					} 
					catch 
					{ 
						return new Type[0]; // Skip assemblies that can't be loaded
					}
				})
				.FirstOrDefault(t => t.Name == typeName);
				
			if (type != null)
			{
				dict[type] = messageType;
				Debug.Log($"Found visualizer type: {typeName}");
			}
			else
			{
				Debug.LogWarning($"Visualizer type not found: {typeName}");
			}
		}
		catch (System.Exception e)
		{
			Debug.LogError($"Error searching for visualizer type {typeName}: {e.Message}");
		}
	}

	private void GenerateVisualizerConfigUI(VisualizerInfo visualizer)
	{
		if (visualizer?.Parameters == null) return;
		
		// Add basic info
		var infoLabel = new Label($"Message Type: {visualizer.MessageType}");
		infoLabel.style.color = Color.white;
		infoLabel.style.marginBottom = 10;
		pluginConfigContent.Add(infoLabel);
		
		// Generate UI elements for each parameter
		foreach (var param in visualizer.Parameters)
		{
			var element = CreateConfigElement(param);
			if (element != null)
			{
				pluginConfigContent.Add(element);
			}
		}
	}

	private VisualElement CreateConfigElement(ConfigParameter param)
	{
		var container = new VisualElement();
		container.style.marginBottom = 8;
		
		if (param.Type == typeof(string))
		{
			var textField = new TextField(param.DisplayName) { value = param.Value?.ToString() ?? "" };
			SetWhiteTextStyle(textField);
			textField.userData = param; // Store reference for saving
			container.Add(textField);
		}
		else if (param.Type == typeof(int))
		{
			var intField = new IntegerField(param.DisplayName) { value = (int)(param.Value ?? 0) };
			SetWhiteTextStyle(intField);
			intField.userData = param;
			container.Add(intField);
		}
		else if (param.Type == typeof(float))
		{
			var floatField = new FloatField(param.DisplayName) { value = (float)(param.Value ?? 0f) };
			SetWhiteTextStyle(floatField);
			floatField.userData = param;
			container.Add(floatField);
		}
		else if (param.Type == typeof(bool))
		{
			var toggle = new Toggle(param.DisplayName) { value = (bool)(param.Value ?? false) };
			SetWhiteTextStyle(toggle);
			toggle.userData = param;
			container.Add(toggle);
		}
		else if (param.Type == typeof(Vector3))
		{
			var vector3Field = new Vector3Field(param.DisplayName) { value = (Vector3)(param.Value ?? Vector3.zero) };
			SetWhiteTextStyle(vector3Field);
			vector3Field.userData = param;
			container.Add(vector3Field);
		}
		else if (param.Type == typeof(Color))
		{
#if UNITY_EDITOR
			var colorField = new ColorField(param.DisplayName) { value = (Color)(param.Value ?? Color.white) };
			SetWhiteTextStyle(colorField);
			colorField.userData = param;
			container.Add(colorField);
#else
			// For runtime builds, use a simple text field to show/edit color as string
			var color = (Color)(param.Value ?? Color.white);
			var colorText = new TextField(param.DisplayName) { value = $"#{ColorUtility.ToHtmlStringRGBA(color)}" };
			SetWhiteTextStyle(colorText);
			colorText.userData = param;
			container.Add(colorText);
#endif
		}
		else if (param.Type.IsEnum)
		{
			var enumField = new EnumField(param.DisplayName, (System.Enum)(param.Value ?? System.Activator.CreateInstance(param.Type)));
			SetWhiteTextStyle(enumField);
			enumField.userData = param;
			container.Add(enumField);
		}
		
		// Add tooltip if available
		if (!string.IsNullOrEmpty(param.Tooltip))
		{
			var tooltipLabel = new Label($"ℹ {param.Tooltip}");
			tooltipLabel.style.fontSize = 12;
			tooltipLabel.style.color = Color.white;
			tooltipLabel.style.whiteSpace = WhiteSpace.Normal;
			container.Add(tooltipLabel);
		}
		
		return container;
	}

	private void SetWhiteTextStyle(VisualElement element)
	{
		// Set label color to white for the field
		element.style.color = Color.white;
		
		// For fields with labels, also set the label text color
		var label = element.Q<Label>();
		if (label != null)
		{
			label.style.color = Color.white;
		}
		
		// For text input fields, ensure the text color is white
		if (element is TextField textField)
		{
			var textInput = textField.Q<VisualElement>("unity-text-input");
			if (textInput != null)
			{
				textInput.style.color = Color.white;
			}
		}
		else if (element is IntegerField intField)
		{
			var textInput = intField.Q<VisualElement>("unity-text-input");
			if (textInput != null)
			{
				textInput.style.color = Color.white;
			}
		}
		else if (element is FloatField floatField)
		{
			var textInput = floatField.Q<VisualElement>("unity-text-input");
			if (textInput != null)
			{
				textInput.style.color = Color.white;
			}
		}
	}

	private void ApplyConfigurationChanges(VisualizerInfo visualizer)
	{
		if (visualizer?.Component == null || pluginConfigContent == null) return;
		
		// Find all config elements and apply their values
		var elements = pluginConfigContent.Query<VisualElement>().Where(e => e.userData is ConfigParameter).ToList();
		
		foreach (var element in elements)
		{
			var param = (ConfigParameter)element.userData;
			object newValue = null;
			
			if (element is TextField textField)
				newValue = textField.value;
			else if (element is IntegerField intField)
				newValue = intField.value;
			else if (element is FloatField floatField)
				newValue = floatField.value;
			else if (element is Toggle toggle)
				newValue = toggle.value;
			else if (element is Vector3Field vector3Field)
				newValue = vector3Field.value;
#if UNITY_EDITOR
			else if (element is ColorField colorField)
				newValue = colorField.value;
#else
			else if (element is TextField textField && param.Type == typeof(Color))
			{
				// Parse color from hex string in runtime builds
				if (ColorUtility.TryParseHtmlString(textField.value, out Color parsedColor))
					newValue = parsedColor;
			}
#endif
			else if (element is EnumField enumField)
				newValue = enumField.value;
			
			if (newValue != null && param.Field != null)
			{
				try
				{
					param.Field.SetValue(visualizer.Component, newValue);
					Debug.Log($"Set {param.Field.Name} = {newValue} on {visualizer.Name}");
				}
				catch (System.Exception e)
				{
					Debug.LogError($"Failed to set {param.Field.Name}: {e.Message}");
				}
			}
		}
		
		// Mark the component as dirty for Unity to recognize changes
#if UNITY_EDITOR
		UnityEditor.EditorUtility.SetDirty(visualizer.Component);
#endif
	}

	#endregion
}
