using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static CastleStoryPlus.GameUi.CsControlParts;

namespace CastleStoryPlus.GameUi;

// The control views' parts placed by their edges: offsetMin is the lower left corner's distance from its anchor,
// offsetMax the upper right's.
internal static class CsControlParts
{
	internal static void Inset(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
	{
		rect.anchorMin = anchorMin;
		rect.anchorMax = anchorMax;
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.offsetMin = offsetMin;
		rect.offsetMax = offsetMax;
	}
}

// A UiToggle: its label on the left and a check box on the right (CheckboxPanel.lua). The whole row is clickable.
internal class CsToggleView : CsItemView, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
{
	private const float Box = 20f;

	private UiToggle _toggle;

	private Text _label;

	private Image _box;

	private Image _check;

	private Image _background;

	private bool _enabled = true;

	protected override UiItem Item
	{
		get { return _toggle; }
	}

	public static CsToggleView Create(Transform parent, UiToggle toggle)
	{
		RectTransform row = UiKit.CreateRect("Toggle " + toggle.Id, parent);
		LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
		element.minHeight = 30f;
		element.preferredHeight = 30f;
		element.preferredWidth = toggle.Width;
		CsToggleView view = row.gameObject.AddComponent<CsToggleView>();
		view._toggle = toggle;
		view._background = row.gameObject.AddComponent<Image>();
		LuiStyle.Paint(view._background, LuiStyle.ButtonSprite, LuiStyle.BgNone);
		view._label = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._label.raycastTarget = false;
		Inset(view._label.rectTransform, Vector2.zero, Vector2.one, new Vector2(2f, 0f), new Vector2(-(Box + 8f), 0f));
		RectTransform box = UiKit.CreateRect("Box", row);
		box.anchorMin = box.anchorMax = new Vector2(1f, 0.5f);
		box.pivot = new Vector2(1f, 0.5f);
		box.anchoredPosition = new Vector2(-4f, 0f);
		box.sizeDelta = new Vector2(Box, Box);
		view._box = box.gameObject.AddComponent<Image>();
		LuiStyle.Paint(view._box, LuiStyle.ButtonSprite, LuiStyle.BgDark);
		view._box.raycastTarget = false;
		LuiWidgets.Frame(box, LuiStyle.CastleYellow, 1f);
		RectTransform check = UiKit.CreateRect("Check", box);
		Inset(check, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));
		view._check = check.gameObject.AddComponent<Image>();
		LuiStyle.Paint(view._check, LuiStyle.ButtonSprite, LuiStyle.CastleYellow);
		view._check.raycastTarget = false;
		return view;
	}

	protected override void Read()
	{
		_enabled = _toggle.Enabled == null || _toggle.Enabled();
		string label = (_toggle.Label != null) ? _toggle.Label() : string.Empty;
		if (_label.text != label)
		{
			_label.text = label;
		}
		Color color = _enabled ? LuiStyle.CastleYellow : LuiStyle.Gray;
		_label.color = color;
		bool on = _toggle.Value != null && _toggle.Value();
		_check.enabled = on;
		_check.color = color;
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || !_enabled || _toggle.Set == null)
		{
			return;
		}
		bool on = !(_toggle.Value != null && _toggle.Value());
		_toggle.Set(on);
		GameUiState.Sound(on ? "WorldEditor_UI_Toggle_Checked" : "WorldEditor_UI_Toggle_Unchecked");
		GameUiState.Announce();
		Refresh();
	}

	public void OnPointerEnter(PointerEventData eventData)
	{
		if (_enabled)
		{
			_background.color = LuiStyle.FgHover;
		}
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		_background.color = LuiStyle.BgNone;
	}
}

// A UiSlider: its label and value on one line, the slider under them (SliderInputPanel.lua).
internal class CsSliderView : CsItemView
{
	private UiSlider _slider;

	private Text _label;

	private Text _value;

	private Slider _control;

	private Image _fill;

	private Image _handle;

	// While the view sets the slider: its change is not the player's.
	private bool _reading;

	protected override UiItem Item
	{
		get { return _slider; }
	}

	public static CsSliderView Create(Transform parent, UiSlider slider)
	{
		RectTransform row = UiKit.CreateRect("Slider " + slider.Id, parent);
		LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
		element.minHeight = 48f;
		element.preferredHeight = 48f;
		element.preferredWidth = slider.Width;
		CsSliderView view = row.gameObject.AddComponent<CsSliderView>();
		view._slider = slider;
		view._label = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		Inset(view._label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(2f, -24f), new Vector2(-70f, 0f));
		view._value = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.White, TextAnchor.MiddleRight, LuiStyle.Bold);
		RectTransform value = view._value.rectTransform;
		value.anchorMin = value.anchorMax = new Vector2(1f, 1f);
		value.pivot = new Vector2(1f, 0.5f);
		value.anchoredPosition = new Vector2(-4f, -12f);
		value.sizeDelta = new Vector2(66f, 24f);
		view._control = BuildSlider(row, out view._fill, out view._handle);
		view._control.onValueChanged.AddListener(view.OnChanged);
		return view;
	}

	// A plain uGUI slider: a dark track, a yellow fill and a square handle, over the row's lower half.
	private static Slider BuildSlider(RectTransform row, out Image fill, out Image handle)
	{
		RectTransform rect = UiKit.CreateRect("Slider", row);
		rect.anchorMin = new Vector2(0f, 0f);
		rect.anchorMax = new Vector2(1f, 0f);
		rect.pivot = new Vector2(0.5f, 0f);
		rect.anchoredPosition = new Vector2(0f, 4f);
		rect.sizeDelta = new Vector2(-16f, 18f);
		Image track = UiKit.CreateRect("Track", rect).gameObject.AddComponent<Image>();
		UiKit.SetRect(track.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(0f, 6f));
		LuiStyle.Paint(track, LuiStyle.ButtonSprite, LuiStyle.BgDark);
		RectTransform fillArea = UiKit.CreateRect("Fill Area", rect);
		UiKit.SetRect(fillArea, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(0f, 6f));
		fill = UiKit.CreateRect("Fill", fillArea).gameObject.AddComponent<Image>();
		UiKit.SetRect(fill.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		LuiStyle.Paint(fill, LuiStyle.ButtonSprite, LuiStyle.CastleYellow);
		fill.raycastTarget = false;
		RectTransform handleArea = UiKit.CreateRect("Handle Area", rect);
		UiKit.SetRect(handleArea, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-14f, 0f));
		handle = UiKit.CreateRect("Handle", handleArea).gameObject.AddComponent<Image>();
		handle.rectTransform.sizeDelta = new Vector2(14f, 0f);
		LuiStyle.Paint(handle, LuiStyle.ButtonSprite, LuiStyle.CastleYellowLight);
		Slider slider = rect.gameObject.AddComponent<Slider>();
		slider.direction = Slider.Direction.LeftToRight;
		slider.fillRect = fill.rectTransform;
		slider.handleRect = handle.rectTransform;
		slider.targetGraphic = handle;
		slider.transition = Selectable.Transition.None;
		Navigation navigation = slider.navigation;
		navigation.mode = Navigation.Mode.None;
		slider.navigation = navigation;
		return slider;
	}

	protected override void Read()
	{
		bool enabled = _slider.Enabled == null || _slider.Enabled();
		string label = (_slider.Label != null) ? _slider.Label() : string.Empty;
		if (_label.text != label)
		{
			_label.text = label;
		}
		Color color = enabled ? LuiStyle.CastleYellow : LuiStyle.Gray;
		_label.color = color;
		_fill.color = color;
		_handle.color = enabled ? LuiStyle.CastleYellowLight : LuiStyle.Gray;
		_control.interactable = enabled;
		_reading = true;
		try
		{
			_control.wholeNumbers = _slider.WholeNumbers;
			float min = (_slider.Min != null) ? _slider.Min() : 0f;
			float max = (_slider.Max != null) ? _slider.Max() : 1f;
			if (_control.minValue != min)
			{
				_control.minValue = min;
			}
			if (_control.maxValue != max)
			{
				_control.maxValue = Mathf.Max(min, max);
			}
			float value = (_slider.Value != null) ? _slider.Value() : min;
			if (_control.value != value)
			{
				_control.value = value;
			}
		}
		finally
		{
			_reading = false;
		}
		ShowValue(_control.value);
	}

	private void ShowValue(float value)
	{
		string text = (_slider.Format != null) ? _slider.Format(value) : value.ToString(_slider.WholeNumbers ? "0" : "0.0");
		if (_value.text != text)
		{
			_value.text = text;
		}
	}

	private void OnChanged(float value)
	{
		if (_reading || _slider.Set == null)
		{
			return;
		}
		_slider.Set(value);
		ShowValue(value);
	}
}

// A UiPicture: a texture (part of it) at the picture's size, centred in its row.
internal class CsPictureView : CsItemView
{
	private UiPicture _picture;

	private RawImage _image;

	protected override UiItem Item
	{
		get { return _picture; }
	}

	public static CsPictureView Create(Transform parent, UiPicture picture)
	{
		RectTransform row = UiKit.CreateRect("Picture " + picture.Id, parent);
		LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
		element.minHeight = picture.Height + 8f;
		element.preferredHeight = picture.Height + 8f;
		element.preferredWidth = picture.Width + 8f;
		Image background = row.gameObject.AddComponent<Image>();
		background.color = new Color(0f, 0f, 0f, 0.75f);
		background.raycastTarget = false;
		CsPictureView view = row.gameObject.AddComponent<CsPictureView>();
		view._picture = picture;
		RectTransform rect = UiKit.CreateRect("Image", row);
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(picture.Width, picture.Height);
		view._image = rect.gameObject.AddComponent<RawImage>();
		view._image.raycastTarget = false;
		return view;
	}

	protected override void Read()
	{
		Texture texture = (_picture.Texture != null) ? _picture.Texture() : null;
		if (_image.texture != texture)
		{
			_image.texture = texture;
		}
		_image.enabled = texture != null;
		_image.uvRect = (_picture.Uv != null) ? _picture.Uv() : new Rect(0f, 0f, 1f, 1f);
	}
}
