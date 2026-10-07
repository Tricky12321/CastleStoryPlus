using System;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.LuaUi;

// The C# menus' way into the game's in-game menu (LUI/Menus/GameMenu.lua). The game keeps the logic of that menu
// in plain Lua tables: menu groups (_m.mg.*) and handles (_m.mh, _m.bh, _m.sh: label, icon, OnAction... as
// closures), which the hotkeys, the tutorial and the mod's own Lua additions use. What takes long to build when a
// map loads are the views drawn from them. So a C# menu keeps those tables and replaces only a view: GameMenu
// hands its table to C# (AttachGameMenu, just before it initialises), a view's block in GameMenu is cut out
// (ReplaceMenuBlock) and the menu field it filled gets a stub, and a C# view draws from the handles instead.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class GameMenuLink
{
	// GameMenu's _m table and its script, while the game's in-game menu exists.
	public static Table Menu { get; private set; }

	public static Script Script { get; private set; }

	// After GameMenu handed over its table, and when that table goes (game left, menu rebuilt).
	public static event Action Attached;

	public static event Action Detached;

	private static void Enable()
	{
		if (!LuaUiConfig.Any)
		{
			return;
		}
		// A stub for a menu whose view was cut out: every method answers the stub again, so chains like
		// _m.menu_popup.AddSection(...).AddDivider() and the game's and mods' calls on it still run, doing nothing.
		LuaInjection.AddFunction("MenuStub", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			return DynValue.NewTable(Stub(context.OwnerScript));
		});
		if (!LuaUiConfig.AnyGameMenu)
		{
			return;
		}
		LuaInjection.AddFunction("AttachGameMenu", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count > 0 && args[0].Type == DataType.Table)
			{
				Attach(args[0].Table, context.OwnerScript);
			}
			return DynValue.Void;
		});
		// The C# sidebars cut their .AddMenuHandleSocket(...) calls out of these chains; with all of them cut the
		// chain's first line would stand alone, which Lua does not allow as a statement. Assigned, it always parses.
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", "\t_m.menu_leftContent\n", LuaInjection.Mode.Replace, "\tlocal _ = _m.menu_leftContent\n");
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", "\t_m.menu_rightContent\n", LuaInjection.Mode.Replace, "\tlocal _ = _m.menu_rightContent\n");
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", "--init\nData.Project:OnSetSelected()", LuaInjection.Mode.InsertBefore, "--C# menus (Castle Story Plus)\nCastleStoryPlus.AttachGameMenu(_m)\n\n");
		GameSession.OnLeave(Detach);
	}

	// Cuts a view's block out of GameMenu.lua and puts a stub in its menu field. A block in GameMenu reads
	//   ---lyt: top bar
	//   do
	//   local m =
	//   	dofile("LUI/Menus/Game/TopBarMenu.lua")
	//   ...
	//   _m.menu_top = m
	//   end
	// so it is found by its comment line (start, e.g. "---lyt: top bar\ndo\n") and the line that stores the menu
	// (field "menu_top"): from the comment to that line's "end". Patches other features insert after the block
	// still apply (range patches go last); the C# view takes over what the block's panel did.
	public static void ReplaceMenuBlock(string owner, string start, string field)
	{
		ReplaceMenuBlock(owner, "LUI/Menus/GameMenu.lua", start, field);
	}

	public static void ReplaceMenuBlock(string owner, string file, string start, string field)
	{
		ReplaceMenuBlock(owner, file, start, field, "CastleStoryPlus.MenuStub()");
	}

	// As above, with the Lua expression the menu field gets instead of the stub (a LuaMenuRecorder, for a view
	// that is drawn from what the game later adds to the menu).
	public static void ReplaceMenuBlock(string owner, string file, string start, string field, string expression)
	{
		string end = "_m." + field + " = m\nend\n";
		LuaInjection.AddRangePatch(owner, file, start, end, "---" + field + ": drawn in C# (Castle Story Plus)\n_m." + field + " = " + expression + "\n");
	}

	// Lua stub table: indexing anything answers a function that answers the stub.
	public static Table Stub(Script script)
	{
		Table stub = new Table(script);
		Table meta = new Table(script);
		DynValue self = DynValue.NewTable(stub);
		DynValue method = DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) => self);
		meta.Set("__index", DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) => method));
		stub.MetaTable = meta;
		return stub;
	}

	private static void Attach(Table menu, Script script)
	{
		Detach();
		Menu = menu;
		Script = script;
		if (script != null)
		{
			script.OnScriptKilled += OnScriptKilled;
		}
		Plugin.Log.LogInfo("CsMenus: the in-game menu's tables are handed to C#");
		// Told a frame later, outside the Lua call: views read the handles' closures as they are made, and those
		// should not run in the middle of GameMenu's own script.
		LuiCanvas.Instance.Tick += AnnounceAttached;
	}

	private static void AnnounceAttached()
	{
		LuiCanvas.Instance.Tick -= AnnounceAttached;
		if (Menu != null)
		{
			Attached?.Invoke();
		}
	}

	private static void OnScriptKilled(Script script)
	{
		if (script == Script)
		{
			Detach();
		}
	}

	private static void Detach()
	{
		if (Menu == null)
		{
			return;
		}
		if (Script != null)
		{
			Script.OnScriptKilled -= OnScriptKilled;
		}
		Menu = null;
		Script = null;
		Detached?.Invoke();
	}

	// _m.mg.<name>: a menu group (left, right, top, projectContext, newTask, settings...).
	public static Table Group(string name)
	{
		Table groups = (Menu != null) ? LuaBridge.GetTable(Menu, "mg") : null;
		return (groups != null) ? LuaBridge.GetTable(groups, name) : null;
	}

	// _m.<kind>.<name>: a handle, kind "mh" (menu), "bh" (button), "sh" (section) or "dh" (display).
	public static Table Handle(string kind, string name)
	{
		Table handles = (Menu != null) ? LuaBridge.GetTable(Menu, kind) : null;
		return (handles != null) ? LuaBridge.GetTable(handles, name) : null;
	}

	// A group's handles in their order (MenuGroup keeps them in its array part).
	public static Table[] Children(Table group)
	{
		Table children = (group != null) ? LuaBridge.GetTable(group, "children") : null;
		if (children == null)
		{
			return new Table[0];
		}
		int count = children.Length;
		Table[] result = new Table[count];
		int found = 0;
		for (int i = 1; i <= count; i++)
		{
			DynValue child = children.Get(i);
			if (child.Type == DataType.Table)
			{
				result[found++] = child.Table;
			}
		}
		if (found < count)
		{
			Array.Resize(ref result, found);
		}
		return result;
	}
}
