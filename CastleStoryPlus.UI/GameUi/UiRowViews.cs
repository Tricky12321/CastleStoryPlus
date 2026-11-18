using System.Collections.Generic;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// A UiButton drawn as a wide row (a section with UiLayout.Rows: the workshop's recipes): its icon in a slot with the
// hotkey in the corner, its label over its detail, and the game's yellow plus on the right. A click anywhere on the row
// is the button's click (Shift and Ctrl with it); a highlighted row gets a yellow edge on the left and a faint
// yellow fill.
internal class CsListRowView : CsItemView, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	private const float Height = 42f;

	private const float SlotSize = 36f;

	private UiButton _button;

	private Image _background;

	private Image _edge;

	private Image _icon;

	private Text _hotkey;

	private Text _label;

	private Text _detail;

	private bool _hovered;

	private bool _highlighted;

	private UiCounter _counter;

	protected override UiItem Item
	{
		get { return _button; }
	}

	public static CsListRowView Create(Transform parent, UiButton button)
	{
		RectTransform rect = UiKit.CreateRect("Row " + button.Id, parent);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(6, 6, 3, 3);
		layout.spacing = 8f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		// At least Height tall, taller when the label or the cost wraps (the layout's own preferred height).
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = Height;
		element.flexibleWidth = 1f;
		CsListRowView view = rect.gameObject.AddComponent<CsListRowView>();
		view._button = button;
		view._background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(view._background, LuiStyle.BgNone, UiTheme.ButtonRadius);
		view._edge = CsRowParts.Edge(rect);

		RectTransform slot = CsRowParts.Slot(rect, SlotSize);
		view._icon = CsRowParts.Picture(slot, 28f);
		view._hotkey = LuiWidgets.Label(slot, string.Empty, 9, UiTheme.TextDim, TextAnchor.UpperLeft, LuiStyle.Bold);
		UiKit.SetRect(view._hotkey.rectTransform, Vector2.zero, Vector2.one, new Vector2(3f, 0f), new Vector2(0f, -1f));

		RectTransform texts = LuiWidgets.Vertical(rect, 0f, 0);
		Object.DestroyImmediate(texts.GetComponent<ContentSizeFitter>());
		// The texts take only the room the slot and the controls on the right leave (no width of their own to claim),
		// and wrap inside it instead of running under the loop icon and the counter.
		texts.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
		UiKit.Flexible(texts.gameObject);
		texts.GetComponent<LayoutElement>().preferredWidth = 0f;
		view._label = LuiWidgets.Label(texts, string.Empty, LuiStyle.FontSize, LuiStyle.White, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._label.horizontalOverflow = HorizontalWrapMode.Wrap;
		view._detail = LuiWidgets.Label(texts, string.Empty, LuiStyle.SmallFontSize, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Regular);
		view._detail.supportRichText = true;
		view._detail.horizontalOverflow = HorizontalWrapMode.Wrap;

		// A Step: the row's own "-  value  +" (a recipe's loop limit), which takes its clicks itself.
		if (button.Step != null && button.StepValue != null)
		{
			// The game's loop icon says what the number is: how many to keep while the queue loops.
			RectTransform loop = UiKit.CreateRect("Loop", rect);
			UiKit.Fixed(loop.gameObject, 16f, 16f);
			Image loopIcon = loop.gameObject.AddComponent<Image>();
			loopIcon.sprite = Brix.UI.Icons.IconKeys._UI_Wheel.Get64();
			loopIcon.preserveAspect = true;
			loopIcon.color = UiTheme.TextMuted;
			loopIcon.raycastTarget = false;
			view._counter = UiCounter.Create(rect, 78f, (int delta) =>
			{
				if (button.Step(delta))
				{
					GameUiState.Announce();
					CsItemViews.MarkAll();
				}
			});
			view._counter.SetFontSize(12);
			CsTaskListPointer hover = view._counter.gameObject.AddComponent<CsTaskListPointer>();
			hover.Enter = (bool on) =>
			{
				string tip = (on && button.StepTooltip != null) ? button.StepTooltip() : null;
				if (!string.IsNullOrEmpty(tip))
				{
					LuiTooltip.Show(tip, (RectTransform)view._counter.transform);
				}
				else
				{
					LuiTooltip.Hide((RectTransform)view._counter.transform);
				}
			};
		}

		// Queue one: a dark plus in a yellow square (the row's main action, unlike the counter's signs).
		RectTransform add = UiKit.CreateRect("Add", rect);
		UiKit.Fixed(add.gameObject, 26f, 26f);
		Image addBackground = add.gameObject.AddComponent<Image>();
		UiTheme.Round(addBackground, LuiStyle.CastleYellow, UiTheme.SlotRadius);
		addBackground.raycastTarget = false;
		Text plus = LuiWidgets.Label(add, "+", 22, UiTheme.TextOnYellow, TextAnchor.MiddleCenter, LuiStyle.Bold);
		UiKit.SetRect(plus.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 1f), new Vector2(0f, 1f));
		return view;
	}

	protected override void Read()
	{
		UiButton b = _button;
		Sprite sprite = (b.Icon != null) ? b.Icon() : null;
		if (_icon.sprite != sprite)
		{
			_icon.sprite = sprite;
		}
		_icon.enabled = sprite != null;
		_icon.color = (b.IconColor != null) ? b.IconColor() : LuiStyle.White;
		CsRowParts.SetText(_hotkey, (b.Hotkey != null) ? b.Hotkey() : string.Empty);
		CsRowParts.SetText(_label, b.LabelText());
		string detail = (b.Detail != null) ? b.Detail() : string.Empty;
		CsRowParts.SetText(_detail, detail);
		_detail.gameObject.SetActive(!string.IsNullOrEmpty(detail));
		if (_counter != null)
		{
			_counter.Set(b.StepValue());
		}
		_highlighted = b.Highlight != null && b.Highlight();
		Paint();
	}

	private void Paint()
	{
		_edge.enabled = _highlighted;
		Color background = _highlighted ? UiTheme.BgSelectedRow : LuiStyle.BgNone;
		_background.color = _hovered ? LuiStyle.Hovered(background) : background;
	}

	public void OnPointerEnter(PointerEventData eventData)
	{
		_hovered = true;
		Paint();
		string tooltip = (_button.Tooltip != null) ? _button.Tooltip() : null;
		if (!string.IsNullOrEmpty(tooltip))
		{
			LuiTooltip.Show(tooltip, (RectTransform)transform);
		}
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		_hovered = false;
		Paint();
		LuiTooltip.Hide((RectTransform)transform);
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || GameUiState.Paused)
		{
			return;
		}
		_button.Click(LuiInput.Shift, LuiInput.Ctrl);
		CsItemViews.MarkAll();
	}

	private void OnDisable()
	{
		_hovered = false;
		if (_background != null)
		{
			Paint();
		}
		LuiTooltip.Hide((RectTransform)transform);
	}
}

// A UiStatus: a dark block with the picture in a framed slot, the caption, name and detail beside it and a thin bar
// under them.
internal class CsStatusView : CsItemView, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	private UiStatus _status;

	private Image _icon;

	private Text _caption;

	private Text _title;

	private Text _detail;

	private GameObject _bar;

	private RectTransform _fill;

	private CanvasGroup _group;

	protected override UiItem Item
	{
		get { return _status; }
	}

	public static CsStatusView Create(Transform parent, UiStatus status)
	{
		RectTransform rect = UiKit.CreateRect("Status " + status.Id, parent);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(8, 8, 8, 8);
		layout.spacing = 10f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
		Image background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(background, UiTheme.BgBlock, UiTheme.ButtonRadius);
		CsStatusView view = rect.gameObject.AddComponent<CsStatusView>();
		view._status = status;
		view._group = rect.gameObject.AddComponent<CanvasGroup>();

		RectTransform slot = CsRowParts.Slot(rect, 48f);
		view._icon = CsRowParts.Picture(slot, 40f);
		LuiWidgets.Frame(slot, LuiStyle.Highlight, 1f).SetActive(true);

		RectTransform texts = LuiWidgets.Vertical(rect, 1f, 0);
		Object.DestroyImmediate(texts.GetComponent<ContentSizeFitter>());
		texts.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
		UiKit.Flexible(texts.gameObject);
		if (status.Width > 0f)
		{
			// A set width (a sidebar's list): long texts wrap inside the room the slot leaves instead of pushing the block wider.
			rect.GetComponent<LayoutElement>().preferredWidth = status.Width;
			texts.GetComponent<LayoutElement>().preferredWidth = 0f;
		}
		view._caption = LuiWidgets.Label(texts, string.Empty, 10, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._title = LuiWidgets.Label(texts, string.Empty, 16, LuiStyle.White, TextAnchor.MiddleLeft, LuiStyle.Title);
		view._detail = LuiWidgets.Label(texts, string.Empty, LuiStyle.SmallFontSize, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Regular);
		view._detail.supportRichText = true;
		view._detail.horizontalOverflow = HorizontalWrapMode.Wrap;

		RectTransform bar = UiKit.CreateRect("Bar", texts);
		LayoutElement barElement = bar.gameObject.AddComponent<LayoutElement>();
		barElement.minHeight = 5f;
		barElement.preferredHeight = 5f;
		barElement.flexibleWidth = 1f;
		Image track = bar.gameObject.AddComponent<Image>();
		UiTheme.Round(track, new Color(1f, 1f, 1f, 0.08f), UiTheme.BarRadius);
		track.raycastTarget = false;
		RectTransform fill = UiKit.CreateRect("Fill", bar);
		UiKit.SetRect(fill, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		Image fillImage = fill.gameObject.AddComponent<Image>();
		UiTheme.Round(fillImage, LuiStyle.CastleYellow, UiTheme.BarRadius);
		fillImage.raycastTarget = false;
		view._bar = bar.gameObject;
		view._fill = fill;
		return view;
	}

	protected override void Read()
	{
		UiStatus s = _status;
		Sprite sprite = (s.Icon != null) ? s.Icon() : null;
		if (_icon.sprite != sprite)
		{
			_icon.sprite = sprite;
		}
		_icon.enabled = sprite != null;
		CsRowParts.SetText(_caption, ((s.Caption != null) ? s.Caption() : string.Empty).ToUpperInvariant());
		CsRowParts.SetText(_title, (s.Title != null) ? s.Title() : string.Empty);
		string detail = (s.Detail != null) ? s.Detail() : string.Empty;
		CsRowParts.SetText(_detail, detail);
		_detail.gameObject.SetActive(!string.IsNullOrEmpty(detail));
		float opacity = (s.Dimmed != null && s.Dimmed()) ? 0.45f : 1f;
		if (_group.alpha != opacity)
		{
			_group.alpha = opacity;
		}
		float progress = (s.Progress != null) ? s.Progress() : -1f;
		bool bar = progress >= 0f;
		if (_bar.activeSelf != bar)
		{
			_bar.SetActive(bar);
		}
		if (bar)
		{
			_fill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
		}
	}

	public void OnPointerEnter(PointerEventData eventData)
	{
		string tooltip = (_status.Tooltip != null) ? _status.Tooltip() : null;
		if (!string.IsNullOrEmpty(tooltip))
		{
			LuiTooltip.Show(tooltip, (RectTransform)transform);
		}
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		LuiTooltip.Hide((RectTransform)transform);
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || _status.OnClick == null || GameUiState.Paused)
		{
			return;
		}
		_status.OnClick();
		GameUiState.Announce();
		CsItemViews.MarkAll();
	}

	private void OnDisable()
	{
		LuiTooltip.Hide((RectTransform)transform);
	}
}

// A UiChoice: its label on the left, the options side by side on the right in one dark strip, the chosen one yellow
// with dark text.
internal class CsChoiceView : CsItemView
{
	private UiChoice _choice;

	private Text _label;

	private readonly List<Image> _backgrounds = new List<Image>();

	private readonly List<Text> _texts = new List<Text>();

	protected override UiItem Item
	{
		get { return _choice; }
	}

	public static CsChoiceView Create(Transform parent, UiChoice choice)
	{
		RectTransform row = UiKit.CreateRect("Choice " + choice.Id, parent);
		HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 6f;
		layout.padding = new RectOffset(2, 2, 2, 2);
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
		element.minHeight = 28f;
		element.preferredHeight = 28f;
		element.flexibleWidth = 1f;
		CsChoiceView view = row.gameObject.AddComponent<CsChoiceView>();
		view._choice = choice;
		view._label = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.White, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(view._label.gameObject);

		RectTransform strip = LuiWidgets.Horizontal(row, 2f, 2);
		Image stripBackground = strip.gameObject.AddComponent<Image>();
		UiTheme.Round(stripBackground, UiTheme.BgSlot, UiTheme.PillRadius);
		for (int i = 0; i < choice.Options.Length; i++)
		{
			int option = i;
			RectTransform cell = UiKit.CreateRect("Option " + choice.Options[i], strip);
			Image background = cell.gameObject.AddComponent<Image>();
			UiTheme.Round(background, LuiStyle.BgNone, 9);
			Text text = LuiWidgets.Label(cell, choice.Options[i], LuiStyle.SmallFontSize, UiTheme.TextMuted, TextAnchor.MiddleCenter, LuiStyle.Bold);
			UiKit.SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			UiKit.Fixed(cell.gameObject, Mathf.Max(34f, text.preferredWidth + 12f), 20f);
			CsTaskListPointer pointer = cell.gameObject.AddComponent<CsTaskListPointer>();
			pointer.Enter = (bool on) => view.Paint(on ? option : -1);
			pointer.Click = (PointerEventData.InputButton which, bool shift, bool ctrl) =>
			{
				if (which == PointerEventData.InputButton.Left && !GameUiState.Paused && choice.Select != null)
				{
					choice.Select(option);
					GameUiState.Announce();
					CsItemViews.MarkAll();
				}
			};
			view._backgrounds.Add(background);
			view._texts.Add(text);
		}
		return view;
	}

	protected override void Read()
	{
		CsRowParts.SetText(_label, (_choice.Label != null) ? _choice.Label() : string.Empty);
		Paint(-1);
	}

	private void Paint(int hovered)
	{
		int selected = (_choice.Selected != null) ? _choice.Selected() : -1;
		for (int i = 0; i < _backgrounds.Count; i++)
		{
			bool on = i == selected;
			Color background = on ? LuiStyle.CastleYellow : LuiStyle.BgNone;
			_backgrounds[i].color = (i == hovered && !on) ? LuiStyle.Hovered(background) : background;
			_texts[i].color = on ? UiTheme.TextOnYellow : UiTheme.TextMuted;
		}
	}
}

// The pieces the rows share.
internal static class CsRowParts
{
	// A square with a slot's faint fill.
	internal static RectTransform Slot(Transform parent, float size)
	{
		RectTransform slot = UiKit.CreateRect("Slot", parent);
		UiKit.Fixed(slot.gameObject, size, size);
		Image background = slot.gameObject.AddComponent<Image>();
		UiTheme.Round(background, UiTheme.BgSlot, UiTheme.SlotRadius);
		background.raycastTarget = false;
		return slot;
	}

	// A picture in the middle of a slot.
	internal static Image Picture(RectTransform slot, float size)
	{
		RectTransform icon = UiKit.CreateRect("Icon", slot);
		icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
		icon.sizeDelta = new Vector2(size, size);
		Image image = icon.gameObject.AddComponent<Image>();
		image.preserveAspect = true;
		image.raycastTarget = false;
		image.enabled = false;
		return image;
	}

	// The yellow edge on the left of a highlighted row.
	internal static Image Edge(RectTransform row)
	{
		RectTransform edge = UiKit.CreateRect("Edge", row);
		edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		edge.anchorMin = new Vector2(0f, 0f);
		edge.anchorMax = new Vector2(0f, 1f);
		edge.pivot = new Vector2(0f, 0.5f);
		edge.anchoredPosition = Vector2.zero;
		edge.sizeDelta = new Vector2(3f, 0f);
		Image image = edge.gameObject.AddComponent<Image>();
		UiTheme.Round(image, LuiStyle.CastleYellow, 1);
		image.raycastTarget = false;
		image.enabled = false;
		return image;
	}

	internal static void SetText(Text text, string value)
	{
		value = value ?? string.Empty;
		if (text.text != value)
		{
			text.text = value;
		}
	}
}
