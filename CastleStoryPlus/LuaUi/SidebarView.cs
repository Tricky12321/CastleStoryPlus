using System;
using System.Collections.Generic;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// One of GameMenu's floating sidebars (SidebarFloatingMenu.lua: new task, structures, blocks, settings) drawn in
// C#. The game fills such a sidebar from its handle's onLoad event (m.AddSection(sh), m.AddMenuHandleToggle(h, sh),
// m.AddMenuHandleSpacer(h), m.AddAlertHandle(h, sh)...); here that event gets a recorder instead of the Lua
// menu, so every listener (the game's and the mods') runs as it would and the sidebar is drawn from what they
// added, in the same order: a header with the handle's label and a close button, then sections (a caption and a
// grid of 64 pixel buttons). It shows while its handle is open in its group, so opening and closing stay the
// game's (hotkeys, the bars' toggles, the exclusive left and right groups). Buttons are read a few times a second
// only while the sidebar is open; when closed only their own events refresh them.
internal class LuaSidebarView : MonoBehaviour
{
	private const float Width = 272f;

	// GameMenu's left and right content overlays leave this much room at the bottom (the task list).
	private const float Bottom = 154f;

	private const float BarWidth = 48f;

	private const float PollSeconds = 0.25f;

	private class Section
	{
		public Table Handle;

		public GameObject Caption;

		public Text CaptionText;

		public RectTransform Grid;
	}

	private Table _handle;

	private RectTransform _panel;

	private Text _title;

	private Transform _content;

	private readonly Dictionary<Table, Section> _sections = new Dictionary<Table, Section>();

	private readonly List<LuaHandleView> _buttons = new List<LuaHandleView>();

	private readonly List<LuaElementView> _elements = new List<LuaElementView>();

	private readonly List<Action> _unlisten = new List<Action>();

	private bool _shown;

	private bool _dirty = true;

	private float _readAt;

	// Loads the handle's sidebar into a recorder (its onLoad event) and draws it, on the left (beside the left
	// bar) or on the right.
	public static LuaSidebarView Create(Transform parent, Table handle, bool rightSide)
	{
		Script script = handle.OwnerScript;
		LuaMenuRecorder recorder = LuaMenuRecorder.Create(script, LuaBridge.String(LuaBridge.Value(handle, "Label"), "sidebar"));
		Table onLoad = LuaBridge.GetTable(handle, "onLoad");
		if (onLoad != null)
		{
			LuaBridge.CallMethod(onLoad, "Invoke", DynValue.NewTable(recorder.Table));
		}
		RectTransform panel = UiKit.CreateRect("CS sidebar", parent);
		float x = rightSide ? 1f : 0f;
		panel.anchorMin = new Vector2(x, 0f);
		panel.anchorMax = new Vector2(x, 1f);
		panel.pivot = new Vector2(x, 0.5f);
		panel.offsetMin = new Vector2(rightSide ? -BarWidth - Width : BarWidth, Bottom);
		panel.offsetMax = new Vector2(rightSide ? -BarWidth : BarWidth + Width, 0f);
		Image background = panel.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		VerticalLayoutGroup layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(0, 0, 0, 4);
		layout.spacing = 0f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		LuaSidebarView view = panel.gameObject.AddComponent<LuaSidebarView>();
		view._handle = handle;
		view._panel = panel;
		view.BuildHeader();
		view.Build(recorder);
		view.Listen();
		LuiCanvas.Instance.Tick += view.Tick;
		view.SetShown(false);
		view.Tick();
		return view;
	}

	private void BuildHeader()
	{
		Transform header = UiKit.CreateRow(_panel, 40f);
		((HorizontalLayoutGroup)header.GetComponent<HorizontalLayoutGroup>()).padding = new RectOffset(10, 4, 0, 0);
		_title = LuiWidgets.Label(header, string.Empty, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		UiKit.Flexible(_title.gameObject);
		LuiIconButton close = LuiWidgets.IconButton(header, 32f);
		close.SetLabel(string.Empty);
		close.SetCount("X", LuiStyle.CastleYellow);
		close.SetBackground(LuiStyle.BgNone);
		close.OnClick = (bool shift, bool ctrl) =>
		{
			LuaBridge.CallMethod(_handle, "Close");
			_dirty = true;
		};
		LuiWidgets.Divider(_panel, vertical: false);
		_content = UiKit.CreateScrollList(_panel, out LayoutElement size);
		size.flexibleHeight = 1f;
		size.minHeight = 64f;
		VerticalLayoutGroup list = _content.GetComponent<VerticalLayoutGroup>();
		list.padding = new RectOffset(8, 8, 4, 4);
		list.spacing = 0f;
	}

	private void Build(LuaMenuRecorder recorder)
	{
		foreach (LuaMenuRecorder.Call call in recorder.Calls)
		{
			Table first = LuaMenuRecorder.TableArg(call, 0);
			Table section = LuaMenuRecorder.TableArg(call, 1);
			switch (call.Method)
			{
			case "AddSection":
				if (first != null)
				{
					AddSection(first);
				}
				break;
			case "AddMenuHandleToggle":
				if (first != null)
				{
					AddButton(first, section);
				}
				break;
			// The blueprint library's own panels (CsBlueprintLibrary): its name field goes under its section's
			// caption, its saved blueprints into their section's one-column list.
			case "AddCompoundHandleToggle":
				if (first != null)
				{
					_elements.Add(LuaCompoundView.Create(Into(section), first));
				}
				break;
			case "AddMenuHandleInputField":
				if (first != null)
				{
					_elements.Add(LuaInputFieldView.Create(_content, first));
				}
				break;
			case "AddMenuHandleSpacer":
				if (first != null)
				{
					_elements.Add(LuaSpacerView.Create(_content, first, 48f, horizontal: false));
				}
				break;
			case "AddAlertHandle":
				if (first != null)
				{
					_elements.Add(LuaAlertView.Create(Into(section), first));
				}
				break;
			case "AddDivider":
				LuiWidgets.Divider(Into(section), vertical: false);
				break;
			default:
				LuaBridge.LogOnce("sidebar " + recorder.Name, "no C# view for " + call.Method);
				break;
			}
		}
	}

	private void AddSection(Table handle)
	{
		Section section = new Section { Handle = handle };
		if (!LuaBridge.Bool(LuaBridge.Get(handle, "hideHeader"), false))
		{
			Text caption = LuiWidgets.Label(_content, LuaBridge.String(LuaBridge.Value(handle, "Label"), string.Empty), LuiStyle.FontSize, LuaBridge.Color(LuaBridge.Value(handle, "LabelColor"), LuiStyle.CastleYellow), TextAnchor.MiddleLeft, LuiStyle.Bold);
			LayoutElement element = caption.gameObject.AddComponent<LayoutElement>();
			element.minHeight = 28f;
			element.preferredHeight = 28f;
			section.Caption = caption.gameObject;
			section.CaptionText = caption;
		}
		Vector2 cell = new Vector2(64f, 64f);
		DynValue size = LuaBridge.Value(handle, "CellSize");
		if (size.Type == DataType.UserData && size.UserData != null && size.UserData.TryGet<Vector2>(out Vector2 cellSize))
		{
			cell = cellSize;
		}
		int columns = Mathf.Max(1, Mathf.FloorToInt((Width - 16f) / Mathf.Max(1f, cell.x)));
		section.Grid = LuiWidgets.Grid(_content, cell, 0f, columns);
		_sections[handle] = section;
	}

	// The grid of the handle's section, or the list itself when it has none (as SidebarFloatingMenu does).
	private Transform Into(Table section)
	{
		return (section != null && _sections.TryGetValue(section, out Section found)) ? found.Grid : _content;
	}

	private void AddButton(Table handle, Table section)
	{
		float size = 64f;
		if (section != null && _sections.TryGetValue(section, out Section found))
		{
			size = found.Grid.GetComponent<GridLayoutGroup>().cellSize.x;
		}
		LuaHandleView view = LuaHandleView.Create(Into(section), handle, size, LuiIconButton.Side.Below);
		view.LabelAsTooltip = true;
		// The catalogues' handles ask for 32 pixel icons, and their sprites have wide transparent borders: fill the cell.
		view.MinIconSize = size;
		view.Read();
		_buttons.Add(view);
	}

	private void Listen()
	{
		Script script = _handle.OwnerScript;
		foreach (string name in new string[] { "onOpen", "onClose" })
		{
			Table luaEvent = LuaBridge.GetTable(_handle, name);
			if (luaEvent != null)
			{
				_unlisten.Add(LuaBridge.Listen(luaEvent, script, () => _dirty = true));
			}
		}
		Table group = LuaBridge.GetTable(_handle, "parent");
		Table onAnyOpen = (group != null) ? LuaBridge.GetTable(group, "onAnyOpen") : null;
		if (onAnyOpen != null)
		{
			_unlisten.Add(LuaBridge.Listen(onAnyOpen, script, () => _dirty = true));
		}
	}

	private void Tick()
	{
		if (_handle == null || (!_dirty && Time.unscaledTime - _readAt < PollSeconds))
		{
			return;
		}
		_dirty = false;
		_readAt = Time.unscaledTime;
		bool shown = LuaBridge.Get(_handle, "parent").Type == DataType.Table && LuaBridge.Bool(LuaBridge.CallMethod(_handle, "IsOpened"), false);
		if (shown != _shown)
		{
			SetShown(shown);
		}
		if (_shown)
		{
			_title.text = LuaBridge.String(LuaBridge.Value(_handle, "Label"), string.Empty);
			foreach (Section section in _sections.Values)
			{
				bool visible = LuaBridge.Bool(LuaBridge.Value(section.Handle, "Visible"), true);
				if (section.Grid.gameObject.activeSelf != visible)
				{
					section.Grid.gameObject.SetActive(visible);
				}
				if (section.Caption != null && section.Caption.activeSelf != visible)
				{
					section.Caption.SetActive(visible);
				}
				// Some captions count what the section holds (the library's "Saved blueprints (3/16)").
				if (visible && section.CaptionText != null)
				{
					string caption = LuaBridge.String(LuaBridge.Value(section.Handle, "Label"), string.Empty);
					if (section.CaptionText.text != caption)
					{
						section.CaptionText.text = caption;
					}
				}
			}
		}
	}

	private void SetShown(bool shown)
	{
		_shown = shown;
		_panel.gameObject.SetActive(shown);
		foreach (LuaHandleView button in _buttons)
		{
			if (button != null)
			{
				button.Polling = shown;
				if (shown)
				{
					button.MarkDirty();
				}
			}
		}
		foreach (LuaElementView element in _elements)
		{
			if (element != null)
			{
				element.Polling = shown;
				if (shown)
				{
					element.MarkDirty();
				}
			}
		}
	}

	private void OnDestroy()
	{
		if (LuiCanvas.Exists)
		{
			LuiCanvas.Instance.Tick -= Tick;
		}
		if (GameMenuLink.Script != null)
		{
			foreach (Action unlisten in _unlisten)
			{
				unlisten();
			}
		}
		_unlisten.Clear();
		_handle = null;
	}
}
