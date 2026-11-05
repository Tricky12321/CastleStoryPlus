using System.Collections.Generic;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// A sidebar beside the left or right bar (SidebarFloatingMenu.lua's look): a header with the panel's title and a
// close button, then its sections, each a caption and a grid of buttons (other items as rows). It shows while its
// panel is the open one of its group.
internal class CsPanelView : MonoBehaviour
{
	internal const float Width = 272f;

	// The left and right overlays leave this much room at the bottom (the task list).
	internal const float Bottom = 154f;

	internal const float BarWidth = 48f;

	// The gap between a catalogue's cells.
	private const float CellGap = 5f;

	private sealed class SectionView
	{
		public UiSection Section;

		public GameObject Caption;

		public Text CaptionText;

		public RectTransform Body;
	}

	// How many sidebars are open on each side, for the task popup to stand beside them instead of over them.
	internal static int LeftOpen;

	internal static int RightOpen;

	private bool _rightSide;

	private UiPanel _panel;

	private RectTransform _rect;

	private Text _title;

	private Transform _content;

	private readonly List<SectionView> _sections = new List<SectionView>();

	private float _width = Width;

	public static CsPanelView Create(Transform parent, UiPanel panel, bool rightSide)
	{
		return Create(parent, panel, rightSide, BarWidth, Width, Bottom);
	}

	// Beside a bar barWidth wide, width wide, from bottom up to the top of the screen (the world editor's panels).
	public static CsPanelView Create(Transform parent, UiPanel panel, bool rightSide, float barWidth, float width, float bottom)
	{
		RectTransform rect = UiKit.CreateRect("CS panel " + panel.Id, parent);
		float x = rightSide ? 1f : 0f;
		rect.anchorMin = new Vector2(x, 0f);
		rect.anchorMax = new Vector2(x, 1f);
		rect.pivot = new Vector2(x, 0.5f);
		rect.offsetMin = new Vector2(rightSide ? -barWidth - width : barWidth, bottom);
		rect.offsetMax = new Vector2(rightSide ? -barWidth : barWidth + width, 0f);
		Image background = rect.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(0, 0, 0, 4);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		CsPanelView view = rect.gameObject.AddComponent<CsPanelView>();
		view._panel = panel;
		view._rect = rect;
		view._rightSide = rightSide;
		view._width = width;
		view.BuildHeader();
		view.BuildSections();
		if (panel.Group != null)
		{
			panel.Group.Changed += view.OnGroupChanged;
		}
		GameUiState.Changed += view.ReadSections;
		view.OnGroupChanged();
		return view;
	}

	private void BuildHeader()
	{
		Transform header = UiKit.CreateRow(_rect, 40f);
		header.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(10, 4, 0, 0);
		_title = LuiWidgets.Label(header, string.Empty, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		UiKit.Flexible(_title.gameObject);
		LuiIconButton close = LuiWidgets.IconButton(header, 32f);
		close.SetText("X", LuiStyle.CastleYellow);
		close.SetBackground(LuiStyle.BgNone);
		close.OnClick = (bool shift, bool ctrl) => _panel.Close();
		LuiWidgets.Divider(_rect, vertical: false);
		_content = UiKit.CreateScrollList(_rect, out LayoutElement size);
		size.flexibleHeight = 1f;
		size.minHeight = 64f;
		VerticalLayoutGroup list = _content.GetComponent<VerticalLayoutGroup>();
		list.padding = new RectOffset(8, 8, 4, 4);
		list.spacing = 0f;
	}

	private void BuildSections()
	{
		foreach (UiSection section in _panel.Sections)
		{
			SectionView view = new SectionView { Section = section };
			if (section.Title != null)
			{
				Text caption = LuiWidgets.Label(_content, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
				LayoutElement element = caption.gameObject.AddComponent<LayoutElement>();
				element.minHeight = 28f;
				element.preferredHeight = 28f;
				view.Caption = caption.gameObject;
				view.CaptionText = caption;
			}
			bool buttons = section.Items.TrueForAll((UiItem i) => i is UiButton);
			if (buttons)
			{
				// As many columns as cells of the section's size fit, a little smaller to leave a gap between them.
				int columns = Mathf.Max(1, Mathf.FloorToInt((_width - 16f) / Mathf.Max(1f, section.CellSize)));
				float cell = Mathf.Floor((_width - 16f - (columns - 1) * CellGap) / columns);
				view.Body = LuiWidgets.Grid(_content, new Vector2(cell, cell), CellGap, columns);
			}
			else
			{
				view.Body = LuiWidgets.Vertical(_content, 2f, 0);
			}
			float size = buttons ? ((GridLayoutGroup)view.Body.GetComponent<GridLayoutGroup>()).cellSize.x : section.CellSize;
			foreach (UiItem item in section.Items)
			{
				CsItemFactory.Create(view.Body, item, size, LuiIconButton.Side.Below, catalogue: true);
			}
			_sections.Add(view);
		}
	}

	private void OnEnable()
	{
		if (_rightSide)
		{
			RightOpen++;
		}
		else
		{
			LeftOpen++;
		}
	}

	private void OnDisable()
	{
		if (_rightSide)
		{
			RightOpen = Mathf.Max(0, RightOpen - 1);
		}
		else
		{
			LeftOpen = Mathf.Max(0, LeftOpen - 1);
		}
	}

	private void OnGroupChanged()
	{
		bool open = _panel.IsOpen;
		if (_rect.gameObject.activeSelf != open)
		{
			_rect.gameObject.SetActive(open);
		}
		if (open)
		{
			ReadSections();
			CsItemViews.MarkAll();
		}
	}

	private void ReadSections()
	{
		if (!_rect.gameObject.activeSelf)
		{
			return;
		}
		_title.text = (_panel.Title != null) ? _panel.Title() : string.Empty;
		foreach (SectionView view in _sections)
		{
			bool visible = view.Section.IsVisible();
			if (view.Body.gameObject.activeSelf != visible)
			{
				view.Body.gameObject.SetActive(visible);
			}
			if (view.Caption != null)
			{
				if (view.Caption.activeSelf != visible)
				{
					view.Caption.SetActive(visible);
				}
				if (visible)
				{
					string caption = view.Section.Title();
					if (view.CaptionText.text != caption)
					{
						view.CaptionText.text = caption;
					}
				}
			}
		}
	}

	private void OnDestroy()
	{
		if (_panel != null && _panel.Group != null)
		{
			_panel.Group.Changed -= OnGroupChanged;
		}
		GameUiState.Changed -= ReadSections;
	}
}

// The left or right bar: a column of 48 pixel buttons over the screen's full height (LeftBarMenu.lua). A null item
// is a spacer that takes the room left over.
internal class CsBarView : MonoBehaviour
{
	private const int ButtonInset = 4;

	public static CsBarView Create(Transform parent, List<UiItem> items, bool rightSide, Color spacerColor)
	{
		RectTransform rect = UiKit.CreateRect(rightSide ? "CS right bar" : "CS left bar", parent);
		float x = rightSide ? 1f : 0f;
		rect.anchorMin = new Vector2(x, 0f);
		rect.anchorMax = new Vector2(x, 1f);
		rect.pivot = new Vector2(x, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(CsPanelView.BarWidth, 0f);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		// The buttons a little in from the screen's edge and apart, each its own rounded square.
		layout.padding = new RectOffset(ButtonInset, ButtonInset, ButtonInset, ButtonInset);
		layout.spacing = ButtonInset;
		layout.childAlignment = TextAnchor.MiddleCenter;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		CsBarView view = rect.gameObject.AddComponent<CsBarView>();
		LuiIconButton.Side side = rightSide ? LuiIconButton.Side.Left : LuiIconButton.Side.Right;
		foreach (UiItem item in items)
		{
			if (item == null)
			{
				RectTransform spacer = UiKit.CreateRect("Spacer", rect);
				Image image = spacer.gameObject.AddComponent<Image>();
				image.color = spacerColor;
				LayoutElement element = spacer.gameObject.AddComponent<LayoutElement>();
				element.flexibleHeight = 1f;
				element.minHeight = 1f;
				continue;
			}
			CsItemFactory.Create(rect, item, CsPanelView.BarWidth - 2 * ButtonInset, side, catalogue: false);
		}
		return view;
	}
}

// The top bar: the selected task's name in a dark strip at the top centre while a task other than the idle group is
// selected (TopBarMenu.lua).
internal class CsTopBarView : MonoBehaviour
{
	private RectTransform _strip;

	private Text _label;

	public static CsTopBarView Create(Transform parent)
	{
		RectTransform rect = UiKit.CreateRect("CS top bar", parent);
		UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		CsTopBarView view = rect.gameObject.AddComponent<CsTopBarView>();
		RectTransform strip = UiKit.CreateRect("Strip", rect);
		strip.anchorMin = strip.anchorMax = new Vector2(0.5f, 1f);
		strip.pivot = new Vector2(0.5f, 1f);
		strip.anchoredPosition = Vector2.zero;
		strip.sizeDelta = new Vector2(320f, 64f);
		Image background = strip.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		background.raycastTarget = false;
		view._strip = strip;
		view._label = LuiWidgets.Label(strip, string.Empty, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleCenter, LuiStyle.Title);
		UiKit.SetRect(view._label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		GameUiState.Changed += view.Read;
		view.Read();
		return view;
	}

	private void Read()
	{
		bool shown = GameUiState.IsSelectedNotIdle;
		if (_strip.gameObject.activeSelf != shown)
		{
			_strip.gameObject.SetActive(shown);
		}
		if (shown)
		{
			_label.text = GameMenuTexts.TaskName(GameUiState.Selected);
		}
	}

	private void OnDestroy()
	{
		GameUiState.Changed -= Read;
	}
}
