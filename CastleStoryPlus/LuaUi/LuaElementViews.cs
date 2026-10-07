using System;
using System.Collections.Generic;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// The smaller pieces the game's Lua menus draw from a handle besides its button: a spacer (a coloured gap), a
// text (the top bar's task name, the left bar's task count) and an alert row (an icon and a message). Like
// LuaHandleView they read the handle again when it says so (ev_refresh) and a few times a second while Polling.
internal abstract class LuaElementView : MonoBehaviour
{
	private const float PollSeconds = 0.25f;

	protected Table Handle;

	private readonly List<Action> _unlisten = new List<Action>();

	private bool _dirty = true;

	private float _readAt;

	public bool Polling = true;

	protected void Bind(Table handle)
	{
		Handle = handle;
		LuiCanvas.Instance.Tick += Tick;
		Table refresh = LuaBridge.GetTable(handle, "ev_refresh");
		if (refresh != null)
		{
			_unlisten.Add(LuaBridge.Listen(refresh, handle.OwnerScript, MarkDirty));
		}
		Read();
	}

	public void MarkDirty()
	{
		_dirty = true;
	}

	private void Tick()
	{
		if (Handle != null && (_dirty || (Polling && Time.unscaledTime - _readAt >= PollSeconds)))
		{
			_dirty = false;
			_readAt = Time.unscaledTime;
			Read();
		}
	}

	protected abstract void Read();

	// The handle's Visible closure; fallback when it has none.
	protected bool Visible(bool fallback)
	{
		bool shown = LuaBridge.Bool(LuaBridge.Value(Handle, "Visible"), fallback);
		if (gameObject.activeSelf != shown)
		{
			gameObject.SetActive(shown);
		}
		return shown;
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
		Handle = null;
	}
}

// A gap of the handle's Height (-1 with a FlexibleHeight: as much as is left), in its BgColorDefault.
internal class LuaSpacerView : LuaElementView
{
	private Image _image;

	private LayoutElement _element;

	public static LuaSpacerView Create(Transform parent, Table handle, float defaultHeight, bool horizontal)
	{
		RectTransform rect = UiKit.CreateRect("Spacer", parent);
		LuaSpacerView view = rect.gameObject.AddComponent<LuaSpacerView>();
		// Part of the bar or sidebar it is in: the mouse over it is over the menu, not the world.
		view._image = rect.gameObject.AddComponent<Image>();
		view._element = rect.gameObject.AddComponent<LayoutElement>();
		float height = LuaBridge.Number(LuaBridge.Value(handle, "Height"), defaultHeight);
		float flexible = LuaBridge.Number(LuaBridge.Value(handle, "FlexibleHeight"), -1f);
		if (horizontal)
		{
			view._element.flexibleWidth = 1f;
		}
		if (height >= 0f)
		{
			view._element.minHeight = height;
			view._element.preferredHeight = height;
		}
		if (flexible > 0f)
		{
			view._element.flexibleHeight = flexible;
		}
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		if (!Visible(true))
		{
			return;
		}
		_image.color = LuaBridge.Color(LuaBridge.Value(Handle, "BgColorDefault"), LuiStyle.BgDefault);
	}
}

// A handle shown as text: the top bar's label (LabelText) or a display handle (Label, with HotkeyLabel_Text as a
// small caption above it, as the left bar's task count).
internal class LuaTextView : LuaElementView
{
	private Text _text;

	private Text _caption;

	private string _field;

	public static LuaTextView Create(Transform parent, Table handle, string field, int fontSize, float width, float height)
	{
		RectTransform rect = UiKit.CreateRect("Text", parent);
		UiKit.Fixed(rect.gameObject, width, height);
		LuaTextView view = rect.gameObject.AddComponent<LuaTextView>();
		view._field = field;
		view._text = LuiWidgets.Label(rect, string.Empty, fontSize, LuiStyle.CastleYellow, TextAnchor.MiddleCenter, LuiStyle.Title);
		UiKit.SetRect(view._text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		view._text.horizontalOverflow = HorizontalWrapMode.Wrap;
		if (LuaBridge.Bool(LuaBridge.Get(handle, "hasHotkey"), false))
		{
			view._caption = LuiWidgets.Label(rect, string.Empty, LuiStyle.SmallFontSize, LuiStyle.CastleYellow, TextAnchor.UpperCenter, LuiStyle.Regular);
			UiKit.SetRect(view._caption.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -4f));
		}
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		if (!Visible(true))
		{
			return;
		}
		_text.text = LuaBridge.String(LuaBridge.Value(Handle, _field), string.Empty);
		_text.color = LuaBridge.Color(LuaBridge.Value(Handle, "LabelColor"), LuiStyle.CastleYellow);
		if (_caption != null)
		{
			_caption.text = LuaBridge.String(LuaBridge.Value(Handle, "HotkeyLabel_Text"), string.Empty);
		}
	}
}

// An alert row of a sidebar: the handle's icon and its message (Count), as the new-task sidebar's "too many
// tasks" warning.
internal class LuaAlertView : LuaElementView
{
	private Image _icon;

	private Text _text;

	public static LuaAlertView Create(Transform parent, Table handle)
	{
		RectTransform row = (RectTransform)UiKit.CreateRow(parent, LuaBridge.Number(LuaBridge.Value(handle, "Height"), 56f));
		LuaAlertView view = row.gameObject.AddComponent<LuaAlertView>();
		RectTransform icon = UiKit.CreateRect("Icon", row);
		UiKit.Fixed(icon.gameObject, 32f, 32f);
		view._icon = icon.gameObject.AddComponent<Image>();
		view._icon.preserveAspect = true;
		view._icon.raycastTarget = false;
		view._text = LuiWidgets.Label(row, string.Empty, LuiStyle.FontSize, LuiStyle.Gray, TextAnchor.MiddleLeft, LuiStyle.Regular);
		view._text.horizontalOverflow = HorizontalWrapMode.Wrap;
		UiKit.Flexible(view._text.gameObject);
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		if (!Visible(true))
		{
			return;
		}
		Sprite sprite = LuaBridge.Sprite(LuaBridge.Value(Handle, "Icon"));
		_icon.sprite = sprite;
		_icon.enabled = sprite != null;
		_icon.color = LuaBridge.Color(LuaBridge.Value(Handle, "IconColor"), LuiStyle.CastleRed);
		_text.text = LuaBridge.String(LuaBridge.Value(Handle, "Count"), string.Empty);
		_text.color = LuaBridge.Color(LuaBridge.Value(Handle, "CountColor"), LuiStyle.Gray);
	}
}

// Game state the C# menus ask the game's Lua for, the way GameMenu's own panels do.
internal static class LuaUiGame
{
	// Data.Project:IsSelectedNotOfTypeIdle(): a task other than the idle group is selected.
	public static bool SelectedTaskNotIdle()
	{
		Script script = GameMenuLink.Script;
		DynValue data = (script != null) ? script.Globals.Get("Data") : DynValue.Nil;
		Table project = (data.Type == DataType.Table) ? LuaBridge.GetTable(data.Table, "Project") : null;
		return project != null && LuaBridge.Bool(LuaBridge.CallMethod(project, "IsSelectedNotOfTypeIdle"), false);
	}
}
