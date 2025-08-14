// using UnityEngine;
// using UnityEngine.UIElements;
// using System;
// using System.Collections.Generic;
// using System.Linq;

// public class AddPluginListController : MonoBehaviour
// {
// 	[SerializeField] private UIDocument doc;
// 	[SerializeField] private VisualTreeAsset addPluginCardTemplate; // Assign AddPluginCard.uxml
// 	[SerializeField] private float cardRowHeight = 72f;

// 	// Represents a candidate plugin to add
// 	[Serializable]
// 	public class PluginInfo
// 	{
// 		public string Id;
// 		public string DisplayName;
// 	}

// 	[SerializeField] private List<PluginInfo> availablePlugins = new();

// 	public Action<PluginInfo> OnAddPlugin; // Consumer can subscribe to handle add
// 	public Action OnBack;                  // Consumer can subscribe for back navigation

// 	private ListView _list;
// 	private Button _backButton;

// 	void OnEnable()
// 	{
// 		if (doc == null)
// 			doc = GetComponent<UIDocument>();

// 		var root = doc.rootVisualElement;
// 		_list = root.Q<ListView>("addPluginCardsList");
// 		_backButton = root.Q<Button>("addPluginBackButton");
// 		if (_backButton != null)
// 		{
// 			_backButton.clicked += () => OnBack?.Invoke();
// 		}

// 		_list.fixedItemHeight = cardRowHeight;
// 		_list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;

// 		_list.makeItem = () => addPluginCardTemplate.Instantiate();

// 		_list.bindItem = (e, i) =>
// 		{
// 			if (i < 0 || i >= availablePlugins.Count) return;
// 			var info = availablePlugins[i];

// 			var nameLabel = e.Q<Label>("pluginName");
// 			if (nameLabel != null)
// 			{
// 				nameLabel.text = info.DisplayName;
// 				nameLabel.style.color = Color.white;
// 			}

// 			var addBtn = e.Q<Button>("addPluginButton");
// 			if (addBtn != null)
// 			{
// 				// Clear old click handlers
// 				addBtn.clicked -= addBtn.userData as Action;
// 				Action click = () => OnAddPlugin?.Invoke(info);
// 				addBtn.userData = click;
// 				addBtn.clicked += click;
// 			}
// 		};

// 		_list.unbindItem = (e, i) =>
// 		{
// 			var addBtn = e.Q<Button>("addPluginButton");
// 			if (addBtn != null && addBtn.userData is Action click)
// 				addBtn.clicked -= click;
// 		};

// 		_list.itemsSource = availablePlugins;
// 		_list.selectionType = SelectionType.None;

// 		_list.RefreshItems();
// 		root.schedule.Execute(() => _list.RefreshItems()).ExecuteLater(0);
// 	}

// 	public void SetAvailablePlugins(IEnumerable<PluginInfo> plugins)
// 	{
// 		availablePlugins = plugins?.ToList() ?? new List<PluginInfo>();
// 		if (_list != null)
// 		{
// 			_list.itemsSource = availablePlugins;
// 			_list.RefreshItems();
// 		}
// 	}
// }
