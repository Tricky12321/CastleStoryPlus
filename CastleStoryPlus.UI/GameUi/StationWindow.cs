using System;
using Brix.Game.Components;
using Brix.Lifecycle.Pooling;
using Brix.UI.GameSelector;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// A building's own window in the popup's place (over the task list, above the selected row), open while a station
// of its kind is selected: the research station and the market. Its header has the building's icon, name, a line
// under it, a chip on the right and a close button (closing deselects the building). The task popup hides while one
// is open. Subclasses build their body once and read the station four times a second.
internal abstract class CsStationWindow : MonoBehaviour
{
	private const float ReadSeconds = 0.25f;

	// How many station windows are open (the task popup hides meanwhile).
	private static int _open;

	internal static bool Covering
	{
		get { return _open > 0; }
	}

	protected RectTransform Rect;

	protected RectTransform Body;

	protected Text Subtitle;

	protected Text ChipText;

	protected Image ChipIcon;

	private GameObject _chip;

	private CraftingStation _station;

	private float _readAt;

	private bool _shown;

	private CsWindowDrag _drag;

	// The workshop rows at the bottom (WorkshopPriority, WorkshopFetchRange), as under a workshop's recipes.
	private Button[] _priority;

	private Text _fetchRange;

	protected abstract float Width { get; }

	// The selected station when it is this window's kind, else null.
	protected abstract CraftingStation Pick(CraftingStation selected);

	protected abstract void Build();

	// The station changed (another of the kind was selected): forget what was built for the last one.
	protected virtual void OnStation(CraftingStation station)
	{
	}

	protected abstract void Read(CraftingStation station);

	protected CraftingStation Station
	{
		get { return _station; }
	}

	protected void Setup(Transform parent, string name, Func<Sprite> icon, string title)
	{
		Rect = (RectTransform)transform;
		Rect.SetParent(parent, false);
		Rect.anchorMin = Rect.anchorMax = new Vector2(0f, 0f);
		Rect.pivot = new Vector2(0f, 0f);
		Rect.anchoredPosition = new Vector2(CsPanelView.BarWidth, GameTaskList.Height + 4f);
		Rect.sizeDelta = new Vector2(Width, 0f);
		gameObject.name = name;
		_drag = CsWindowDrag.Add(Rect, name);
		Image background = gameObject.AddComponent<Image>();
		UiTheme.Round(background, UiTheme.BgPopup, UiTheme.PanelRadius);
		VerticalLayoutGroup layout = gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(12, 12, 10, 12);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		Body = Column(Rect, 8f);

		Transform header = Row(Body, 34f, 8f);
		Image picture = Picture(header, 30f);
		Sprite sprite = (icon != null) ? icon() : null;
		picture.sprite = sprite;
		picture.enabled = sprite != null;
		RectTransform names = Column(header, 0f);
		UiKit.Flexible(names.gameObject);
		LuiWidgets.Label(names, title, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		Subtitle = LuiWidgets.Label(names, string.Empty, LuiStyle.SmallFontSize, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Regular);
		Subtitle.gameObject.SetActive(false);

		RectTransform chip = UiKit.CreateRect("Chip", header);
		HorizontalLayoutGroup chipLayout = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
		chipLayout.padding = new RectOffset(8, 10, 2, 2);
		chipLayout.spacing = 4f;
		chipLayout.childAlignment = TextAnchor.MiddleCenter;
		chipLayout.childControlWidth = true;
		chipLayout.childControlHeight = true;
		chipLayout.childForceExpandWidth = false;
		chipLayout.childForceExpandHeight = false;
		LayoutElement chipSize = chip.gameObject.AddComponent<LayoutElement>();
		chipSize.minHeight = 24f;
		chipSize.preferredHeight = 24f;
		UiTheme.Round(chip.gameObject.AddComponent<Image>(), UiTheme.BgSlot, UiTheme.PillRadius);
		ChipIcon = Picture(chip, 16f);
		ChipText = LuiWidgets.Label(chip, string.Empty, LuiStyle.SmallFontSize + 1, LuiStyle.White, TextAnchor.MiddleLeft, LuiStyle.Bold);
		_chip = chip.gameObject;
		_chip.SetActive(false);

		Button close = UiKit.CreateButton(header, "X", 26f, Close);
		UiKit.Fixed(close.gameObject, 26f, 26f);
		RoundButton(close, UiTheme.SlotRadius);
		Build();
		BuildWorkshopRows();
		Body.gameObject.SetActive(false);
		background.enabled = false;
	}

	private void Update()
	{
		CraftingStation selected = (UIGameObserver.crafting != null) ? UIGameObserver.crafting.CurrentSelected : null;
		CraftingStation station = (!selected.IsNullOrReleased()) ? Pick(selected) : null;
		// By reference: a destroyed station equals null for Unity, which would keep the window open.
		if (!ReferenceEquals(station, _station))
		{
			_station = station;
			OnStation(station);
			_readAt = 0f;
		}
		Show(station != null);
		if (station != null && Time.unscaledTime - _readAt > ReadSeconds)
		{
			_readAt = Time.unscaledTime;
			Read(station);
			ReadWorkshopRows(station);
		}
		if (station != null)
		{
			Place();
		}
	}

	// Priority (four steps side by side) and fetch range ("-  40  +", Shift 25 at a time), each when its feature is on.
	private void BuildWorkshopRows()
	{
		bool priority = Plugin.IsEnabled(Core.Features.WorkshopPriority);
		bool range = Plugin.IsEnabled(Core.Features.WorkshopFetchRange);
		if (!priority && !range)
		{
			return;
		}
		Line(Body);
		if (priority)
		{
			Transform row = Row(Body, 26f, 4f);
			Text caption = Caption(row, "Priority");
			UiKit.Fixed(caption.gameObject, 92f, 26f);
			_priority = new Button[Economy.WorkshopPriority.StepNames.Length];
			for (int i = 0; i < _priority.Length; i++)
			{
				int step = i;
				Button button = UiKit.CreateButton(row, Economy.WorkshopPriority.StepNames[i], 0f, () =>
				{
					if (Station != null && !GameUiState.Paused)
					{
						Economy.WorkshopPriority.ChangeStep(Station.state, step);
						ReadWorkshopRows(Station);
					}
				});
				RoundButton(button, UiTheme.SlotRadius);
				_priority[i] = button;
			}
		}
		if (range)
		{
			Transform row = Row(Body, 26f, 4f);
			Text caption = Caption(row, "Fetch within");
			UiKit.Fixed(caption.gameObject, 92f, 26f);
			Button less = UiKit.CreateButton(row, "-", 26f, () => StepRange(-1));
			RoundButton(less, UiTheme.SlotRadius);
			_fetchRange = LuiWidgets.Label(row, string.Empty, LuiStyle.SmallFontSize + 1, LuiStyle.White, TextAnchor.MiddleCenter, LuiStyle.Bold);
			UiKit.Fixed(_fetchRange.gameObject, 70f, 26f);
			Button more = UiKit.CreateButton(row, "+", 26f, () => StepRange(1));
			RoundButton(more, UiTheme.SlotRadius);
			Clickable(_fetchRange.gameObject, null, () => "How far from this building, in blocks, its workers go for what it needs, to stockpiles and loose items (Shift: 25 at a time). Past " + Economy.WorkshopFetchRange.Max + ": no limit.");
		}
	}

	private void StepRange(int direction)
	{
		if (Station == null || GameUiState.Paused)
		{
			return;
		}
		bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
		Economy.WorkshopFetchRange.StepFor(Station.state, direction, shift);
		ReadWorkshopRows(Station);
	}

	private void ReadWorkshopRows(CraftingStation station)
	{
		if (_priority != null)
		{
			int current = Economy.WorkshopPriority.StepOf(station.state);
			for (int i = 0; i < _priority.Length; i++)
			{
				Text label = _priority[i].GetComponentInChildren<Text>();
				if (label != null)
				{
					label.color = (i == current) ? LuiStyle.CastleYellow : UiTheme.TextMuted;
				}
			}
		}
		if (_fetchRange != null)
		{
			CsRowParts.SetText(_fetchRange, Economy.WorkshopFetchRange.Shown(Economy.WorkshopFetchRange.Get(station.state)));
		}
	}

	// Read again at once (after a click).
	protected void Soon()
	{
		_readAt = 0f;
	}

	private void Show(bool shown)
	{
		if (shown == _shown)
		{
			return;
		}
		_shown = shown;
		_open += shown ? 1 : -1;
		// The view's own Update runs while it is hidden: only its content is switched.
		Body.gameObject.SetActive(shown);
		GetComponent<Image>().enabled = shown;
		if (shown)
		{
			LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
		}
	}

	protected void SetChip(Sprite icon, string text)
	{
		_chip.SetActive(!string.IsNullOrEmpty(text));
		ChipIcon.sprite = icon;
		ChipIcon.enabled = icon != null;
		CsRowParts.SetText(ChipText, text);
	}

	protected void SetSubtitle(string text)
	{
		Subtitle.gameObject.SetActive(!string.IsNullOrEmpty(text));
		CsRowParts.SetText(Subtitle, text);
	}

	// Above the selected row of the task list, kept on screen; where the player moved it, if moved (CsWindowDrag) (as the task popup).
	private void Place()
	{
		if (_drag.TryPlace(out Vector2 moved))
		{
			Rect.anchoredPosition = moved;
			return;
		}
		GameTaskList list = GameTaskList.Current;
		RectTransform row = (list != null) ? list.SelectedRow() : null;
		RectTransform parent = (RectTransform)Rect.parent;
		float x = CsPanelView.BarWidth;
		if (row != null && row.rect.width > 0f)
		{
			Vector3[] corners = new Vector3[4];
			row.GetWorldCorners(corners);
			x = parent.InverseTransformPoint(corners[1]).x - parent.rect.xMin;
		}
		x = Mathf.Clamp(x, CsPanelView.BarWidth, Mathf.Max(CsPanelView.BarWidth, parent.rect.width - CsPanelView.BarWidth - Width));
		Rect.anchoredPosition = new Vector2(x, GameTaskList.Height + 4f);
	}

	private void Close()
	{
		if (_station != null)
		{
			UIGameSelector.Unselect(_station.gameObject);
		}
	}

	protected virtual void OnDestroy()
	{
		if (_shown)
		{
			_open--;
			_shown = false;
		}
	}

	// ---- parts

	protected static Transform Row(Transform parent, float height, float spacing)
	{
		Transform row = UiKit.CreateRow(parent, height);
		row.GetComponent<HorizontalLayoutGroup>().spacing = spacing;
		return row;
	}

	protected static RectTransform Column(Transform parent, float spacing)
	{
		RectTransform column = LuiWidgets.Vertical(parent, spacing, 0);
		DestroyImmediate(column.GetComponent<ContentSizeFitter>());
		VerticalLayoutGroup layout = column.GetComponent<VerticalLayoutGroup>();
		layout.childForceExpandWidth = true;
		return column;
	}

	// Cells of a fixed size in the window's column (without the grid's own size fitter, which fights the column).
	protected static RectTransform Grid(Transform parent, Vector2 cell, float spacing, int columns)
	{
		RectTransform grid = LuiWidgets.Grid(parent, cell, spacing, columns);
		DestroyImmediate(grid.GetComponent<ContentSizeFitter>());
		return grid;
	}

	protected static Image Picture(Transform parent, float size)
	{
		RectTransform rect = UiKit.CreateRect("Icon", parent);
		UiKit.Fixed(rect.gameObject, size, size);
		Image image = rect.gameObject.AddComponent<Image>();
		image.preserveAspect = true;
		image.raycastTarget = false;
		return image;
	}

	// A small caption in capitals.
	protected static Text Caption(Transform parent, string text)
	{
		return LuiWidgets.Label(parent, text.ToUpperInvariant(), 10, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Bold);
	}

	protected static Text Note(Transform parent, string text)
	{
		Text note = LuiWidgets.Label(parent, text, LuiStyle.SmallFontSize, UiTheme.TextDim, TextAnchor.UpperLeft, LuiStyle.Regular);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		note.supportRichText = true;
		return note;
	}

	// A thin line across.
	protected static void Line(Transform parent)
	{
		GameObject line = LuiWidgets.Divider(parent, vertical: false).gameObject;
		line.GetComponent<Image>().color = UiTheme.Line;
	}

	protected static void RoundButton(Button button, int radius)
	{
		Image image = button.GetComponent<Image>();
		image.sprite = UiTheme.Fill(radius);
		image.type = Image.Type.Sliced;
		ColorBlock colors = button.colors;
		colors.normalColor = UiTheme.BgSlot;
		colors.highlightedColor = new Color(1f, 1f, 1f, 0.16f);
		colors.pressedColor = new Color(1f, 197f / 255f, 10f / 255f, 0.35f);
		colors.disabledColor = new Color(1f, 1f, 1f, 0.03f);
		button.colors = colors;
		Text text = button.GetComponentInChildren<Text>();
		if (text != null)
		{
			text.font = LuiStyle.Bold;
			text.color = UiTheme.TextMuted;
		}
	}

	// Clicks and a tooltip on any part (a card, a chip, a slot).
	protected static void Clickable(GameObject go, Action click, Func<string> tooltip)
	{
		// The part's own graphic takes the clicks (a text cannot get an Image beside it); else a clear Image.
		Graphic hit = go.GetComponent<Graphic>();
		if (hit == null)
		{
			Image clear = go.AddComponent<Image>();
			clear.color = LuiStyle.BgNone;
			hit = clear;
		}
		hit.raycastTarget = true;
		CsTaskListPointer pointer = go.AddComponent<CsTaskListPointer>();
		RectTransform rect = (RectTransform)go.transform;
		pointer.Enter = (bool on) =>
		{
			string tip = (on && tooltip != null) ? tooltip() : null;
			if (!string.IsNullOrEmpty(tip))
			{
				LuiTooltip.Show(tip, rect);
			}
			else
			{
				LuiTooltip.Hide(rect);
			}
		};
		if (click != null)
		{
			pointer.Click = (PointerEventData.InputButton button, bool shift, bool ctrl) =>
			{
				if (button == PointerEventData.InputButton.Left && !GameUiState.Paused)
				{
					click();
				}
			};
		}
	}
}
