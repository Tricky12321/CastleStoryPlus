using CastleStoryPlus.Core;
using CastleStoryPlus.LuaUi;

namespace CastleStoryPlus.GameUi;

// The sandbox tutorial (LTD.lua: its objectives and step tooltips) is gameplay in Lua, which stays Lua for now.
// GameMenu.lua loaded it in a sandbox game and connected it while open; with GameMenu not loaded, the sandbox's
// own menu (its scoreboard, SandboxMenu.lua, loaded in every sandbox game) does that instead.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class GameTutorial
{
	private const string File = "Gamemodes/sandbox/Menu/SandboxMenu.lua";

	private static void Enable()
	{
		if (!LuaUiConfig.CsGameMenu)
		{
			return;
		}
		LuaInjection.AddPatch(Features.CsMenus, File, "require(\"LUI2.lua\")\n", LuaInjection.Mode.InsertAfter,
			"require(\"LTD.lua\") -- the sandbox tutorial, once loaded by GameMenu.lua (Castle Story Plus)\n");
		LuaInjection.AddPatch(Features.CsMenus, File, "local OnOpen =\n\tfunction()\n", LuaInjection.Mode.InsertAfter,
			"\t\tif ConnectLTD and not _menu.ltdConnected then _menu.ltdConnected = true ConnectLTD() end\n");
		LuaInjection.AddPatch(Features.CsMenus, File, "local OnClose =\n\tfunction()\n", LuaInjection.Mode.InsertAfter,
			"\t\tif DisconnectLTD and _menu.ltdConnected then _menu.ltdConnected = false DisconnectLTD() end\n");
	}

	// With the C# gamemode panel (CsScoreboard) SandboxMenu.lua is not loaded: the panel loads and connects the
	// tutorial in the sandbox script itself while it is shown. Whether it was connected.
	internal static bool Connect(MoonSharp.Interpreter.Script script)
	{
		if (!LuaUiConfig.CsGameMenu || script == null)
		{
			return false;
		}
		return Run(script, "require(\"LTD.lua\") if ConnectLTD and not CastleStoryPlus_LtdConnected then CastleStoryPlus_LtdConnected = true ConnectLTD() end");
	}

	internal static void Disconnect(MoonSharp.Interpreter.Script script)
	{
		if (script != null)
		{
			Run(script, "if DisconnectLTD and CastleStoryPlus_LtdConnected then CastleStoryPlus_LtdConnected = false DisconnectLTD() end");
		}
	}

	private static bool Run(MoonSharp.Interpreter.Script script, string code)
	{
		try
		{
			script.DoString(code);
			return true;
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("GameTutorial: " + ex.Message);
			return false;
		}
	}
}
