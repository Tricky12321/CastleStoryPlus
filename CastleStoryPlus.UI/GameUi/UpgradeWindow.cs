using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using CastleStoryPlus.Upgrades;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// The window of an upgrade building (research station, smithy, armoury): its upgrades in groups on top (research:
// Colony, Workers, Crystal; smithy: Melee, Ranged; armoury: Body, Helm and shield), the group's upgrades as cards
// with a dot per researched tier, and under them the chosen upgrade's tiers side by side (done, the next with its
// price as the button that starts it, the later ones with their price). The station's queue runs along the bottom
// with a bar for the research under way; a click on a queued research takes it out. Starting and cancelling go
// through UpgradeOrders (the game's crafting commands; the host refuses a locked or unpaid research). While these
// windows exist the old upgrade window (UpgradePanel) is off.
internal class CsUpgradeWindow : CsStationWindow
{
	private sealed class Group
	{
		public string Name;

		public readonly List<UpgradeLine> Lines = new List<UpgradeLine>();
	}

	private sealed class Card
	{
		public UpgradeLine Line;

		public GameObject Root;

		public Image Background;

		public Image Icon;

		public Text Name;

		public Image[] Pips;
	}

	private sealed class Tier
	{
		public GameObject Root;

		public Image Background;

		public Text Label;

		public Text Value;

		public Image Status;

		// The price: a count and an icon per material (only the first text for "Done" and the like).
		public Text[] StatusTexts;

		public Image[] StatusIcons;

		// What a click on the status does now (null: nothing).
		public System.Action Click;

		public string Tooltip;
	}

	private const int Columns = 3;

	private static readonly Color PipOff = new Color(1f, 1f, 1f, 0.16f);

	private static readonly Color Bad = new Color(0.9f, 0.36f, 0.26f, 1f);

	private readonly List<Group> _groups = new List<Group>();

	private readonly List<Card> _cards = new List<Card>();

	private readonly List<Image> _tabs = new List<Image>();

	private readonly List<Text> _tabTexts = new List<Text>();

	private readonly Tier[] _tiers = new Tier[UpgradeLines.MaxTier];

	private readonly List<Image> _queueSlots = new List<Image>();

	private int _group;

	private UpgradeLine _line;

	private Text _lineName;

	private Text _lineText;

	private RectTransform _bar;

	private RectTransform _barFill;

	private Text _barText;

	private Text _queueEmpty;

	private const int MaxMaterials = 3;

	// How many of these windows exist (the old upgrade window is off while any does).
	private static int _windows;

	private UpgradeHall _hall;

	private Sprite _crystal;

	protected override float Width
	{
		get { return 440f; }
	}

	public static CsUpgradeWindow Create(Transform parent, UpgradeHall hall)
	{
		CsUpgradeWindow window = new GameObject("CS " + hall, typeof(RectTransform)).AddComponent<CsUpgradeWindow>();
		window._hall = hall;
		switch (hall)
		{
			case UpgradeHall.Smithy:
				window.Setup(parent, "CS smithy", BuildCatalog.ModIcon("smithy", "_Forge"), "Smithy");
				break;
			case UpgradeHall.Armoury:
				window.Setup(parent, "CS armoury", BuildCatalog.ModIcon("armoury", "_Shield"), "Armoury");
				break;
			default:
				window.Setup(parent, "CS research", BuildCatalog.ModIcon("research", "_Lab"), "Research Station");
				break;
		}
		_windows++;
		OldPanel(false);
		return window;
	}

	protected override void OnDestroy()
	{
		_windows--;
		if (_windows <= 0)
		{
			OldPanel(true);
		}
		base.OnDestroy();
	}

	// The old upgrade window: off while the C# menu has its own.
	private static void OldPanel(bool on)
	{
		UpgradePanel.ResearchElsewhere = !on;
		UpgradePanel panel = (Plugin.Root != null) ? Plugin.Root.GetComponent<UpgradePanel>() : null;
		if (panel != null && panel.enabled != on)
		{
			panel.enabled = on;
			if (!on)
			{
				// Its window stays as it was when it stops: hide any it has open.
				foreach (Transform child in panel.transform)
				{
					if (child.name == "UpgradeCanvas")
					{
						child.gameObject.SetActive(false);
					}
				}
			}
			else
			{
				foreach (Transform child in panel.transform)
				{
					if (child.name == "UpgradeCanvas")
					{
						child.gameObject.SetActive(true);
					}
				}
			}
		}
	}

	protected override CraftingStation Pick(CraftingStation selected)
	{
		UpgradeStation station = selected as UpgradeStation;
		return (station != null && station.Hall == _hall) ? station : null;
	}

	// ---- build

	private void MakeGroups()
	{
		switch (_hall)
		{
			case UpgradeHall.Smithy:
				AddGroup("Melee", UpgradeLines.SharpenedBlades, UpgradeLines.HeavyBlades);
				AddGroup("Ranged", UpgradeLines.Fletching, UpgradeLines.SteadyAim, UpgradeLines.Winch);
				break;
			case UpgradeHall.Armoury:
				AddGroup("Body", UpgradeLines.Chainmail, UpgradeLines.Gambeson, UpgradeLines.WardedPlate);
				AddGroup("Helm and shield", UpgradeLines.Helmet, UpgradeLines.ShieldRims);
				break;
			default:
				AddGroup("Colony", UpgradeLines.Housing, UpgradeLines.Stacking, UpgradeLines.Scouting);
				AddGroup("Workers", UpgradeLines.WorkMethods, UpgradeLines.LightBoots, UpgradeLines.Sprint, UpgradeLines.EfficientMining);
				AddGroup("Crystal", UpgradeLines.CrystalAttunement, UpgradeLines.FireflyLore, UpgradeLines.Giants);
				break;
		}
		// An upgrade of the building that no group names (added later) goes in the last group.
		foreach (UpgradeLine line in UpgradeLines.In(_hall))
		{
			if (!_groups.Exists((Group g) => g.Lines.Contains(line)))
			{
				_groups[_groups.Count - 1].Lines.Add(line);
			}
		}
		_groups.RemoveAll((Group g) => g.Lines.Count == 0);
	}

	private void AddGroup(string name, params int[] lines)
	{
		Group group = new Group { Name = name };
		foreach (int index in lines)
		{
			if (index < UpgradeLines.All.Count && UpgradeLines.All[index].Hall == _hall)
			{
				group.Lines.Add(UpgradeLines.All[index]);
			}
		}
		_groups.Add(group);
	}

	protected override void Build()
	{
		MakeGroups();
		foreach (UpgradeLine line in UpgradeLines.In(UpgradeHall.Research))
		{
			if (_hall == UpgradeHall.Research && line.Cost[1] != null && line.Cost[1].Length > 0 && line.Cost[1][0].Icon != null)
			{
				_crystal = line.Cost[1][0].Icon.Get64();
				break;
			}
		}
		BuildTabs();
		int most = 0;
		foreach (Group group in _groups)
		{
			most = Mathf.Max(most, group.Lines.Count);
		}
		float cell = (Width - 24f - (Columns - 1) * 6f) / Columns;
		RectTransform grid = Grid(Body, new Vector2(cell, 84f), 6f, Columns);
		for (int i = 0; i < most; i++)
		{
			_cards.Add(BuildCard(grid, i));
		}
		Line(Body);
		_lineName = LuiWidgets.Label(Body, string.Empty, 16, LuiStyle.White, TextAnchor.MiddleLeft, LuiStyle.Title);
		_lineText = Note(Body, string.Empty);
		_lineText.color = UiTheme.TextMuted;
		Transform ladder = Row(Body, 74f, 6f);
		HorizontalLayoutGroup ladderLayout = ladder.GetComponent<HorizontalLayoutGroup>();
		ladderLayout.childForceExpandWidth = true;
		ladderLayout.childForceExpandHeight = true;
		for (int i = 0; i < _tiers.Length; i++)
		{
			_tiers[i] = BuildTier(ladder, i + 1);
		}
		Line(Body);
		BuildQueue();
		Note(Body, "Starting a research reserves its price from the stockpiles at once; a worker brings it here and works on it, one at a time. Each tier needs the one before and works for the whole " + ((_hall == UpgradeHall.Research) ? "colony" : "team") + ".");
		Choose(0);
	}

	private void BuildTabs()
	{
		RectTransform tabs = UiKit.CreateRect("Groups", Body);
		HorizontalLayoutGroup layout = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(3, 3, 3, 3);
		layout.spacing = 2f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = true;
		LayoutElement size = tabs.gameObject.AddComponent<LayoutElement>();
		size.minHeight = 30f;
		size.preferredHeight = 30f;
		UiTheme.Round(tabs.gameObject.AddComponent<Image>(), UiTheme.BgSlot, UiTheme.PillRadius + 2);
		for (int i = 0; i < _groups.Count; i++)
		{
			int index = i;
			RectTransform tab = UiKit.CreateRect("Tab " + _groups[i].Name, tabs);
			Image background = tab.gameObject.AddComponent<Image>();
			UiTheme.Round(background, LuiStyle.BgNone, UiTheme.PillRadius);
			Text text = LuiWidgets.Label(tab, _groups[i].Name, LuiStyle.FontSize - 1, UiTheme.TextMuted, TextAnchor.MiddleCenter, LuiStyle.Bold);
			UiKit.SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			Clickable(tab.gameObject, () => Choose(index), null);
			_tabs.Add(background);
			_tabTexts.Add(text);
		}
	}

	private Card BuildCard(Transform grid, int index)
	{
		RectTransform rect = UiKit.CreateRect("Card " + index, grid);
		Card card = new Card { Root = rect.gameObject, Pips = new Image[UpgradeLines.MaxTier] };
		card.Background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(card.Background, UiTheme.BgBlock, UiTheme.ButtonRadius);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(4, 4, 6, 6);
		layout.spacing = 3f;
		layout.childAlignment = TextAnchor.UpperCenter;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		RectTransform slot = CsRowParts.Slot(rect, 40f);
		card.Icon = CsRowParts.Picture(slot, 34f);
		card.Name = LuiWidgets.Label(rect, string.Empty, LuiStyle.SmallFontSize + 1, LuiStyle.White, TextAnchor.MiddleCenter, LuiStyle.Bold);
		RectTransform pips = UiKit.CreateRect("Pips", rect);
		HorizontalLayoutGroup pipLayout = pips.gameObject.AddComponent<HorizontalLayoutGroup>();
		pipLayout.spacing = 4f;
		pipLayout.childControlWidth = true;
		pipLayout.childControlHeight = true;
		pipLayout.childForceExpandWidth = false;
		pipLayout.childForceExpandHeight = false;
		for (int i = 0; i < card.Pips.Length; i++)
		{
			RectTransform pip = UiKit.CreateRect("Pip", pips);
			UiKit.Fixed(pip.gameObject, 8f, 8f);
			card.Pips[i] = pip.gameObject.AddComponent<Image>();
			UiTheme.Round(card.Pips[i], PipOff, 4);
			card.Pips[i].raycastTarget = false;
		}
		Clickable(card.Root, () =>
		{
			if (card.Line != null)
			{
				_line = card.Line;
				Soon();
			}
		}, () => (card.Line != null) ? (card.Line.Name + ": " + card.Line.Text) : null);
		return card;
	}

	private Tier BuildTier(Transform ladder, int tier)
	{
		RectTransform rect = UiKit.CreateRect("Tier " + tier, ladder);
		Tier view = new Tier { Root = rect.gameObject };
		// The same width for every tier, whatever its texts.
		LayoutElement size = rect.gameObject.AddComponent<LayoutElement>();
		size.minWidth = 0f;
		size.preferredWidth = 1f;
		size.flexibleWidth = 1f;
		view.Background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(view.Background, UiTheme.BgBlock, UiTheme.ButtonRadius);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(6, 6, 6, 6);
		layout.spacing = 2f;
		layout.childAlignment = TextAnchor.UpperCenter;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		view.Label = LuiWidgets.Label(rect, string.Empty, 10, UiTheme.TextMuted, TextAnchor.MiddleCenter, LuiStyle.Bold);
		view.Value = LuiWidgets.Label(rect, string.Empty, 15, LuiStyle.White, TextAnchor.MiddleCenter, LuiStyle.Bold);
		RectTransform status = UiKit.CreateRect("Status", rect);
		HorizontalLayoutGroup statusLayout = status.gameObject.AddComponent<HorizontalLayoutGroup>();
		statusLayout.padding = new RectOffset(9, 9, 2, 2);
		statusLayout.spacing = 3f;
		statusLayout.childAlignment = TextAnchor.MiddleCenter;
		statusLayout.childControlWidth = true;
		statusLayout.childControlHeight = true;
		statusLayout.childForceExpandWidth = false;
		statusLayout.childForceExpandHeight = false;
		LayoutElement statusSize = status.gameObject.AddComponent<LayoutElement>();
		statusSize.minHeight = 22f;
		statusSize.preferredHeight = 22f;
		view.Status = status.gameObject.AddComponent<Image>();
		UiTheme.Round(view.Status, UiTheme.BgSlot, UiTheme.PillRadius);
		view.StatusTexts = new Text[MaxMaterials];
		view.StatusIcons = new Image[MaxMaterials];
		for (int i = 0; i < MaxMaterials; i++)
		{
			view.StatusTexts[i] = LuiWidgets.Label(status, string.Empty, LuiStyle.SmallFontSize + 1, LuiStyle.White, TextAnchor.MiddleCenter, LuiStyle.Bold);
			view.StatusIcons[i] = Picture(status, 14f);
		}
		Clickable(rect.gameObject, () => view.Click?.Invoke(), () => view.Tooltip);
		return view;
	}

	private void BuildQueue()
	{
		Transform row = Row(Body, 32f, 6f);
		Text caption = Caption(row, "Queue");
		UiKit.Fixed(caption.gameObject, 44f, 32f);
		for (int i = 0; i < UpgradeOrders.MaxQueue; i++)
		{
			int index = i;
			RectTransform slot = CsRowParts.Slot(row, 30f);
			Image icon = CsRowParts.Picture(slot, 26f);
			slot.GetComponent<Image>().raycastTarget = true;
			Clickable(slot.gameObject, () => Cancel(index), () => QueueTooltip(index));
			_queueSlots.Add(icon);
		}
		_queueEmpty = LuiWidgets.Label(row, "Nothing under way", LuiStyle.SmallFontSize, UiTheme.TextDim, TextAnchor.MiddleLeft, LuiStyle.Regular);
		UiKit.Flexible(_queueEmpty.gameObject);
		_bar = UiKit.CreateRect("Bar", row);
		LayoutElement barSize = _bar.gameObject.AddComponent<LayoutElement>();
		barSize.flexibleWidth = 1f;
		barSize.minHeight = 6f;
		barSize.preferredHeight = 6f;
		UiTheme.Round(_bar.gameObject.AddComponent<Image>(), UiTheme.BgSlot, UiTheme.BarRadius);
		_barFill = UiKit.CreateRect("Fill", _bar);
		_barFill.anchorMin = Vector2.zero;
		_barFill.anchorMax = new Vector2(0f, 1f);
		_barFill.offsetMin = _barFill.offsetMax = Vector2.zero;
		UiTheme.Round(_barFill.gameObject.AddComponent<Image>(), LuiStyle.CastleYellow, UiTheme.BarRadius);
		_barText = LuiWidgets.Label(row, string.Empty, LuiStyle.SmallFontSize, UiTheme.TextMuted, TextAnchor.MiddleRight, LuiStyle.Regular);
		UiKit.Fixed(_barText.gameObject, 34f, 32f);
	}

	// ---- read

	private UpgradeStation Research
	{
		get { return Station as UpgradeStation; }
	}

	private void Choose(int group)
	{
		_group = Mathf.Clamp(group, 0, _groups.Count - 1);
		_line = (_groups.Count > 0 && _groups[_group].Lines.Count > 0) ? _groups[_group].Lines[0] : null;
		Soon();
	}

	protected override void Read(CraftingStation station)
	{
		UpgradeStation research = (UpgradeStation)station;
		Faction faction = UpgradeOrders.FactionOf(research);
		List<RecipeInfo> queue = research.QueuedRecipes;
		// The research station's crystals in the header (the smithy's and armoury's materials are on their prices).
		if (_hall == UpgradeHall.Research)
		{
			Ressource crystal = UpgradeOrders.ResearchCrystal;
			int reserved = ResearchReservations.Reserved(faction, crystal);
			SetChip(_crystal, UpgradeOrders.Free(faction, crystal) + ((reserved > 0) ? ("  <color=" + UiTheme.Hex(UiTheme.TextMuted) + ">(" + reserved + " reserved)</color>") : string.Empty));
			ChipText.supportRichText = true;
		}
		for (int i = 0; i < _tabs.Count; i++)
		{
			bool on = i == _group;
			_tabs[i].color = on ? LuiStyle.CastleYellow : LuiStyle.BgNone;
			_tabTexts[i].color = on ? UiTheme.TextOnYellow : UiTheme.TextMuted;
		}
		List<UpgradeLine> lines = (_groups.Count > 0) ? _groups[_group].Lines : new List<UpgradeLine>();
		for (int i = 0; i < _cards.Count; i++)
		{
			ReadCard(_cards[i], (i < lines.Count) ? lines[i] : null, faction, queue);
		}
		ReadLine(research, faction, queue);
		ReadQueue(research, queue);
	}

	private void ReadCard(Card card, UpgradeLine line, Faction faction, List<RecipeInfo> queue)
	{
		card.Line = line;
		// Kept in the grid (invisible) so the cards keep their places and the window its size.
		card.Background.enabled = line != null;
		foreach (Transform child in card.Root.transform)
		{
			child.gameObject.SetActive(line != null);
		}
		if (line == null)
		{
			return;
		}
		int tier = TeamUpgrades.Tier(faction, line.Index);
		int queued = UpgradeOrders.QueuedTier(queue, line);
		Sprite icon = UpgradeOrders.IconFor(line, Mathf.Clamp(Mathf.Max(tier + 1, queued), 1, line.Tiers));
		card.Icon.sprite = icon;
		card.Icon.enabled = icon != null;
		CsRowParts.SetText(card.Name, line.Name);
		bool selected = line == _line;
		card.Background.color = selected ? UiTheme.BgSelectedRow : UiTheme.BgBlock;
		card.Name.color = selected ? LuiStyle.CastleYellow : LuiStyle.White;
		for (int i = 0; i < card.Pips.Length; i++)
		{
			card.Pips[i].gameObject.SetActive(i < line.Tiers);
			card.Pips[i].color = (i < tier) ? LuiStyle.CastleYellow : ((i == queued - 1) ? UiTheme.TextMuted : PipOff);
		}
	}

	private void ReadLine(UpgradeStation station, Faction faction, List<RecipeInfo> queue)
	{
		UpgradeLine line = _line;
		CsRowParts.SetText(_lineName, (line != null) ? line.Name : string.Empty);
		CsRowParts.SetText(_lineText, (line != null) ? line.Text : string.Empty);
		int tier = (line != null) ? TeamUpgrades.Tier(faction, line.Index) : 0;
		int queued = (line != null) ? UpgradeOrders.QueuedTier(queue, line) : 0;
		bool lockedElsewhere = false;
		ResearchReservations.Lock busy = null;
		if (line != null && ResearchReservations.TryGetLock(faction, line.Index, out busy) && busy.Station != station)
		{
			lockedElsewhere = true;
		}
		bool full = queue != null && queue.Count >= UpgradeOrders.MaxQueue;
		bool current = queue != null && queue.Count > 0 && line != null && UpgradeLines.TryGetResearch(queue[0], out UpgradeLine first, out int _) && first == line;
		for (int i = 0; i < _tiers.Length; i++)
		{
			Tier view = _tiers[i];
			int number = i + 1;
			bool shown = line != null && number <= line.Tiers;
			view.Root.SetActive(shown);
			if (!shown)
			{
				continue;
			}
			view.Click = null;
			view.Tooltip = line.Effect[number];
			CsRowParts.SetText(view.Label, UpgradeLines.TierName(line, number).ToUpperInvariant());
			CsRowParts.SetText(view.Value, Short(line.Effect[number]));
			Ingredient[] price = line.Cost[number];
			if (number <= tier)
			{
				SetTier(view, UiTheme.BgBlock, new Color(UiTheme.Good.r, UiTheme.Good.g, UiTheme.Good.b, 0.18f), "Done", UiTheme.Good, null);
			}
			else if (number == queued)
			{
				SetTier(view, UiTheme.BgSelectedRow, UiTheme.BgSlot, current ? "Under way" : "Queued", UiTheme.TextMuted, null);
				view.Tooltip += "\n" + (current ? "Being researched now." : "Waiting in the queue.") + " Click it in the queue below to cancel it.";
			}
			else if (number == tier + 1 && queued == 0)
			{
				if (lockedElsewhere)
				{
					SetTier(view, UiTheme.BgSelectedRow, UiTheme.BgSlot, "Busy", UiTheme.TextMuted, null);
					view.Tooltip += "\nTier " + busy.Tier + " is " + (busy.Current ? "under way" : "queued") + " at " + ResearchReservations.Describe(busy.Station, station) + ".";
				}
				else if (!UpgradeOrders.Affordable(faction, line.Cost[number]))
				{
					SetTier(view, UiTheme.BgSelectedRow, UiTheme.BgSlot, null, Bad, price);
					view.Tooltip += "\nCosts " + UpgradeLines.CostText(line.Cost[number]) + ": the stockpiles do not hold that much that is not reserved.";
				}
				else if (full)
				{
					SetTier(view, UiTheme.BgSelectedRow, UiTheme.BgSlot, null, UiTheme.TextMuted, price);
					view.Tooltip += "\nThe queue is full.";
				}
				else
				{
					SetTier(view, UiTheme.BgSelectedRow, LuiStyle.CastleYellow, null, UiTheme.TextOnYellow, price);
					view.Tooltip += "\nClick to research it for " + UpgradeLines.CostText(line.Cost[number]) + ".";
					view.Click = () =>
					{
						if (UpgradeOrders.Research(station, line))
						{
							Soon();
						}
					};
				}
			}
			else
			{
				SetTier(view, UiTheme.BgBlock, UiTheme.BgSlot, null, UiTheme.TextDim, price);
				view.Tooltip += "\nCosts " + UpgradeLines.CostText(line.Cost[number]) + ", after tier " + (number - 1) + ".";
			}
		}
	}

	// The tier's block and its status: a word (Done, Queued...) or the price, a count and an icon per material.
	private static void SetTier(Tier view, Color block, Color status, string text, Color textColor, Ingredient[] price)
	{
		view.Background.color = block;
		view.Status.color = status;
		for (int i = 0; i < MaxMaterials; i++)
		{
			Ingredient ingredient = (price != null && i < price.Length) ? price[i] : null;
			string shown = (price == null) ? ((i == 0) ? text : string.Empty) : ((ingredient != null) ? ingredient.Count.ToString() : string.Empty);
			view.StatusTexts[i].gameObject.SetActive(!string.IsNullOrEmpty(shown));
			CsRowParts.SetText(view.StatusTexts[i], shown);
			view.StatusTexts[i].color = textColor;
			Sprite icon = (ingredient != null && ingredient.Icon != null) ? ingredient.Icon.Get64() : null;
			view.StatusIcons[i].sprite = icon;
			view.StatusIcons[i].gameObject.SetActive(icon != null);
		}
	}

	// "+10%" from "+10% work speed"; a tier that only unlocks something says so.
	private static string Short(string effect)
	{
		if (string.IsNullOrEmpty(effect))
		{
			return string.Empty;
		}
		string first = effect.Split(' ')[0];
		return (first.StartsWith("+") || first.StartsWith("-")) ? first : "Unlock";
	}

	private void ReadQueue(UpgradeStation station, List<RecipeInfo> queue)
	{
		int count = (queue != null) ? queue.Count : 0;
		for (int i = 0; i < _queueSlots.Count; i++)
		{
			bool shown = i < count;
			_queueSlots[i].transform.parent.gameObject.SetActive(shown);
			if (!shown)
			{
				continue;
			}
			Sprite icon = UpgradeLines.TryGetResearch(queue[i], out UpgradeLine line, out int tier) ? UpgradeOrders.IconFor(line, tier) : null;
			_queueSlots[i].sprite = icon;
			_queueSlots[i].enabled = icon != null;
			_queueSlots[i].transform.parent.GetComponent<Image>().color = (i == 0) ? UiTheme.BgSelectedRow : UiTheme.BgSlot;
		}
		_queueEmpty.gameObject.SetActive(count == 0);
		float progress = (count > 0) ? UpgradeOrders.Progress(station) : -1f;
		_bar.gameObject.SetActive(count > 0);
		_barText.gameObject.SetActive(count > 0);
		_barFill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
		CsRowParts.SetText(_barText, (progress >= 0f) ? (Mathf.RoundToInt(progress * 100f) + "%") : string.Empty);
	}

	private string QueueTooltip(int index)
	{
		List<RecipeInfo> queue = (Research != null) ? Research.QueuedRecipes : null;
		if (queue == null || index >= queue.Count || !UpgradeLines.TryGetResearch(queue[index], out UpgradeLine line, out int tier))
		{
			return null;
		}
		return line.Name + " " + UpgradeLines.TierName(line, tier) + ((index == 0) ? " (under way)" : " (queued)") + ": click to cancel it; its price is released.";
	}

	private void Cancel(int index)
	{
		List<RecipeInfo> queue = (Research != null) ? Research.QueuedRecipes : null;
		if (queue != null && index < queue.Count)
		{
			UpgradeOrders.RemoveAt(Research, index);
			Soon();
		}
	}
}
