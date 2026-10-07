using System;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsBlueprintLibrary: the blueprint library (GameMenu's blueprintsMenu handle: a name field with a save
// button, load, publish to the Workshop, delete, and the list of saved blueprints with their thumbnails) drawn in
// C# beside the left bar, like the other sidebars (LuaSidebarView): its socket is taken out of the left content
// overlay, so its SidebarFloatingMenu is never made, and its onLoad listeners fill a recorder instead. The name
// field and the saved blueprints' rows are drawn by LuaInputFieldView and LuaCompoundView.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsBlueprintLibrary
{
	private static GameObject _view;

	private static void Enable()
	{
		if (!LuaUiConfig.CsBlueprintLibrary)
		{
			return;
		}
		LuaMenuRecorder.Register();
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", ".AddMenuHandleSocket(_m.mh.blueprintsMenu)", LuaInjection.Mode.Replace, string.Empty);
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	private static void Show()
	{
		Hide();
		Table handle = GameMenuLink.Handle("mh", "blueprintsMenu");
		if (handle == null)
		{
			Plugin.Log.LogWarning("CsMenus: no blueprintsMenu handle in GameMenu");
			return;
		}
		_view = LuaSidebarView.Create(LuiCanvas.Instance.Menus, handle, rightSide: false).gameObject;
		Plugin.Log.LogInfo("CsMenus: the blueprint library is drawn in C#");
	}

	private static void Hide()
	{
		if (_view != null)
		{
			UnityEngine.Object.Destroy(_view);
		}
		_view = null;
	}

	// Calls one of the game's Lua globals' functions (BlueprintSelection.GetCurrentName...). Most are C# functions
	// given to Lua, which need the script to call them.
	internal static DynValue CallGlobal(Script script, string global, string name, params DynValue[] args)
	{
		if (script == null)
		{
			return DynValue.Nil;
		}
		DynValue owner = script.Globals.Get(global);
		if (owner.Type != DataType.Table)
		{
			return DynValue.Nil;
		}
		DynValue function = owner.Table.Get(name);
		if (function.Type != DataType.Function && function.Type != DataType.ClrFunction)
		{
			return DynValue.Nil;
		}
		try
		{
			DynValue result = script.Call(function, args);
			return (result.Type == DataType.Tuple) ? ((result.Tuple.Length > 0) ? result.Tuple[0] : DynValue.Nil) : result;
		}
		catch (InterpreterException ex)
		{
			LuaBridge.LogOnce(global + "." + name, ex.DecoratedMessage ?? ex.Message);
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce(global + "." + name, ex.Message);
		}
		return DynValue.Nil;
	}
}

// The library's name field (SidebarFloatingMenu.AddMenuHandleInputField): the name of the blueprints selected in
// the world (BlueprintSelection.GetCurrentName/SetCurrentName) and the handle's save button beside it. While the
// field is being typed in, the game's key bindings are off (as its own input fields do: LUI2.SetKeyBindings(false)
// and InputModeController.ChangeInputToText), so typing a name does not rotate the camera or open menus.
internal class LuaInputFieldView : LuaElementView
{
	private InputField _field;

	private LuiIconButton _save;

	private bool _typing;

	public static LuaInputFieldView Create(Transform parent, Table handle)
	{
		Transform row = UiKit.CreateRow(parent, 48f);
		HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(0, 0, 8, 8);
		layout.spacing = 4f;
		LuaInputFieldView view = row.gameObject.AddComponent<LuaInputFieldView>();
		view._field = UiKit.CreateInputField(row, string.Empty, 200f);
		view._field.textComponent.font = LuiStyle.Regular;
		UiKit.Flexible(view._field.gameObject);
		view._field.onEndEdit.AddListener(view.Commit);
		LuaInputFieldFocus focus = view._field.gameObject.AddComponent<LuaInputFieldFocus>();
		focus.Changed = view.SetTyping;
		view._save = LuiWidgets.IconButton(row, 32f);
		view._save.SetIcon(Brix.UI.Icons.IconKeys.SaveBlueprints.Get64());
		view._save.SetIconSize(32f);
		view._save.OnClick = (bool shift, bool ctrl) => view.Save();
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		// The game shows it from the start of the library's list (its Visible closure is only given by the Lua
		// panel, which is not made here).
		if (!Visible(true))
		{
			return;
		}
		_save.SetIconColor(LuaBridge.Color(LuaBridge.Value(Handle, "IconColor"), LuiStyle.CastleYellow));
		if (!_typing)
		{
			string name = LuaBridge.String(CsBlueprintLibrary.CallGlobal(Handle.OwnerScript, "BlueprintSelection", "GetCurrentName"), string.Empty);
			if (_field.text != name)
			{
				_field.text = name;
			}
		}
	}

	private void Commit(string text)
	{
		if (Handle != null)
		{
			CsBlueprintLibrary.CallGlobal(Handle.OwnerScript, "BlueprintSelection", "SetCurrentName", DynValue.NewString(text ?? string.Empty));
		}
		MarkDirty();
	}

	private void Save()
	{
		if (Handle == null)
		{
			return;
		}
		// A name typed but not yet confirmed counts.
		if (_typing)
		{
			Commit(_field.text);
		}
		DynValue action = LuaBridge.Get(Handle, "OnAction");
		if (!action.IsNil())
		{
			LuaBridge.Call(action, "blueprint library save");
		}
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
		// Hidden or destroyed while typing (the library closed): the keys must come back.
		SetTyping(false);
	}
}

// Tells the name field's view when the field gets and loses the keyboard.
internal class LuaInputFieldFocus : MonoBehaviour, ISelectHandler, IDeselectHandler
{
	public Action<bool> Changed;

	public void OnSelect(BaseEventData eventData)
	{
		if (Changed != null)
		{
			Changed(true);
		}
	}

	public void OnDeselect(BaseEventData eventData)
	{
		if (Changed != null)
		{
			Changed(false);
		}
	}
}

// One saved blueprint in the library's list (SidebarFloatingMenu.AddCompoundHandleToggle): its thumbnail on the
// left, its name beside it, framed while it is the selected one; a click selects it (the handle's OnAction). The
// handle's Visible says whether that slot of the list holds a blueprint.
internal class LuaCompoundView : LuaElementView, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
{
	private Image _background;

	private Image _thumbnail;

	private Text _name;

	private GameObject _highlight;

	public static LuaCompoundView Create(Transform parent, Table handle)
	{
		RectTransform rect = UiKit.CreateRect("Compound", parent);
		LuaCompoundView view = rect.gameObject.AddComponent<LuaCompoundView>();
		view._background = rect.gameObject.AddComponent<Image>();
		LuiStyle.Paint(view._background, LuiStyle.ButtonSprite, LuiStyle.BgDefault);
		view._highlight = LuiWidgets.Frame(rect, LuiStyle.Highlight, 2f);
		RectTransform thumbnail = UiKit.CreateRect("Thumbnail", rect);
		thumbnail.anchorMin = thumbnail.anchorMax = new Vector2(0f, 0.5f);
		thumbnail.pivot = new Vector2(0f, 0.5f);
		thumbnail.anchoredPosition = new Vector2(4f, 0f);
		thumbnail.sizeDelta = new Vector2(60f, 36f);
		view._thumbnail = thumbnail.gameObject.AddComponent<Image>();
		view._thumbnail.preserveAspect = true;
		view._thumbnail.raycastTarget = false;
		view._thumbnail.enabled = false;
		view._name = LuiWidgets.Label(rect, string.Empty, LuiStyle.FontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._name.raycastTarget = false;
		UiKit.SetRect(view._name.rectTransform, Vector2.zero, Vector2.one, new Vector2(35f, 0f), new Vector2(-78f, 0f));
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		if (!Visible(false))
		{
			return;
		}
		Sprite sprite = LuaBridge.Sprite(LuaBridge.Value(Handle, "Icon"));
		if (_thumbnail.sprite != sprite)
		{
			_thumbnail.sprite = sprite;
		}
		_thumbnail.enabled = sprite != null;
		_thumbnail.color = LuaBridge.Color(LuaBridge.Value(Handle, "IconColor"), LuiStyle.White);
		string name = LuaBridge.String(LuaBridge.Value(Handle, "Count"), string.Empty);
		if (_name.text != name)
		{
			_name.text = name;
		}
		bool highlight = LuaHandleView.IsHighlighted(LuaBridge.Value(Handle, "Highlight"));
		if (_highlight.activeSelf != highlight)
		{
			_highlight.SetActive(highlight);
			_highlight.transform.SetAsLastSibling();
		}
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || Handle == null)
		{
			return;
		}
		DynValue action = LuaBridge.Get(Handle, "OnAction");
		if (!action.IsNil())
		{
			LuaBridge.Call(action, "blueprint library select");
		}
		MarkDirty();
	}

	public void OnPointerEnter(PointerEventData eventData)
	{
		_background.color = LuiStyle.FgHover;
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		_background.color = LuiStyle.BgDefault;
	}
}
