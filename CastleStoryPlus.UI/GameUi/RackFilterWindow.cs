using Brix.Game.Semantique;
using CastleStoryPlus.Building;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// While a weapon stand is selected: which soldier classes' gear may be put on it (RackFilter), one button per class,
// lit when the class is let in. Placed and dragged like the station windows.
internal class CsRackFilterWindow : MonoBehaviour
{
	private const float Width = 330f;

	private const float ReadSeconds = 0.25f;

	private RectTransform _rect;

	private RectTransform _body;

	private Image _background;

	private CsWindowDrag _drag;

	private Button[] _classes;

	private ToolRack _rack;

	private float _readAt;

	private bool _shown;

	public static CsRackFilterWindow Create(Transform parent)
	{
		CsRackFilterWindow window = new GameObject("CS rack filter", typeof(RectTransform)).AddComponent<CsRackFilterWindow>();
		window.Setup(parent);
		return window;
	}

	private void Setup(Transform parent)
	{
		_rect = (RectTransform)transform;
		_rect.SetParent(parent, false);
		_rect.anchorMin = _rect.anchorMax = new Vector2(0f, 0f);
		_rect.pivot = new Vector2(0f, 0f);
		_rect.anchoredPosition = new Vector2(CsPanelView.BarWidth, GameTaskList.Height + 4f);
		_rect.sizeDelta = new Vector2(Width, 0f);
		_drag = CsWindowDrag.Add(_rect, "CS rack filter");
		_background = gameObject.AddComponent<Image>();
		UiTheme.Round(_background, UiTheme.BgPopup, UiTheme.PanelRadius);
		VerticalLayoutGroup layout = gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(12, 12, 10, 12);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		_body = LuiWidgets.Vertical(_rect, 8f, 0);
		DestroyImmediate(_body.GetComponent<ContentSizeFitter>());
		_body.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;

		LuiWidgets.Label(_body, "Weapon stand", LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		Text note = LuiWidgets.Label(_body, "Gear the stand takes (click a class to let it in or leave it out; what is on the stand stays):", LuiStyle.SmallFontSize, UiTheme.TextDim, TextAnchor.UpperLeft, LuiStyle.Regular);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		RectTransform grid = LuiWidgets.Grid(_body, new Vector2((Width - 24f - 8f) / 3f, 28f), 4f, 3);
		DestroyImmediate(grid.GetComponent<ContentSizeFitter>());
		_classes = new Button[RackFilter.ClassNames.Length];
		for (int i = 0; i < _classes.Length; i++)
		{
			int index = i;
			Button button = UiKit.CreateButton(grid, RackFilter.ClassNames[i], 0f, () =>
			{
				if (_rack != null && !GameUiState.Paused)
				{
					RackFilter.Toggle(_rack, index);
					Read();
				}
			});
			Image image = button.GetComponent<Image>();
			image.sprite = UiTheme.Fill(UiTheme.SlotRadius);
			image.type = Image.Type.Sliced;
			_classes[i] = button;
		}
		_body.gameObject.SetActive(false);
		_background.enabled = false;
	}

	private void Update()
	{
		ToolRack rack = Plugin.IsEnabled(Core.Features.RackFilter) ? RackFilter.Selected() : null;
		if (!ReferenceEquals(rack, _rack))
		{
			_rack = rack;
			_readAt = 0f;
		}
		Show(rack != null);
		if (rack == null)
		{
			return;
		}
		if (Time.unscaledTime - _readAt > ReadSeconds)
		{
			_readAt = Time.unscaledTime;
			Read();
		}
		if (_drag.TryPlace(out Vector2 moved))
		{
			_rect.anchoredPosition = moved;
		}
	}

	private void Show(bool shown)
	{
		if (shown == _shown)
		{
			return;
		}
		_shown = shown;
		_body.gameObject.SetActive(shown);
		_background.enabled = shown;
		if (shown)
		{
			LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
		}
	}

	private void Read()
	{
		if (_rack == null)
		{
			return;
		}
		for (int i = 0; i < _classes.Length; i++)
		{
			bool takes = RackFilter.Allows(_rack, i);
			Text label = _classes[i].GetComponentInChildren<Text>();
			if (label != null)
			{
				label.color = takes ? LuiStyle.CastleYellow : UiTheme.TextDim;
			}
			ColorBlock colors = _classes[i].colors;
			colors.normalColor = takes ? new Color(1f, 197f / 255f, 10f / 255f, 0.22f) : UiTheme.BgSlot;
			_classes[i].colors = colors;
		}
	}
}
