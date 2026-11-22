using System;
using CastleStoryPlus.Core;
using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.LuaUi;
using MoonSharp.Interpreter;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The in-game menu (GameMenu.lua's handles, drawn by the C# menus): its menu handles with their label, whether
// they are open and whether a C# button draws them, the C# sidebars, and pressing a handle's button as a click
// does, so the menus can be tested without a mouse.
internal static class MenuEndpoints
{
	private static readonly string[] Kinds = new string[] { "mh", "bh" };

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/menu", "The in-game menu: its menu handles (_m.mh: label, open, drawn by a C# button and whether it shows), the C# sidebars (new task, structures, blocks, settings) and whether each is shown", Get)
		{
			Parameters = "q (part of a handle's name or label), kind (mh or bh; default mh)"
		});
		routes.Add(new Route("POST", "/api/menu/press", "Presses a handle's button as a click does (its C# button when there is one, else the handle's OnAction or the toggle of its group), e.g. buildStructures, newTask, resourceList, settings", Press)
		{
			Parameters = "handle (name in _m.mh or _m.bh, required), kind (mh or bh; default: whichever has it)"
		});
		routes.Add(new Route("POST", "/api/ui/reload", "Reloads the in-game menu's code (CastleStoryPlus.UI.dll, the newest build in the plugin folder) and builds the menu again if a game is going; no game restart. Patches and the rest of the mod are not reloaded.", ReloadUi));
		routes.Add(new Route("POST", "/api/mod/reload", "Loads the newest build of the mod (CastleStoryPlus.dll in the plugin folder) into the running game, no restart: the running copy's patches are removed, its features' Disable() run and its components on the plugin object destroyed, an open in-game menu is built again from the new copy. Its other components and event handlers stay, and DevTools and the tests stay bound to the startup copy. For trying patch changes; restart for anything else", ReloadMod));
	}

	private static object ReloadMod(ApiRequest request)
	{
		try
		{
			return new Dictionary<string, object> { { "reloaded", ModReload.Reload() } };
		}
		catch (Exception ex)
		{
			throw new ApiException(500, "Reload failed: " + (ex.InnerException ?? ex).Message);
		}
	}

	private static object ReloadUi(ApiRequest request)
	{
		if (!LuaUiConfig.CsGameMenu)
		{
			throw new ApiException(409, "The C# in-game menu is off ([LuaUi] CsGameMenu); there is no UI assembly to reload");
		}
		if (ModReload.Reloads > 0)
		{
			// The mod was reloaded: the menu belongs to its newest copy.
			return new Dictionary<string, object> { { "loaded", ModReload.CallCurrent("CastleStoryPlus.GameUi.UiAssembly", "Reload") } };
		}
		bool loaded = CastleStoryPlus.GameUi.UiAssembly.Reload();
		return new Dictionary<string, object>
		{
			{ "loaded", loaded },
			{ "loads", CastleStoryPlus.GameUi.UiAssembly.Loads },
			{ "built", CastleStoryPlus.GameUi.UiAssembly.LoadedAt.ToString("yyyy-MM-dd HH:mm:ss") },
			{ "menuOpen", CastleStoryPlus.GameUi.CsGameMenu.Model != null }
		};
	}

	private static void NeedMenu()
	{
		if (GameMenuLink.Menu == null)
		{
			throw new ApiException(409, "No in-game menu handed to the C# menus (no game in progress, [LuaUi] menus off, or GameMenu did not finish building)");
		}
	}

	private static object Get(ApiRequest request)
	{
		if (LuaUiConfig.CsGameMenu)
		{
			return CsMenu.Get(request);
		}
		NeedMenu();
		string q = request.String("q", null);
		string kind = request.String("kind", "mh");
		Table handles = LuaBridge.GetTable(GameMenuLink.Menu, kind);
		if (handles == null)
		{
			throw new ApiException(400, "No handles of kind '" + kind + "'");
		}
		Dictionary<Table, LuaHandleView> buttons = Buttons();
		List<object> list = new List<object>();
		foreach (TablePair pair in handles.Pairs)
		{
			if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Table)
			{
				continue;
			}
			string name = pair.Key.String;
			Table handle = pair.Value.Table;
			string label = LuaBridge.String(LuaBridge.Value(handle, "Label"), string.Empty);
			if (!string.IsNullOrEmpty(q) && name.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			list.Add(Describe(name, handle, buttons));
		}
		List<object> sidebars = new List<object>();
		// Hidden sidebars are inactive, which FindObjectsOfType skips.
		foreach (LuaSidebarView sidebar in Resources.FindObjectsOfTypeAll<LuaSidebarView>())
		{
			if (sidebar == null || !sidebar.gameObject.scene.IsValid())
			{
				continue;
			}
			sidebars.Add(Obj("label", LuaBridge.String(LuaBridge.Value(sidebar.Handle, "Label"), null), "shown", sidebar.Shown, "active", sidebar.gameObject.activeInHierarchy));
		}
#if !ESSENTIALS
		return Obj("kind", kind, "count", list.Count, "handles", list, "sidebars", sidebars, "resourceListShown", CastleStoryPlus.UI.ResourceList.Shown);
#else
		return Obj("kind", kind, "count", list.Count, "handles", list, "sidebars", sidebars, "resourceListShown", false);
#endif
	}

	private static object Press(ApiRequest request)
	{
		if (LuaUiConfig.CsGameMenu)
		{
			return CsMenu.Press(request);
		}
		NeedMenu();
		string name = request.Required("handle");
		string kind = request.String("kind", null);
		Table handle = null;
		foreach (string k in (kind != null) ? new string[] { kind } : Kinds)
		{
			handle = GameMenuLink.Handle(k, name);
			if (handle != null)
			{
				kind = k;
				break;
			}
		}
		if (handle == null)
		{
			throw new ApiException(404, "No handle '" + name + "' in _m.mh or _m.bh");
		}
		Dictionary<Table, LuaHandleView> buttons = Buttons();
		string by;
		if (buttons.TryGetValue(handle, out LuaHandleView view))
		{
			view.Act();
			by = "C# button";
		}
		else
		{
			DynValue action = LuaBridge.Get(handle, "OnAction");
			if (!action.IsNil())
			{
				LuaBridge.Call(action, "OnAction");
				by = "OnAction (no C# button draws it)";
			}
			else if (LuaBridge.Get(handle, "parent").Type == DataType.Table)
			{
				LuaBridge.CallMethod(handle, "Toggle");
				by = "Toggle (no C# button draws it)";
			}
			else
			{
				throw new ApiException(409, "Handle '" + name + "' has neither a C# button, an OnAction nor a group to toggle in");
			}
		}
		Dictionary<string, object> result = Describe(name, handle, Buttons());
		result["kind"] = kind;
		result["pressedBy"] = by;
		return result;
	}

	// The C# buttons by the handle they draw.
	private static Dictionary<Table, LuaHandleView> Buttons()
	{
		Dictionary<Table, LuaHandleView> result = new Dictionary<Table, LuaHandleView>();
		foreach (LuaHandleView view in Resources.FindObjectsOfTypeAll<LuaHandleView>())
		{
			if (view != null && view.Handle != null && view.gameObject.scene.IsValid() && !result.ContainsKey(view.Handle))
			{
				result[view.Handle] = view;
			}
		}
		return result;
	}

	private static Dictionary<string, object> Describe(string name, Table handle, Dictionary<Table, LuaHandleView> buttons)
	{
		bool grouped = LuaBridge.Get(handle, "parent").Type == DataType.Table;
		bool open = grouped && LuaBridge.Bool(LuaBridge.CallMethod(handle, "IsOpened"), false);
		bool drawn = buttons.TryGetValue(handle, out LuaHandleView view);
		return Obj(
			"name", name,
			"label", LuaBridge.String(LuaBridge.Value(handle, "Label"), null),
			"visible", LuaBridge.Bool(LuaBridge.Value(handle, "Visible"), true),
			"inGroup", grouped,
			"open", open,
			"hasOnAction", !LuaBridge.Get(handle, "OnAction").IsNil(),
			"button", drawn ? (object)Obj("shown", view.Shown, "active", view.gameObject.activeInHierarchy) : null);
	}
}

// The same for the C# in-game menu ([LuaUi] CsGameMenu): its items by id, from the bars, the sidebars and the task
// popup, with their label and whether they show; pressing one clicks it (a sidebar's id that no item has toggles the sidebar).
internal static class CsMenu
{
	private static CastleStoryPlus.GameUi.GameMenuModel Need()
	{
		CastleStoryPlus.GameUi.GameMenuModel model = CastleStoryPlus.GameUi.CsGameMenu.Model;
		if (model == null)
		{
			throw new ApiException(409, "No C# in-game menu (no game in progress, or it is still loading)");
		}
		return model;
	}

	private static IEnumerable<KeyValuePair<string, CastleStoryPlus.GameUi.UiItem>> Items(CastleStoryPlus.GameUi.GameMenuModel model)
	{
		foreach (CastleStoryPlus.GameUi.UiItem item in model.LeftBar)
		{
			if (item != null)
			{
				yield return new KeyValuePair<string, CastleStoryPlus.GameUi.UiItem>("leftBar", item);
			}
		}
		foreach (CastleStoryPlus.GameUi.UiItem item in model.RightBar)
		{
			if (item != null)
			{
				yield return new KeyValuePair<string, CastleStoryPlus.GameUi.UiItem>("rightBar", item);
			}
		}
		foreach (CastleStoryPlus.GameUi.UiPanel panel in Panels(model))
		{
			foreach (CastleStoryPlus.GameUi.UiSection section in panel.Sections)
			{
				foreach (CastleStoryPlus.GameUi.UiItem item in section.Items)
				{
					yield return new KeyValuePair<string, CastleStoryPlus.GameUi.UiItem>(panel.Id + "/" + section.Id, item);
				}
			}
		}
	}

	private static List<CastleStoryPlus.GameUi.UiPanel> Panels(CastleStoryPlus.GameUi.GameMenuModel model)
	{
		List<CastleStoryPlus.GameUi.UiPanel> panels = new List<CastleStoryPlus.GameUi.UiPanel> { model.NewTask, model.Structures, model.Blocks, model.Blueprints, model.Settings, model.Help, model.Debug, model.Popup };
		panels.AddRange(model.ExtraPanels);
		return panels;
	}

	internal static object Get(ApiRequest request)
	{
		CastleStoryPlus.GameUi.GameMenuModel model = Need();
		string q = request.String("q", null);
		List<object> list = new List<object>();
		foreach (KeyValuePair<string, CastleStoryPlus.GameUi.UiItem> pair in Items(model))
		{
			string label = Label(pair.Value);
			if (!string.IsNullOrEmpty(q) && (pair.Value.Id ?? string.Empty).IndexOf(q, System.StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			list.Add(Obj("id", pair.Value.Id, "in", pair.Key, "type", pair.Value.GetType().Name, "label", label, "visible", Safe(pair.Value.IsVisible)));
		}
		List<object> sidebars = new List<object>();
		foreach (CastleStoryPlus.GameUi.UiPanel panel in Panels(model))
		{
			if (panel != model.Popup)
			{
				sidebars.Add(Obj("id", panel.Id, "open", panel.IsOpen));
			}
		}
#if !ESSENTIALS
		return Obj("menu", "C#", "count", list.Count, "items", list, "sidebars", sidebars, "resourceListShown", CastleStoryPlus.UI.ResourceList.Shown);
#else
		return Obj("menu", "C#", "count", list.Count, "items", list, "sidebars", sidebars, "resourceListShown", false);
#endif
	}

	internal static object Press(ApiRequest request)
	{
		CastleStoryPlus.GameUi.GameMenuModel model = Need();
		string id = request.Required("handle");
		foreach (KeyValuePair<string, CastleStoryPlus.GameUi.UiItem> pair in Items(model))
		{
			if (pair.Value.Id != id)
			{
				continue;
			}
			if (pair.Value is CastleStoryPlus.GameUi.UiButton button)
			{
				button.Click(false, false);
				CastleStoryPlus.GameUi.UiAssembly.MarkAll?.Invoke();
				return Obj("id", id, "in", pair.Key, "pressedBy", "C# button", "label", Label(button));
			}
			if (pair.Value is CastleStoryPlus.GameUi.UiRow row && row.OnClick != null)
			{
				row.OnClick();
				CastleStoryPlus.GameUi.GameUiState.Announce();
				return Obj("id", id, "in", pair.Key, "pressedBy", "C# row");
			}
			throw new ApiException(409, "Item '" + id + "' is a " + pair.Value.GetType().Name + ", which cannot be pressed");
		}
		foreach (CastleStoryPlus.GameUi.UiPanel panel in Panels(model))
		{
			if (panel.Id == id && panel != model.Popup)
			{
				panel.Toggle();
				return Obj("id", id, "pressedBy", "sidebar toggle", "open", panel.IsOpen);
			}
		}
		throw new ApiException(404, "No item or sidebar '" + id + "' in the C# menu");
	}

	private static string Label(CastleStoryPlus.GameUi.UiItem item)
	{
		try
		{
			switch (item)
			{
				case CastleStoryPlus.GameUi.UiButton button:
					return button.LabelText();
				case CastleStoryPlus.GameUi.UiStepper stepper:
					return (stepper.Label != null) ? stepper.Label() : string.Empty;
				case CastleStoryPlus.GameUi.UiText text:
					return (text.Text != null) ? text.Text() : string.Empty;
				case CastleStoryPlus.GameUi.UiRow row:
					return (row.Text != null) ? row.Text() : string.Empty;
				default:
					return string.Empty;
			}
		}
		catch (System.Exception ex)
		{
			return "(error: " + ex.Message + ")";
		}
	}

	private static object Safe(System.Func<bool> read)
	{
		try
		{
			return read();
		}
		catch (System.Exception ex)
		{
			return "error: " + ex.Message;
		}
	}
}
