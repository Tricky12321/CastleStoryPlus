using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using UnityEngine;

namespace CastleStoryPlus.LuaUi;

// Draws one of the game's menu handles (Info/Lua/MenuHandle.lua; GameMenu's _m.mh/bh entries) as an icon button,
// reading what the game's own button panel reads from it: Visible, Icon, IconSize, IconColor, Label, LabelColor,
// Count, CountColor, HotkeyLabel_Text (when hasHotkey), Highlight, Tooltip, and TooltipPath's article on hover
// (CsTooltips). A click calls its OnAction, or, like the
// game's bars (LeftBarMenu.AddMenuHandleToggle), toggles the handle's menu when it has none. The handle is read
// again when it says so (its ev_refresh and onRefreshHighlight events) and a few times a second, since many of
// its closures depend on game state no event announces.
internal class LuaHandleView : MonoBehaviour
{
	private const float PollSeconds = 0.25f;

	private Table _handle;

	private LuiIconButton _button;

	private readonly List<Action> _unlisten = new List<Action>();

	private bool _dirty = true;

	private float _readAt;

	public Table Handle
	{
		get { return _handle; }
	}

	public LuiIconButton Button
	{
		get { return _button; }
	}

	// Whether the handle's Visible closure said so at the last read.
	public bool Shown { get; private set; }

	// Off while the view's menu is closed (a catalogue's many buttons): the handle is then only read when it says
	// so (its events), not a few times a second.
	public bool Polling = true;

	// The label is shown as a tooltip instead of the strip beside the button (buttons inside a scrolling list,
	// whose mask would cut the strip off).
	public bool LabelAsTooltip;

	// No label at all, neither beside the button nor as its tooltip (the task popup's buttons, whose article
	// tooltip says what they do).
	public bool HideLabel;

	// The smallest icon drawn, whatever the handle's IconSize says (the build catalogues' big cells); 0 = none.
	public float MinIconSize;

	public static LuaHandleView Create(Transform parent, Table handle, float size, LuiIconButton.Side labelSide)
	{
		LuiIconButton button = LuiWidgets.IconButton(parent, size);
		button.LabelSide = labelSide;
		LuaHandleView view = button.gameObject.AddComponent<LuaHandleView>();
		view.Bind(handle, button);
		return view;
	}

	private void Bind(Table handle, LuiIconButton button)
	{
		_handle = handle;
		_button = button;
		_button.OnClick = (bool shift, bool ctrl) => Act();
		// A handle with an article tooltip (TooltipPath) shows it on hover, as the game's menus do (CsTooltips).
		if (LuaUiConfig.CsTooltips && !LuaBridge.Get(handle, "TooltipPath").IsNil())
		{
			_button.Hovered = (bool over) =>
			{
				if (over)
				{
					CsArticles.ShowFor(LuaBridge.Value(_handle, "TooltipPath"), (RectTransform)_button.transform);
				}
				else
				{
					CsArticles.HideFor((RectTransform)_button.transform);
				}
			};
		}
		// Ticked by the canvas, not by Update: a hidden handle's button is inactive and must still be read to
		// show it again.
		LuiCanvas.Instance.Tick += Tick;
		Script script = handle.OwnerScript;
		foreach (string name in new string[] { "ev_refresh", "onRefreshHighlight" })
		{
			Table luaEvent = LuaBridge.GetTable(handle, name);
			if (luaEvent != null)
			{
				_unlisten.Add(LuaBridge.Listen(luaEvent, script, MarkDirty));
			}
		}
		Read();
	}

	public void MarkDirty()
	{
		_dirty = true;
	}

	private void Tick()
	{
		if (_handle == null || _button == null)
		{
			return;
		}
		if (_dirty || (Polling && Time.unscaledTime - _readAt >= PollSeconds))
		{
			Read();
		}
	}

	// Reads every value the button shows from the handle.
	public void Read()
	{
		_dirty = false;
		_readAt = Time.unscaledTime;
		Shown = LuaBridge.Bool(LuaBridge.Value(_handle, "Visible"), true);
		if (_button.gameObject.activeSelf != Shown)
		{
			_button.gameObject.SetActive(Shown);
		}
		if (!Shown)
		{
			return;
		}
		_button.SetIcon(LuaBridge.Sprite(LuaBridge.Value(_handle, "Icon")));
		float iconSize = Mathf.Max(LuaBridge.Number(LuaBridge.Value(_handle, "IconSize"), 0f), MinIconSize);
		if (iconSize > 0f)
		{
			_button.SetIconSize(iconSize);
		}
		_button.SetIconColor(LuaBridge.Color(LuaBridge.Value(_handle, "IconColor"), LuiStyle.CastleYellow));
		string label = LuaBridge.String(LuaBridge.Value(_handle, "Label"), string.Empty);
		_button.SetLabel((LabelAsTooltip || HideLabel) ? string.Empty : label);
		_button.SetCount(LuaBridge.String(LuaBridge.Value(_handle, "Count"), string.Empty), LuaBridge.Color(LuaBridge.Value(_handle, "CountColor"), LuiStyle.CastleYellow));
		bool hotkey = LuaBridge.Bool(LuaBridge.Get(_handle, "hasHotkey"), false);
		_button.SetHotkey(hotkey ? LuaBridge.String(LuaBridge.Value(_handle, "HotkeyLabel_Text"), string.Empty) : string.Empty);
		_button.SetHighlight(IsHighlighted(LuaBridge.Value(_handle, "Highlight")));
		string tooltip = LuaBridge.String(LuaBridge.Value(_handle, "Tooltip"), null);
		_button.SetTooltip((LabelAsTooltip && !HideLabel && string.IsNullOrEmpty(tooltip)) ? label : tooltip);
		_button.SetSelected(IsOpened());
	}

	// HighlightMode (the game's FrameHighlight enum): anything but None draws the frame.
	internal static bool IsHighlighted(DynValue value)
	{
		if (value.IsNil())
		{
			return false;
		}
		if (value.Type == DataType.Boolean)
		{
			return value.Boolean;
		}
		if (value.Type == DataType.Number)
		{
			return value.Number != 0;
		}
		// The game hands its enums to Lua wrapped in LuaEnumProxy.
		if (value.Type == DataType.UserData && value.UserData != null && value.UserData.TryGet<LuaEnumProxy<UniversalButtonInterface.HiglightsModes>>(out LuaEnumProxy<UniversalButtonInterface.HiglightsModes> mode))
		{
			return mode.value != UniversalButtonInterface.HiglightsModes.None && mode.value != UniversalButtonInterface.HiglightsModes.Invalid;
		}
		return false;
	}

	// A handle in a menu group is open when the group says so (MenuHandle.IsOpened answers true without a group).
	public bool IsOpened()
	{
		if (LuaBridge.Get(_handle, "parent").Type != DataType.Table)
		{
			return false;
		}
		return LuaBridge.Bool(LuaBridge.CallMethod(_handle, "IsOpened"), false);
	}

	public void Act()
	{
		DynValue action = LuaBridge.Get(_handle, "OnAction");
		if (!action.IsNil())
		{
			LuaBridge.Call(action, "OnAction");
		}
		else if (LuaBridge.Get(_handle, "parent").Type == DataType.Table)
		{
			LuaBridge.CallMethod(_handle, "Toggle");
		}
		MarkDirty();
	}

	// A handle's step closure (the work limit's + and -): true when it changed something.
	public bool Step(int direction)
	{
		DynValue step = LuaBridge.Get(_handle, "Step");
		if (step.IsNil())
		{
			return false;
		}
		bool changed = LuaBridge.Bool(LuaBridge.Call(step, "Step", DynValue.NewNumber(direction)), false);
		MarkDirty();
		return changed;
	}

	private void OnDestroy()
	{
		if (LuiCanvas.Exists)
		{
			LuiCanvas.Instance.Tick -= Tick;
		}
		// When the game's script is already gone its events are too; LuaBridge swallows the failed removal.
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
