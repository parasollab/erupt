// using UnityEngine;
// using UnityEngine.UIElements;
// using System;
// using System.Collections.Generic;
// using System.Linq;

// public class CardListController : MonoBehaviour
// {
// 	[SerializeField] UIDocument doc;
// 	[SerializeField] VisualTreeAsset cardTemplate;  // drag CardItem.uxml in Inspector
// 	[SerializeField] float cardRowHeight = 80f;     // keep in sync with USS card height
// 	[SerializeField] int initialVisibleCount = 3;   // show only this many initially

// 	public Action<CardData> OnCardActivated;        // plug in your custom action

// 	List<CardData> _allItems;    // full dataset
// 	List<CardData> _visibleItems; // items currently displayed
// 	ListView _list;
// 	Button _addButton;

// 	void OnEnable()
// 	{
// 		// Pre-seeded full dataset
// 		_allItems = new()
// 		{
// 			new CardData("Planner",  true),
// 			new CardData("Mapper",   false),
// 			new CardData("Localize", true),
// 			new CardData("A", true),
// 			new CardData("B", true),
// 			new CardData("C", true),
// 			new CardData("D", true),
// 			new CardData("E", true),
// 			new CardData("F", true),
// 			new CardData("G", true),
// 			new CardData("H", true),
// 		};

// 		// Start by showing only the first N items
// 		_visibleItems = _allItems.Take(Mathf.Clamp(initialVisibleCount, 0, _allItems.Count)).ToList();

// 		var root = doc.rootVisualElement;
// 		_list = root.Q<ListView>("activePluginCardsList");
// 		_addButton = root.Q<Button>("addPluginButton");
// 		if (_addButton != null)
// 		{
// 			// _addButton.clicked += ShowNextCard;
//             _addButton.clicked += ShowAddPluginView;
// 		}

// 		// Ensure each row matches the card's height exactly
// 		_list.fixedItemHeight = cardRowHeight;
// 		_list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;

// 		// Use makeItem/bindItem to control creation & binding
// 		_list.makeItem = () =>
// 		{
// 			// Instantiate the card template for each row
// 			var container = cardTemplate.Instantiate(); // TemplateContainer derives from VisualElement
// 			return container;
// 		};

// 		_list.bindItem = (e, i) =>
// 		{
// 			var card   = e.Q<VisualElement>("card");
// 			var toggle = e.Q<Toggle>("enable");
// 			var title  = e.Q<Label>("title");

// 			// Guard in case of async refreshes
// 			if (i < 0 || i >= _visibleItems.Count) return;

// 			// Ensure label is visible and set text immediately
// 			if (title != null)
// 			{
// 				title.text = _visibleItems[i].Name;
// 				title.style.color = Color.white;
// 				title.style.display = DisplayStyle.Flex;
// 				title.style.visibility = Visibility.Visible;
// 				title.style.opacity = 1f;
// 				title.MarkDirtyRepaint();
// 			}

// 			// Set toggle without firing change callback
// 			if (toggle != null)
// 			{
// 				toggle.SetValueWithoutNotify(_visibleItems[i].Enabled);
// 				// prevent toggle from stealing selection
// 				toggle.RegisterCallback<PointerDownEvent>(evt => evt.StopImmediatePropagation());
// 				toggle.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());

// 				// Rebind toggle change handler safely
// 				if (toggle.userData is EventCallback<ChangeEvent<bool>> oldCb)
// 					toggle.UnregisterValueChangedCallback(oldCb);

// 				EventCallback<ChangeEvent<bool>> cb = evt => _visibleItems[i].Enabled = evt.newValue;
// 				toggle.userData = cb;
// 				toggle.RegisterValueChangedCallback(cb);
// 			}

// 			// Visual selection state
// 			if (card != null)
// 			{
// 				SetSelectedVisual(card, _list.selectedIndex == i);
// 				card.MarkDirtyRepaint();
// 			}
// 		};

// 		_list.unbindItem = (e, i) =>
// 		{
// 			var toggle = e.Q<Toggle>("enable");
// 			if (toggle?.userData is EventCallback<ChangeEvent<bool>> oldCb)
// 				toggle.UnregisterValueChangedCallback(oldCb);
// 		};

// 		// Provide data & selection mode AFTER binding is set
// 		_list.itemsSource   = _visibleItems;
// 		_list.selectionType = SelectionType.Single;

// 		// Keep selection highlight in sync
// 		_list.onSelectionChange += _ =>
// 		{
// 			_list.RefreshItems();
// 		};

// 		// Activate action (double-click or Enter)
// 		_list.onItemsChosen += chosen =>
// 		{
// 			var item = chosen.FirstOrDefault() as CardData;
// 			if (item != null) OnCardActivated?.Invoke(item);
// 		};

// 		// Optional: single-click activation on the whole row (excluding the toggle)
// 		_list.Q<ListView>().RegisterCallback<ClickEvent>(evt =>
// 		{
// 			var ve = evt.target as VisualElement;
// 			if (ve == null) return;
// 			if (ve is Toggle || ve.GetFirstOfType<Toggle>() != null) return;
// 			var clickedIndex = _list.selectedIndex;
// 			if (clickedIndex >= 0 && clickedIndex < _visibleItems.Count)
// 				OnCardActivated?.Invoke(_visibleItems[clickedIndex]);
// 		});

// 		// Force initial bind/paint immediately and next frame
// 		_list.RefreshItems();
// 		root.schedule.Execute(() => _list.RefreshItems()).ExecuteLater(0);
// 	}

// 	void ShowNextCard()
// 	{
// 		if (_visibleItems.Count >= _allItems.Count) return; // nothing more to add
// 		_visibleItems.Add(_allItems[_visibleItems.Count]);
// 		_list.itemsSource = _visibleItems; // reassign to be safe
// 		_list.RefreshItems();
// 	}

// 	void SetSelectedVisual(VisualElement card, bool selected)
// 	{
// 		if (selected) card.AddToClassList("is-selected");
// 		else          card.RemoveFromClassList("is-selected");
// 	}

// 	// Optional: add to the full dataset at runtime; does not auto-show
// 	public void AddCard(string name, bool enabled = false)
// 	{
// 		_allItems.Add(new CardData(name, enabled));
// 		// To also show immediately, uncomment next two lines:
// 		// _visibleItems.Add(_allItems[^1]);
// 		// _list.RefreshItems();
// 	}

// 	[Serializable] public class CardData
// 	{
// 		public string Name;
// 		public bool Enabled;
// 		public CardData(string name, bool enabled) { Name = name; Enabled = enabled; }
// 	}
// }
