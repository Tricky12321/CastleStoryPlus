using System;
using Brix.Input;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// The building blocks of the C# menus, in the look of the game's Lua menus (LuiStyle). Only backgrounds and
// buttons are raycast targets: texts, icons and layout objects let the mouse through, so the screen around a
// menu stays the world's (the game's picking asks the EventSystem whether the mouse is over UI).
internal static class LuiWidgets
{
	public static RectTransform Vertical(Transform parent, float spacing, int padding)
	{
		RectTransform rect = UiKit.CreateRect("Vertical", parent);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		Setup(layout, spacing, padding);
		return rect;
	}

	public static RectTransform Horizontal(Transform parent, float spacing, int padding)
	{
		RectTransform rect = UiKit.CreateRect("Horizontal", parent);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		Setup(layout, spacing, padding);
		return rect;
	}

	private static void Setup(HorizontalOrVerticalLayoutGroup layout, float spacing, int padding)
	{
		layout.spacing = spacing;
		layout.padding = new RectOffset(padding, padding, padding, padding);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = layout.gameObject.AddComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
	}

	// Cells of a fixed size, row by row (the build catalogues' buttons).
	public static RectTransform Grid(Transform parent, Vector2 cell, float spacing, int columns)
	{
		RectTransform rect = UiKit.CreateRect("Grid", parent);
		GridLayoutGroup grid = rect.gameObject.AddComponent<GridLayoutGroup>();
		grid.cellSize = cell;
		grid.spacing = new Vector2(spacing, spacing);
		grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
		grid.constraintCount = Mathf.Max(1, columns);
		ContentSizeFitter fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		return rect;
	}

	// A framed panel that grows with its content; content goes into the answer. A title is drawn when given.
	public static RectTransform Window(Transform parent, string title, float width)
	{
		GameObject window = UiKit.CreateWindow(parent, width);
		LuiStyle.Paint(window.GetComponent<Image>(), LuiStyle.FrameSprite, LuiStyle.BgPanel);
		if (!string.IsNullOrEmpty(title))
		{
			Label(window.transform, title, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		}
		return window.GetComponent<RectTransform>();
	}

	public static Text Label(Transform parent, string text, int size, Color color, TextAnchor alignment, Font font)
	{
		Text label = UiKit.CreateText(parent, text, size, color, alignment);
		label.font = font ?? LuiStyle.Regular;
		return label;
	}

	// A titled group of rows (the task popup's sections).
	public static RectTransform Section(Transform parent, string title)
	{
		RectTransform section = Vertical(parent, 2f, 0);
		if (!string.IsNullOrEmpty(title))
		{
			Label(section, title, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		}
		return section;
	}

	public static Image Divider(Transform parent, bool vertical)
	{
		RectTransform rect = UiKit.CreateRect("Divider", parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = new Color(1f, 1f, 1f, 0.12f);
		image.raycastTarget = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		if (vertical)
		{
			element.minWidth = 1f;
			element.preferredWidth = 1f;
			element.flexibleHeight = 1f;
		}
		else
		{
			element.minHeight = 1f;
			element.preferredHeight = 1f;
			element.flexibleWidth = 1f;
		}
		return image;
	}

	// A thin border just inside the parent's edges, hidden until shown (selected rows and buttons). Not Unity's
	// Outline: that draws the whole graphic again offset, which shows through a see-through background as a fill.
	public static GameObject Frame(Transform parent, Color color, float thickness)
	{
		RectTransform frame = UiKit.CreateRect("Frame", parent);
		UiKit.SetRect(frame, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		frame.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		Edge(frame, color, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, thickness));
		Edge(frame, color, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, thickness));
		Edge(frame, color, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(thickness, 0f));
		Edge(frame, color, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(thickness, 0f));
		frame.gameObject.SetActive(false);
		return frame.gameObject;
	}

	private static void Edge(RectTransform frame, Color color, Vector2 min, Vector2 max, Vector2 size)
	{
		RectTransform edge = UiKit.CreateRect("Edge", frame);
		edge.anchorMin = min;
		edge.anchorMax = max;
		edge.pivot = new Vector2(min.x == max.x ? min.x : 0.5f, min.y == max.y ? min.y : 0.5f);
		edge.anchoredPosition = Vector2.zero;
		edge.sizeDelta = size;
		Image image = edge.gameObject.AddComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
	}

	public static LuiIconButton IconButton(Transform parent, float size)
	{
		RectTransform rect = UiKit.CreateRect("IconButton", parent);
		UiKit.Fixed(rect.gameObject, size, size);
		LuiIconButton button = rect.gameObject.AddComponent<LuiIconButton>();
		button.Build(size);
		return button;
	}

	public static LuiProgressBar ProgressBar(Transform parent, float width, float height)
	{
		RectTransform rect = UiKit.CreateRect("ProgressBar", parent);
		UiKit.Fixed(rect.gameObject, width, height);
		LuiProgressBar bar = rect.gameObject.AddComponent<LuiProgressBar>();
		bar.Build();
		return bar;
	}

	// A value with - and + buttons; step is called with -1 or +1 (with Shift held: -10 or +10, as the game's own).
	public static LuiCounter Counter(Transform parent, float width, Action<int> step)
	{
		RectTransform rect = UiKit.CreateRect("Counter", parent);
		UiKit.Fixed(rect.gameObject, width, 24f);
		LuiCounter counter = rect.gameObject.AddComponent<LuiCounter>();
		counter.Build(step);
		return counter;
	}

	// A vertically scrolling list; rows go into the answer, its visible height is set through size.
	public static Transform ScrollList(Transform parent, float height, out LayoutElement size)
	{
		Transform content = UiKit.CreateScrollList(parent, out size);
		size.minHeight = height;
		size.preferredHeight = height;
		return content;
	}
}

// The modifier keys the game's Lua menus read (LuaMenuComponent.IsShift / IsCtrl): the game's own bindings.
internal static class LuiInput
{
	public static bool Shift
	{
		get { return PickingUtility.Player != null && PickingUtility.Player.GetButton("cam_mouse_alt"); }
	}

	public static bool Ctrl
	{
		get { return PickingUtility.Player != null && PickingUtility.Player.GetButton("cam_mouse_sec"); }
	}
}

// A square button with an icon, as the game's bars and catalogues have: a count in a corner, a hotkey in the
// other, and its label beside it on hover (the side is set by LabelSide). Selected gives the darker background
// the game uses for an open menu; Highlight a yellow frame. Under the mouse the background and icon lighten.
internal class LuiIconButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	public enum Side
	{
		Right,
		Left,
		Below
	}

	private Image _background;

	private Image _icon;

	private GameObject _highlight;

	private Text _count;

	private Text _hotkey;

	private RectTransform _hoverLabel;

	private Text _hoverText;

	private bool _enabled = true;

	private bool _selected;

	private bool _hovered;

	private string _tooltip;

	private Color _iconColor = LuiStyle.CastleYellow;

	// The background without the hover lightening (SetSelected, SetBackground).
	private Color _backgroundColor = LuiStyle.BgDefault;

	// Left click, with Shift and Ctrl as the game's menus read them.
	public Action<bool, bool> OnClick;

	// Told when the mouse comes onto the button (true) and leaves it (false): a handle's article tooltip.
	public Action<bool> Hovered;

	public Side LabelSide = Side.Right;

	public void Build(float size)
	{
		_background = gameObject.AddComponent<Image>();
		LuiStyle.Paint(_background, LuiStyle.ButtonSprite, LuiStyle.BgDefault);
		RectTransform icon = UiKit.CreateRect("Icon", transform);
		float iconSize = Mathf.Round(size * (LuiStyle.IconSize / LuiStyle.ButtonSize));
		UiKit.SetRect(icon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(iconSize, iconSize));
		_icon = icon.gameObject.AddComponent<Image>();
		_icon.preserveAspect = true;
		_icon.raycastTarget = false;
		_icon.enabled = false;
		_count = Corner("Count", TextAnchor.LowerRight);
		_hotkey = Corner("Hotkey", TextAnchor.UpperLeft);
		_hotkey.color = LuiStyle.Gray;
		_highlight = LuiWidgets.Frame(transform, LuiStyle.Highlight, 2f);
	}

	private Text Corner(string name, TextAnchor alignment)
	{
		Text text = LuiWidgets.Label(transform, string.Empty, LuiStyle.SmallFontSize, LuiStyle.CastleYellow, alignment, LuiStyle.Bold);
		text.name = name;
		UiKit.SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-6f, -4f));
		text.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
		return text;
	}

	public void SetIcon(Sprite sprite)
	{
		if (_icon.sprite != sprite)
		{
			_icon.sprite = sprite;
		}
		_icon.enabled = sprite != null;
	}

	public void SetIconColor(Color color)
	{
		_iconColor = color;
		Color shown = _enabled ? color : color * 0.45f;
		if (_hovered && _enabled)
		{
			shown = Color.Lerp(shown, LuiStyle.White, 0.3f);
		}
		_icon.color = shown;
	}

	public void SetIconSize(float size)
	{
		_icon.rectTransform.sizeDelta = new Vector2(size, size);
	}

	public void SetCount(string text, Color color)
	{
		_count.text = text ?? string.Empty;
		_count.color = color;
	}

	public void SetHotkey(string text)
	{
		_hotkey.text = text ?? string.Empty;
	}

	public void SetLabel(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			if (_hoverText != null)
			{
				_hoverText.text = string.Empty;
			}
			return;
		}
		if (_hoverLabel == null)
		{
			BuildHoverLabel();
		}
		if (_hoverText.text != text)
		{
			_hoverText.text = text;
		}
		_hoverLabel.gameObject.SetActive(_hovered);
	}

	public void SetTooltip(string text)
	{
		_tooltip = text;
	}

	public void SetSelected(bool selected)
	{
		_selected = selected;
		SetBackground(selected ? LuiStyle.BgSelected : LuiStyle.BgDefault);
	}

	public void SetBackground(Color color)
	{
		_backgroundColor = color;
		_background.color = (_hovered && _enabled) ? LuiStyle.Hovered(color) : color;
	}

	// Lightens or restores the background and icon as the mouse comes and goes.
	private void ShowHover()
	{
		SetBackground(_backgroundColor);
		SetIconColor(_iconColor);
	}

	public void SetHighlight(bool highlight)
	{
		_highlight.SetActive(highlight);
	}

	public void SetEnabled(bool enabled)
	{
		_enabled = enabled;
		SetIconColor(_iconColor);
	}

	public bool Selected
	{
		get { return _selected; }
	}

	// The label strip beside the button (the game's ButtonPanel HoverLabel on a dark background), made on first use.
	private void BuildHoverLabel()
	{
		_hoverLabel = UiKit.CreateRect("HoverLabel", transform);
		Image background = _hoverLabel.gameObject.AddComponent<Image>();
		background.color = LuiStyle.HoverLabelBg;
		background.raycastTarget = false;
		HorizontalLayoutGroup layout = _hoverLabel.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(8, 8, 4, 4);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		ContentSizeFitter fitter = _hoverLabel.gameObject.AddComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		_hoverText = LuiWidgets.Label(_hoverLabel, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		// Outside the button's layout, beside it.
		LayoutElement ignore = _hoverLabel.gameObject.AddComponent<LayoutElement>();
		ignore.ignoreLayout = true;
		switch (LabelSide)
		{
		case Side.Left:
			_hoverLabel.anchorMin = _hoverLabel.anchorMax = new Vector2(0f, 0.5f);
			_hoverLabel.pivot = new Vector2(1f, 0.5f);
			break;
		case Side.Below:
			_hoverLabel.anchorMin = _hoverLabel.anchorMax = new Vector2(0.5f, 0f);
			_hoverLabel.pivot = new Vector2(0.5f, 1f);
			break;
		default:
			_hoverLabel.anchorMin = _hoverLabel.anchorMax = new Vector2(1f, 0.5f);
			_hoverLabel.pivot = new Vector2(0f, 0.5f);
			break;
		}
		_hoverLabel.anchoredPosition = Vector2.zero;
		_hoverLabel.gameObject.SetActive(false);
	}

	public void OnPointerEnter(PointerEventData eventData)
	{
		_hovered = true;
		ShowHover();
		if (_hoverLabel != null && !string.IsNullOrEmpty(_hoverText.text))
		{
			_hoverLabel.gameObject.SetActive(true);
			_hoverLabel.SetAsLastSibling();
		}
		if (!string.IsNullOrEmpty(_tooltip))
		{
			LuiTooltip.Show(_tooltip, (RectTransform)transform);
		}
		if (Hovered != null)
		{
			Hovered(true);
		}
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		_hovered = false;
		ShowHover();
		if (_hoverLabel != null)
		{
			_hoverLabel.gameObject.SetActive(false);
		}
		LuiTooltip.Hide((RectTransform)transform);
		if (Hovered != null)
		{
			Hovered(false);
		}
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || !_enabled || OnClick == null)
		{
			return;
		}
		OnClick(LuiInput.Shift, LuiInput.Ctrl);
	}

	private void OnDisable()
	{
		_hovered = false;
		ShowHover();
		LuiTooltip.Hide((RectTransform)transform);
		if (Hovered != null)
		{
			Hovered(false);
		}
	}
}

internal class LuiProgressBar : MonoBehaviour
{
	private Image _fill;

	private Text _text;

	public void Build()
	{
		Image background = gameObject.AddComponent<Image>();
		background.color = LuiStyle.BgDark;
		background.raycastTarget = false;
		RectTransform fill = UiKit.CreateRect("Fill", transform);
		UiKit.SetRect(fill, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_fill = fill.gameObject.AddComponent<Image>();
		_fill.color = LuiStyle.CastleYellow;
		_fill.raycastTarget = false;
		_text = LuiWidgets.Label(transform, string.Empty, LuiStyle.SmallFontSize, LuiStyle.White, TextAnchor.MiddleCenter, LuiStyle.Bold);
		UiKit.SetRect(_text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_text.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
	}

	// value: 0 to 1, filled from the left.
	public void Set(float value, string text)
	{
		RectTransform fill = _fill.rectTransform;
		fill.anchorMax = new Vector2(Mathf.Clamp01(value), 1f);
		_text.text = text ?? string.Empty;
	}

	public void SetColor(Color color)
	{
		_fill.color = color;
	}
}

internal class LuiCounter : MonoBehaviour
{
	private Text _value;

	public void Build(Action<int> step)
	{
		HorizontalLayoutGroup layout = gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 2f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = true;
		UiKit.CreateButton(transform, "-", 22f, () => step(LuiInput.Shift ? -10 : -1));
		_value = LuiWidgets.Label(transform, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleCenter, LuiStyle.Bold);
		UiKit.Flexible(_value.gameObject);
		UiKit.CreateButton(transform, "+", 22f, () => step(LuiInput.Shift ? 10 : 1));
	}

	public void Set(string value)
	{
		_value.text = value ?? string.Empty;
	}
}

// One tooltip at a time, on the canvas' tooltip layer, beside the thing it explains and kept on screen.
internal static class LuiTooltip
{
	private const float Width = 280f;

	private static RectTransform _panel;

	private static Text _text;

	private static RectTransform _target;

	public static void Show(string text, RectTransform target)
	{
		if (_panel == null)
		{
			Build();
		}
		_target = target;
		_text.text = LuaBridge.Localized(text);
		_panel.gameObject.SetActive(true);
		LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
		Place();
	}

	// Only the target that showed it hides it, so moving from one button to the next does not flicker.
	public static void Hide(RectTransform target)
	{
		if (_panel != null && (_target == target || target == null))
		{
			_panel.gameObject.SetActive(false);
			_target = null;
		}
	}

	private static void Build()
	{
		_panel = UiKit.CreateRect("Tooltip", LuiCanvas.Instance.Tooltips);
		_panel.pivot = new Vector2(0f, 1f);
		Image background = _panel.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		background.raycastTarget = false;
		VerticalLayoutGroup layout = _panel.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(10, 10, 8, 8);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		ContentSizeFitter fitter = _panel.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		_panel.sizeDelta = new Vector2(Width, 0f);
		_text = LuiWidgets.Label(_panel, string.Empty, LuiStyle.FontSize, LuiStyle.White, TextAnchor.UpperLeft, null);
		_text.horizontalOverflow = HorizontalWrapMode.Wrap;
		_text.supportRichText = true;
	}

	// Right of the target, or left of it where the right side has no room; moved up or down to stay on screen.
	private static void Place()
	{
		RectTransform layer = LuiCanvas.Instance.Tooltips;
		Vector3[] corners = new Vector3[4];
		_target.GetWorldCorners(corners);
		Vector2 topLeft = layer.InverseTransformPoint(corners[1]);
		Vector2 topRight = layer.InverseTransformPoint(corners[2]);
		Rect bounds = layer.rect;
		Vector2 size = _panel.rect.size;
		float x = topRight.x + 6f;
		if (x + size.x > bounds.xMax)
		{
			x = topLeft.x - 6f - size.x;
		}
		float y = Mathf.Clamp(topRight.y, bounds.yMin + size.y, bounds.yMax);
		_panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
		_panel.anchoredPosition = new Vector2(x, y);
	}
}
