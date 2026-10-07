using System.Collections.Generic;
using System.Text;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Market;

// Market window, open while a market is selected: a field for every resource to pick what to give and what to get,
// what the trade gives at the current prices, and the market's queue as a list (repeat keeps trading). Uses the
// game's own crafting commands.
[Feature(Features.Market, Features.MarketInfo)]
internal class MarketPanel : MonoBehaviour
{
	private class Tile
	{
		public MarketPrices.Good Good;

		public Button Button;

		public Text Stock;
	}

	private class QueueRow
	{
		public GameObject Row;

		public Image GiveIcon;

		public Image GetIcon;

		public Text Text;
	}

	private const float RefreshSeconds = 0.3f;

	private const int MaxQueue = 8;

	private const int Columns = 5;

	private const float TileWidth = 92f;

	private const float TileHeight = 62f;

	private static readonly Color Bad = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static readonly Color TileNormal = new Color(0.16f, 0.16f, 0.19f, 1f);

	private static readonly Color TileSelected = new Color(0.55f, 0.43f, 0.12f, 1f);

	private GameObject _window;

	private MarketStation _station;

	private int _give;

	private int _get = 1;

	private readonly List<Tile> _giveTiles = new List<Tile>();

	private readonly List<Tile> _getTiles = new List<Tile>();

	private Image _tradeGiveIcon;

	private Image _tradeGetIcon;

	private Text _tradeText;

	private Text _priceText;

	private Text _queueTitle;

	private Text _queueEmpty;

	private Transform _queueList;

	private readonly List<QueueRow> _queueRows = new List<QueueRow>();

	private string _queueShown;

	private Text _repeatLabel;

	private Button _queueButton;

	private float _nextRefresh;

	private readonly Dictionary<MarketPrices.Good, int> _stock = new Dictionary<MarketPrices.Good, int>();

	private static void Enable()
	{
		Plugin.Root.AddComponent<MarketPanel>();
	}

	private void Update()
	{
		CraftingStation selected = (UIGameObserver.crafting != null) ? UIGameObserver.crafting.CurrentSelected : null;
		MarketStation market = (!selected.IsNullOrReleased()) ? (selected as MarketStation) : null;
		// By reference: a destroyed station equals null for Unity, which would keep it and leave the window open.
		if (!ReferenceEquals(market, _station))
		{
			_station = market;
			if (_window == null && market != null)
			{
				BuildWindow();
			}
			if (_window != null)
			{
				_window.SetActive(market != null);
			}
			_queueShown = null;
			_nextRefresh = 0f;
		}
		if (_station != null && Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private MarketPrices.Trade CurrentTrade()
	{
		if (MarketPrices.Goods.Count < 2)
		{
			return null;
		}
		return MarketPrices.TradeFor(MarketPrices.Goods[_give], MarketPrices.Goods[_get]);
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		CountStock();
		RefreshTiles(_giveTiles, _give, _get, false);
		RefreshTiles(_getTiles, _get, _give, true);
		RefreshQueue();
		_repeatLabel.text = "Repeat: " + (_station.Looping ? "on" : "off");
		MarketPrices.Trade trade = CurrentTrade();
		if (trade == null)
		{
			_tradeText.text = "No trades available.";
			_tradeText.color = Bad;
			_priceText.text = string.Empty;
			_queueButton.interactable = false;
			return;
		}
		_tradeGiveIcon.sprite = trade.Give.Icon.Get64();
		_tradeGetIcon.sprite = trade.Get.Icon.Get64();
		_tradeText.text = trade.GiveAmount + " " + trade.Give.Name + "  ->  " + MarketPrices.Output(trade) + " " + trade.Get.Name;
		_tradeText.color = Yellow;
		_priceText.text = "Price level: " + Level(trade.Give) + ", " + Level(trade.Get);
		_queueButton.interactable = _station.QueueCount < MaxQueue;
	}

	private void RefreshTiles(List<Tile> tiles, int selected, int other, bool get)
	{
		for (int i = 0; i < tiles.Count; i++)
		{
			Tile tile = tiles[i];
			tile.Stock.text = Stock(tile.Good).ToString();
			SetTileColor(tile.Button, (i == selected) ? TileSelected : TileNormal);
			// The resource picked on the other side cannot be traded for itself, and some cannot be bought (steel).
			tile.Button.interactable = i != other && (!get || tile.Good.Buyable);
		}
	}

	// The queue as a list, one row per queued trade; rebuilt only when the queue changes.
	private void RefreshQueue()
	{
		List<RecipeInfo> queue = _station.QueuedRecipes;
		int count = (queue != null) ? queue.Count : 0;
		StringBuilder key = new StringBuilder();
		for (int i = 0; i < count; i++)
		{
			key.Append(queue[i].id).Append(',');
		}
		_queueTitle.text = "QUEUE (" + count + "/" + MaxQueue + ")";
		_queueEmpty.gameObject.SetActive(count == 0);
		if (key.ToString() != _queueShown)
		{
			_queueShown = key.ToString();
			foreach (QueueRow row in _queueRows)
			{
				Destroy(row.Row);
			}
			_queueRows.Clear();
			for (int i = 0; i < count; i++)
			{
				_queueRows.Add(CreateQueueRow(i));
			}
		}
		for (int i = 0; i < _queueRows.Count; i++)
		{
			MarketPrices.Trade trade = MarketPrices.TradeFor(queue[i]);
			QueueRow row = _queueRows[i];
			if (trade == null)
			{
				row.Text.text = "?";
				continue;
			}
			row.GiveIcon.sprite = trade.Give.Icon.Get64();
			row.GetIcon.sprite = trade.Get.Icon.Get64();
			row.Text.text = trade.GiveAmount + " " + trade.Give.Name + "  ->  " + MarketPrices.Output(trade) + " " + trade.Get.Name + ((i == 0) ? "  <color=#999999>next</color>" : string.Empty);
		}
	}

	private static string Level(MarketPrices.Good good)
	{
		int markup = MarketPrices.Markup(good);
		return good.Name + ((markup > 0) ? (" +" + markup + "%") : " normal");
	}

	private int Stock(MarketPrices.Good good)
	{
		_stock.TryGetValue(good, out int count);
		return count;
	}

	// The local player's stockpiles.
	private void CountStock()
	{
		_stock.Clear();
		var stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles == null)
		{
			return;
		}
		foreach (GameObject go in stockpiles)
		{
			if (go == null || !go.activeInHierarchy || !Affiliation.BelongsToMe(go))
			{
				continue;
			}
			Recepteur recepteur = go.GetComponent<Recepteur>();
			if (recepteur == null)
			{
				continue;
			}
			foreach (MarketPrices.Good good in MarketPrices.Goods)
			{
				_stock.TryGetValue(good, out int count);
				_stock[good] = count + recepteur.ContentDescription.Value(good.Resource);
			}
		}
	}

	private void Select(bool give, int index)
	{
		if (give)
		{
			_give = index;
		}
		else
		{
			_get = index;
		}
		_nextRefresh = 0f;
	}

	private void Swap()
	{
		int give = _give;
		_give = _get;
		_get = give;
		// What was given cannot always be bought (steel): then the first good that can.
		if (!MarketPrices.Goods[_get].Buyable)
		{
			for (int i = 0; i < MarketPrices.Goods.Count; i++)
			{
				if (i != _give && MarketPrices.Goods[i].Buyable)
				{
					_get = i;
					break;
				}
			}
		}
		_nextRefresh = 0f;
	}

	private static UNetProjectCmd Commands => (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;

	private void QueueTrade()
	{
		MarketPrices.Trade trade = CurrentTrade();
		if (trade == null || _station == null || Commands == null)
		{
			return;
		}
		Commands.CallCmdcraftingQueueRecipe(_station.gameObject, trade.Recipe.id, 1);
		_nextRefresh = Time.unscaledTime + 0.1f;
	}

	private void RemoveQueued(int index)
	{
		if (_station != null && Commands != null)
		{
			Commands.CallCmdcraftingRemoveAt(_station.gameObject, index);
			_nextRefresh = Time.unscaledTime + 0.1f;
		}
	}

	private void ToggleRepeat()
	{
		if (_station != null && Commands != null)
		{
			Commands.CallCmdcraftingLoop(_station.gameObject, !_station.Looping);
			_nextRefresh = Time.unscaledTime + 0.1f;
		}
	}

	private void ClearQueue()
	{
		if (_station != null && Commands != null)
		{
			Commands.CallCmdcraftingDoNothing(_station.gameObject);
			_nextRefresh = Time.unscaledTime + 0.1f;
		}
	}

	// The window follows the selection, so closing it deselects the market.
	private void Close()
	{
		if (_station != null)
		{
			UIGameSelector.Unselect(_station.gameObject);
		}
	}

	// ---- window

	private void BuildWindow()
	{
		GameObject canvasGo = CreateCanvas("MarketCanvas", transform, 900);
		_window = CreateWindow(canvasGo.transform, Columns * TileWidth + (Columns - 1) * 4f + 28f);
		RectTransform windowRect = _window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(1f, 0.5f);
		windowRect.anchorMax = new Vector2(1f, 0.5f);
		windowRect.pivot = new Vector2(1f, 0.5f);
		windowRect.anchoredPosition = new Vector2(-64f, 0f);

		Transform header = CreateRow(_window.transform, 30f);
		Text title = CreateText(header, "MARKET", 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		CreateButton(header, "X", 28f, Close);

		Transform giveHeader = CreateRow(_window.transform, 24f);
		Text giveLabel = CreateText(giveHeader, "GIVE", 12, Grey, TextAnchor.LowerLeft);
		Flexible(giveLabel.gameObject);
		CreateButton(giveHeader, "Swap", 70f, Swap);
		AddGrid(_giveTiles, true);

		AddSection("GET");
		AddGrid(_getTiles, false);

		Transform tradeRow = CreateRow(_window.transform, 34f);
		_tradeGiveIcon = AddIcon(tradeRow, 30f);
		_tradeGetIcon = AddIcon(tradeRow, 30f);
		_tradeText = CreateText(tradeRow, string.Empty, 15, Yellow, TextAnchor.MiddleLeft);
		_tradeText.horizontalOverflow = HorizontalWrapMode.Wrap;
		Flexible(_tradeText.gameObject);
		_priceText = AddNote(string.Empty);
		_priceText.color = TextColor;

		Transform buttons = CreateRow(_window.transform, 30f);
		_queueButton = CreateButton(buttons, "Add to queue", 0f, QueueTrade);
		Flexible(_queueButton.gameObject);
		Button repeat = CreateButton(buttons, "Repeat: off", 110f, ToggleRepeat);
		_repeatLabel = repeat.GetComponentInChildren<Text>();
		CreateButton(buttons, "Clear", 70f, ClearQueue);

		_queueTitle = AddSection("QUEUE");
		_queueList = CreateRect("Queue", _window.transform);
		VerticalLayoutGroup queueLayout = _queueList.gameObject.AddComponent<VerticalLayoutGroup>();
		queueLayout.spacing = 2f;
		queueLayout.childControlWidth = true;
		queueLayout.childControlHeight = true;
		queueLayout.childForceExpandWidth = true;
		queueLayout.childForceExpandHeight = false;
		_queueEmpty = AddNote("The queue is empty. Pick what to give and what to get, then add the trade.");

		Text note = AddNote("A worker brings what you give to the market and trades it. Every trade costs a " + Mathf.RoundToInt(MarketPrices.Fee.Value * 100f) + "% fee, and a resource gets more expensive the more it is traded, back to normal over about " + MarketPrices.RecoveryMinutes.Value.ToString("0.#") + " minutes (half way). You always get less value back than you give.");
		note.color = Grey;
	}

	// One field per resource: icon, name and how many the stockpiles hold. Click to pick it.
	private void AddGrid(List<Tile> tiles, bool give)
	{
		RectTransform grid = CreateRect("Grid", _window.transform);
		GridLayoutGroup layout = grid.gameObject.AddComponent<GridLayoutGroup>();
		layout.cellSize = new Vector2(TileWidth, TileHeight);
		layout.spacing = new Vector2(4f, 4f);
		layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
		layout.constraintCount = Columns;
		for (int i = 0; i < MarketPrices.Goods.Count; i++)
		{
			MarketPrices.Good good = MarketPrices.Goods[i];
			int index = i;
			RectTransform rect = CreateRect("Tile " + good.Name, grid);
			Image background = rect.gameObject.AddComponent<Image>();
			background.color = Color.white;
			Button button = rect.gameObject.AddComponent<Button>();
			button.targetGraphic = background;
			button.onClick.AddListener(() => Select(give, index));
			SetTileColor(button, TileNormal);

			Image icon = CreateRect("Icon", rect).gameObject.AddComponent<Image>();
			icon.sprite = good.Icon.Get64();
			icon.preserveAspect = true;
			icon.raycastTarget = false;
			SetRect(icon.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -17f), new Vector2(28f, 28f));

			Text stock = CreateText(rect, string.Empty, 12, Yellow, TextAnchor.UpperRight);
			SetRect(stock.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-4f, -3f), new Vector2(-8f, 16f));

			Text name = CreateText(rect, good.Name, 11, TextColor, TextAnchor.LowerCenter);
			name.horizontalOverflow = HorizontalWrapMode.Wrap;
			SetRect(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 10f), new Vector2(-4f, 18f));

			tiles.Add(new Tile
			{
				Good = good,
				Button = button,
				Stock = stock
			});
		}
	}

	private static void SetTileColor(Button button, Color normal)
	{
		ColorBlock colors = button.colors;
		if (colors.normalColor == normal)
		{
			return;
		}
		colors.normalColor = normal;
		colors.highlightedColor = normal + new Color(0.1f, 0.1f, 0.1f, 0f);
		colors.pressedColor = TileSelected;
		colors.disabledColor = new Color(0.1f, 0.1f, 0.12f, 0.6f);
		colors.colorMultiplier = 1f;
		button.colors = colors;
	}

	private QueueRow CreateQueueRow(int index)
	{
		Transform row = CreateRow(_queueList, 26f);
		Text number = CreateText(row, (index + 1) + ".", 12, Grey, TextAnchor.MiddleRight);
		Fixed(number.gameObject, 20f, 26f);
		Image giveIcon = AddIcon(row, 22f);
		Image getIcon = AddIcon(row, 22f);
		Text text = CreateText(row, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		text.supportRichText = true;
		Flexible(text.gameObject);
		CreateButton(row, "X", 24f, () => RemoveQueued(index));
		return new QueueRow
		{
			Row = row.gameObject,
			GiveIcon = giveIcon,
			GetIcon = getIcon,
			Text = text
		};
	}

	private static Image AddIcon(Transform row, float size)
	{
		Image icon = CreateRect("Icon", row).gameObject.AddComponent<Image>();
		icon.preserveAspect = true;
		icon.raycastTarget = false;
		Fixed(icon.gameObject, size, size);
		return icon;
	}

	private Text AddSection(string text)
	{
		Transform row = CreateRow(_window.transform, 24f);
		Text label = CreateText(row, text, 12, Grey, TextAnchor.LowerLeft);
		Flexible(label.gameObject);
		return label;
	}

	private Text AddNote(string text)
	{
		Text note = CreateText(_window.transform, text, 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		return note;
	}
}
