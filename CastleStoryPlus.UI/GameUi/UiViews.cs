using System;
using System.Collections.Generic;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// Draws one UiItem and reads it again when the game state changed (GameUiState.Changed), when it shows again and a
// few times a second while it shows (some of what it reads changes with no event: a task type being placed).
internal abstract class CsItemView : MonoBehaviour
{
	private const float PollSeconds = 0.25f;

	private bool _dirty = true;

	private float _readAt;

	protected abstract UiItem Item { get; }

	// The object shown or hidden with the item's Visible (the view itself, or its row).
	protected virtual GameObject Shown
	{
		get { return gameObject; }
	}

	protected void Start()
	{
		GameUiState.Changed += MarkDirty;
		CsItemViews.Register(this);
		Refresh();
	}

	protected void OnDestroy()
	{
		GameUiState.Changed -= MarkDirty;
		CsItemViews.Unregister(this);
	}

	internal void MarkDirty()
	{
		_dirty = true;
	}

	// Called by CsItemViews every frame, also while hidden (a hidden item must be read to show again).
	internal void Tick(float now)
	{
		if (!_dirty && now - _readAt < PollSeconds)
		{
			return;
		}
		Transform parent = transform.parent;
		if (parent != null && !parent.gameObject.activeInHierarchy)
		{
			// Inside a closed panel: read when it opens (OnParentShown marks it).
			return;
		}
		Refresh();
	}

	internal void Refresh()
	{
		_dirty = false;
		_readAt = Time.unscaledTime;
		try
		{
			bool visible = Item.IsVisible();
			if (Shown.activeSelf != visible)
			{
				Shown.SetActive(visible);
			}
			if (visible)
			{
				Read();
			}
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce("GameUi item " + Item.Id, ex.ToString());
		}
	}

	protected abstract void Read();
}

// Ticks every item view (an inactive object gets no Update, and a hidden item must still be read to show again).
internal static class CsItemViews
{
	private static readonly List<CsItemView> Views = new List<CsItemView>();

	private static readonly List<CsItemView> Ticking = new List<CsItemView>();

	internal static void Register(CsItemView view)
	{
		Views.Add(view);
	}

	internal static void Unregister(CsItemView view)
	{
		Views.Remove(view);
	}

	internal static void Tick()
	{
		float now = Time.unscaledTime;
		Ticking.Clear();
		Ticking.AddRange(Views);
		foreach (CsItemView view in Ticking)
		{
			if (view != null)
			{
				view.Tick(now);
			}
		}
	}

	internal static void MarkAll()
	{
		foreach (CsItemView view in Views)
		{
			if (view != null)
			{
				view.MarkDirty();
			}
		}
	}
}

internal class CsButtonView : CsItemView
{
	private UiButton _button;

	private LuiIconButton _view;

	// The label shown as a tooltip instead of the strip beside the button (catalogue cells).
	public bool LabelAsTooltip;

	// The smallest icon drawn (catalogue cells: their sprites have wide transparent borders).
	public float MinIconSize;

	protected override UiItem Item
	{
		get { return _button; }
	}

	public UiButton Button
	{
		get { return _button; }
	}

	public static CsButtonView Create(Transform parent, UiButton button, float size, LuiIconButton.Side labelSide)
	{
		LuiIconButton view = LuiWidgets.IconButton(parent, size);
		view.LabelSide = labelSide;
		CsButtonView item = view.gameObject.AddComponent<CsButtonView>();
		item._button = button;
		item._view = view;
		view.OnClick = (bool shift, bool ctrl) =>
		{
			button.Click(shift, ctrl);
			CsItemViews.MarkAll();
		};
		if (button.Step != null)
		{
			AddStep(view, button, 1, -12f);
			AddStep(view, button, -1, 12f);
		}
		return item;
	}

	// One of the small "+" and "-" over the top of the button, x from its middle (as StepButtons gives the game's).
	private static void AddStep(LuiIconButton view, UiButton button, int direction, float x)
	{
		LuiIconButton step = LuiWidgets.IconButton(view.transform, 22f);
		step.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		RectTransform rect = (RectTransform)step.transform;
		rect.anchorMin = new Vector2(0.5f, 1f);
		rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 0f);
		rect.anchoredPosition = new Vector2(x, 2f);
		rect.sizeDelta = new Vector2(22f, 22f);
		step.SetIcon(((direction < 0) ? Brix.UI.Icons.IconKeys._UI_Minus : Brix.UI.Icons.IconKeys._UI_Plus).Get64());
		step.SetIconSize(18f);
		step.SetIconColor(LuiStyle.CastleYellow);
		step.SetLabel(string.Empty);
		step.SetBackground(LuiStyle.BgNone);
		step.OnClick = (bool shift, bool ctrl) =>
		{
			if (GameUiState.Paused)
			{
				return;
			}
			if (button.Step(direction))
			{
				GameUiState.Announce();
				CsItemViews.MarkAll();
			}
		};
	}

	protected override void Read()
	{
		UiButton b = _button;
		_view.SetIcon((b.Icon != null) ? b.Icon() : null);
		if (MinIconSize > 0f)
		{
			_view.SetIconSize(MinIconSize);
		}
		_view.SetIconColor((b.IconColor != null) ? b.IconColor() : LuiStyle.CastleYellow);
		string label = b.LabelText();
		string tooltip = (b.Tooltip != null) ? b.Tooltip() : null;
		_view.SetLabel(LabelAsTooltip ? string.Empty : label);
		_view.SetTooltip((LabelAsTooltip && string.IsNullOrEmpty(tooltip)) ? label : tooltip);
		_view.SetCount((b.Count != null) ? b.Count() : string.Empty, (b.CountColor != null) ? b.CountColor() : LuiStyle.CastleYellow);
		_view.SetHotkey((b.Hotkey != null) ? b.Hotkey() : string.Empty);
		_view.SetHighlight(b.Highlight != null && b.Highlight());
		_view.SetSelected(b.Selected != null && b.Selected());
		_view.SetEnabled(b.Enabled == null || b.Enabled());
	}
}

internal class CsStepperView : CsItemView
{
	private UiStepper _stepper;

	private Text _label;

	private UiCounter _counter;

	protected override UiItem Item
	{
		get { return _stepper; }
	}

	// One row: the label on the left, "-  value  +" on the right.
	public static CsStepperView Create(Transform parent, UiStepper stepper, float width)
	{
		RectTransform row = UiKit.CreateRect("Stepper " + stepper.Id, parent);
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
		CsStepperView view = row.gameObject.AddComponent<CsStepperView>();
		view._stepper = stepper;
		view._label = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.White, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(view._label.gameObject);
		view._counter = UiCounter.Create(row, 120f, (int delta) =>
		{
			stepper.Step?.Invoke(delta);
			GameUiState.Announce();
		});
		return view;
	}

	protected override void Read()
	{
		string label = (_stepper.Label != null) ? _stepper.Label() : string.Empty;
		_label.text = label;
		_counter.Set((_stepper.Value != null) ? _stepper.Value() : string.Empty);
	}
}

internal class CsTextView : CsItemView
{
	private UiText _text;

	private Text _label;

	protected override UiItem Item
	{
		get { return _text; }
	}

	public static CsTextView Create(Transform parent, UiText text, TextAnchor alignment)
	{
		Text label = LuiWidgets.Label(parent, string.Empty, text.FontSize, LuiStyle.CastleYellow, alignment, text.Title ? LuiStyle.Title : LuiStyle.Bold);
		label.supportRichText = true;
		label.horizontalOverflow = HorizontalWrapMode.Wrap;
		CsTextView view = label.gameObject.AddComponent<CsTextView>();
		view._text = text;
		view._label = label;
		return view;
	}

	protected override void Read()
	{
		_label.text = (_text.Text != null) ? _text.Text() : string.Empty;
		_label.color = (_text.Color != null) ? _text.Color() : LuiStyle.CastleYellow;
	}
}

internal class CsProgressView : CsItemView
{
	private UiProgress _progress;

	private LuiProgressBar _bar;

	protected override UiItem Item
	{
		get { return _progress; }
	}

	// Room under the bar for the small "+" and "-" over the top of the buttons below it (the base ranges').
	private const float StepRoom = 24f;

	public static CsProgressView Create(Transform parent, UiProgress progress, float width)
	{
		RectTransform room = UiKit.CreateRect("ProgressRow", parent);
		UiKit.Fixed(room.gameObject, width, 20f + StepRoom);
		LuiProgressBar bar = LuiWidgets.ProgressBar(room, width, 20f);
		RectTransform rect = (RectTransform)bar.transform;
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(1f, 1f);
		rect.pivot = new Vector2(0.5f, 1f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(0f, 20f);
		CsProgressView view = room.gameObject.AddComponent<CsProgressView>();
		view._progress = progress;
		view._bar = bar;
		return view;
	}

	protected override void Read()
	{
		_bar.Set((_progress.Value != null) ? _progress.Value() : 0f, (_progress.Text != null) ? _progress.Text() : string.Empty);
	}
}

// The text field of a UiInput and its button. While the field is typed in, the game's key bindings are off (as its
// own input fields do), so typing does not move the camera or open menus.
internal class CsInputView : CsItemView
{
	private UiInput _input;

	private InputField _field;

	private CsButtonView _button;

	private bool _typing;

	protected override UiItem Item
	{
		get { return _input; }
	}

	public static CsInputView Create(Transform parent, UiInput input)
	{
		Transform row = UiKit.CreateRow(parent, 48f);
		HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(0, 0, 8, 8);
		layout.spacing = 4f;
		CsInputView view = row.gameObject.AddComponent<CsInputView>();
		view._input = input;
		view._field = UiKit.CreateInputField(row, string.Empty, 200f);
		view._field.textComponent.font = LuiStyle.Regular;
		UiKit.Flexible(view._field.gameObject);
		view._field.onEndEdit.AddListener(view.Commit);
		LuaInputFieldFocus focus = view._field.gameObject.AddComponent<LuaInputFieldFocus>();
		focus.Changed = view.SetTyping;
		if (input.Button != null)
		{
			UiButton button = input.Button;
			Action<bool, bool> click = button.OnClick;
			// A name typed but not yet confirmed counts.
			button.OnClick = (bool shift, bool ctrl) =>
			{
				if (view._typing)
				{
					view.Commit(view._field.text);
				}
				click?.Invoke(shift, ctrl);
			};
			view._button = CsButtonView.Create(row, button, 32f, LuiIconButton.Side.Right);
			view._button.LabelAsTooltip = true;
		}
		return view;
	}

	protected override void Read()
	{
		if (!_typing)
		{
			string text = (_input.Text != null) ? _input.Text() : string.Empty;
			if (_field.text != text)
			{
				_field.text = text;
			}
		}
	}

	private void Commit(string text)
	{
		_input.Set?.Invoke(text ?? string.Empty);
		MarkDirty();
	}

	private void SetTyping(bool typing)
	{
		if (_typing == typing)
		{
			return;
		}
		_typing = typing;
		if (typing)
		{
			Brix.UI.Builder.Menu.LuaMenuUtility.SetAllKeyBindingsTo(false);
			if (Brix.Input.InputModeController.Instance != null)
			{
				Brix.Input.InputModeController.Instance.ChangeInputToMenu();
			}
		}
		else
		{
			Brix.UI.Builder.Menu.LuaMenuUtility.ResetAllKeyBindings();
			if (Brix.Input.InputModeController.Instance != null)
			{
				Brix.Input.InputModeController.Instance.ChangeInputToGame();
			}
		}
	}

	private void OnDisable()
	{
		// Hidden or destroyed while typing (the panel closed): the keys must come back.
		SetTyping(false);
	}
}

// A UiRow: its picture on the left, its text beside it, framed while highlighted.
internal class CsRowView : CsItemView, UnityEngine.EventSystems.IPointerClickHandler, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
{
	private UiRow _row;

	private Image _background;

	private Image _icon;

	private Text _text;

	private GameObject _highlight;

	protected override UiItem Item
	{
		get { return _row; }
	}

	public static CsRowView Create(Transform parent, UiRow row)
	{
		RectTransform rect = UiKit.CreateRect("Row " + row.Id, parent);
		UiKit.Fixed(rect.gameObject, 240f, 40f);
		CsRowView view = rect.gameObject.AddComponent<CsRowView>();
		view._row = row;
		view._background = rect.gameObject.AddComponent<Image>();
		LuiStyle.Paint(view._background, LuiStyle.ButtonSprite, LuiStyle.BgDefault);
		view._highlight = LuiWidgets.Frame(rect, LuiStyle.Highlight, 2f);
		RectTransform icon = UiKit.CreateRect("Icon", rect);
		icon.anchorMin = icon.anchorMax = new Vector2(0f, 0.5f);
		icon.pivot = new Vector2(0f, 0.5f);
		icon.anchoredPosition = new Vector2(4f, 0f);
		icon.sizeDelta = new Vector2(60f, 36f);
		view._icon = icon.gameObject.AddComponent<Image>();
		view._icon.preserveAspect = true;
		view._icon.raycastTarget = false;
		view._icon.enabled = false;
		view._text = LuiWidgets.Label(rect, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._text.raycastTarget = false;
		UiKit.SetRect(view._text.rectTransform, Vector2.zero, Vector2.one, new Vector2(35f, 0f), new Vector2(-78f, 0f));
		return view;
	}

	protected override void Read()
	{
		Sprite sprite = (_row.Icon != null) ? _row.Icon() : null;
		if (_icon.sprite != sprite)
		{
			_icon.sprite = sprite;
		}
		_icon.enabled = sprite != null;
		_icon.color = (_row.IconColor != null) ? _row.IconColor() : LuiStyle.White;
		string text = (_row.Text != null) ? _row.Text() : string.Empty;
		if (_text.text != text)
		{
			_text.text = text;
		}
		bool highlight = _row.Highlight != null && _row.Highlight();
		if (_highlight.activeSelf != highlight)
		{
			_highlight.SetActive(highlight);
			_highlight.transform.SetAsLastSibling();
		}
	}

	public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
	{
		if (eventData.button != UnityEngine.EventSystems.PointerEventData.InputButton.Left || _row.OnClick == null)
		{
			return;
		}
		_row.OnClick();
		GameUiState.Announce();
		CsItemViews.MarkAll();
	}

	public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
	{
		_background.color = LuiStyle.FgHover;
	}

	public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
	{
		_background.color = LuiStyle.BgDefault;
	}
}

// Builds the view of any item.
internal static class CsItemFactory
{
	internal static CsItemView Create(Transform parent, UiItem item, float cell, LuiIconButton.Side labelSide, bool catalogue)
	{
		switch (item)
		{
			case UiButton button:
				{
					CsButtonView view = CsButtonView.Create(parent, button, cell, labelSide);
					view.LabelAsTooltip = catalogue;
					view.MinIconSize = catalogue ? cell : 0f;
					return view;
				}
			case UiStepper stepper:
				return CsStepperView.Create(parent, stepper, Mathf.Max(cell * 2f, 120f));
			case UiText text:
				return CsTextView.Create(parent, text, TextAnchor.MiddleLeft);
			case UiInput input:
				return CsInputView.Create(parent, input);
			case UiRow row:
				return CsRowView.Create(parent, row);
			case UiArticle article:
				return CsArticleItemView.Create(parent, article);
			case UiProgress progress:
				return CsProgressView.Create(parent, progress, Mathf.Max(cell * 3f, 180f));
			case UiStatus status:
				return CsStatusView.Create(parent, status);
			case UiChoice choice:
				return CsChoiceView.Create(parent, choice);
			case UiToggle toggle:
				return CsToggleView.Create(parent, toggle);
			case UiSlider slider:
				return CsSliderView.Create(parent, slider);
			case UiPicture picture:
				return CsPictureView.Create(parent, picture);
			default:
				return null;
		}
	}
}
